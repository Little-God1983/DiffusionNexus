using Microsoft.ML.OnnxRuntime;
using Serilog;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// Owns the full lifecycle of one ONNX <see cref="InferenceSession"/>:
/// provider selection (DirectML with CPU fallback), lazy double-checked
/// initialization, permanent GPU demotion after a failed GPU inference,
/// single-flight processing admission, and disposal that waits out any
/// in-flight inference. <see cref="BackgroundRemovalService"/>,
/// <see cref="ImageUpscalingService"/> and <see cref="ImageTaggingService"/>
/// each used to carry a hand-rolled copy of all of this; the copies had
/// already drifted (missing DirectML compatibility flags, missing CPU tuning,
/// leaked sessions on metadata-discovery failures) and every provider fix had
/// to be found and applied three times.
/// </summary>
/// <remarks>
/// Threading model:
/// <list type="bullet">
/// <item><see cref="_sessionLock"/> guards every touch of <see cref="_session"/> —
/// creation, inference, demotion and disposal. Holding it across
/// <see cref="Run{T}"/> (including result read-out, whose tensors are native
/// memory owned by the result set) is what makes <see cref="Dispose"/> safe to
/// call while an inference is in flight: it blocks until the run finishes
/// instead of freeing the native session under it, which would be an
/// uncatchable <c>AccessViolationException</c> at best.</item>
/// <item><see cref="_processing"/> is a separate, non-blocking admission gate:
/// callers that lose <see cref="TryBeginProcessing"/> get an immediate
/// "already processing" answer instead of queueing on the session lock. It is
/// an <see cref="Interlocked"/> exchange, not a bool — the services' old plain
/// <c>_isProcessing</c> check-then-set let two concurrent callers both pass
/// and then fail unpredictably inside the session.</item>
/// </list>
/// </remarks>
public sealed class OnnxSessionHost : IDisposable
{
    private readonly string _modelDescription;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private InferenceSession? _session;
    private bool _isGpuAvailable;
    private bool _disableGpu;
    private volatile bool _disposed;
    private int _processing;

    /// <param name="modelDescription">
    /// Short human-readable model name used in log messages, e.g. "RMBG-1.4".
    /// </param>
    public OnnxSessionHost(string modelDescription)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDescription);
        _modelDescription = modelDescription;
    }

    public bool IsGpuAvailable => _isGpuAvailable;
    public bool IsProcessing => Volatile.Read(ref _processing) == 1;
    public bool IsDisposed => _disposed;
    public bool IsInitialized => _session is not null;

    /// <summary>
    /// True when a failed GPU inference can still be retried by demoting to
    /// CPU — i.e. the current session actually runs on the GPU and no earlier
    /// failure has already forced CPU-only mode. Mirrors the services'
    /// <c>when (_isGpuAvailable &amp;&amp; !_disableGpu)</c> catch filters.
    /// </summary>
    public bool CanRetryOnCpu => _isGpuAvailable && !_disableGpu;

    /// <summary>
    /// Atomically claims the single-flight processing slot. A <c>false</c>
    /// return means another inference is already running and the caller should
    /// answer "busy" immediately. Pair every successful claim with
    /// <see cref="EndProcessing"/> in a <c>finally</c>.
    /// </summary>
    public bool TryBeginProcessing() => Interlocked.CompareExchange(ref _processing, 1, 0) == 0;

    public void EndProcessing() => Volatile.Write(ref _processing, 0);

    /// <summary>
    /// Creates the session if it does not exist yet. Returns false when
    /// creation failed (missing model file, unloadable model, metadata
    /// rejected by <paramref name="inspectModel"/>) — never throws for those.
    /// </summary>
    /// <param name="inspectModel">
    /// Runs against the freshly created session, still under the session lock,
    /// so the owner can read tensor names/shapes. Throwing rejects the
    /// session: it is disposed, never published, and on the DirectML attempt
    /// the CPU fallback is tried next.
    /// </param>
    public async Task<bool> InitializeAsync(
        string modelPath,
        Action<InferenceSession>? inspectModel = null,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return false;
        if (_session is not null)
            return true;

        await _sessionLock.WaitAsync(cancellationToken);
        try
        {
            if (_disposed)
                return false;
            if (_session is not null)
                return true;

            _session = await Task.Run(() => CreateSession(modelPath, inspectModel), cancellationToken);
            return _session is not null;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private InferenceSession? CreateSession(string modelPath, Action<InferenceSession>? inspectModel)
    {
        if (!File.Exists(modelPath))
        {
            Log.Error("{Model} model file not found: {Path}", _modelDescription, modelPath);
            return null;
        }

        if (!_disableGpu)
        {
            InferenceSession? session = null;
            try
            {
                using var dmlOptions = new SessionOptions();
                // Compatibility parameters for DirectML. These prioritize
                // stability over performance: ORT_ENABLE_BASIC + sequential
                // execution + disabled prepacking/graph-capture fix known
                // DirectML issues (e.g. the RMBG-1.4 "Resize node" invalid
                // parameter error) and are applied uniformly — the tagger's
                // former private copy had silently dropped them.
                dmlOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC;
                dmlOptions.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                dmlOptions.EnableMemoryPattern = false;
                dmlOptions.EnableCpuMemArena = false;
                dmlOptions.AddSessionConfigEntry("session.disable_prepacking", "1");
                dmlOptions.AddSessionConfigEntry("ep.dml.enable_graph_capture", "0");
                // TODO: Linux Implementation — DirectML is Windows-only; a
                // Linux port needs CUDA/ROCm providers here (the CPU fallback
                // below already keeps this path non-fatal on other platforms).
                dmlOptions.AppendExecutionProvider_DML(0);
                // CPU as fallback for operations DirectML doesn't support.
                dmlOptions.AppendExecutionProvider_CPU(0);

                session = new InferenceSession(modelPath, dmlOptions);
                inspectModel?.Invoke(session);
                _isGpuAvailable = true;
                Log.Information("{Model} ONNX session created with GPU (DirectML) acceleration", _modelDescription);
                return session;
            }
            catch (Exception ex)
            {
                // Dispose on the inspection-throw path too — a session that
                // constructed fine but reported unusable metadata would
                // otherwise leak its native model memory on every retry.
                session?.Dispose();
                Log.Warning(ex, "DirectML not available or failed to initialize for {Model}, falling back to CPU", _modelDescription);
            }
        }

        {
            InferenceSession? session = null;
            try
            {
                using var cpuOptions = new SessionOptions();
                cpuOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                cpuOptions.EnableMemoryPattern = true;
                cpuOptions.EnableCpuMemArena = true;
                cpuOptions.IntraOpNumThreads = Environment.ProcessorCount;

                session = new InferenceSession(modelPath, cpuOptions);
                inspectModel?.Invoke(session);
                _isGpuAvailable = false;
                Log.Information("{Model} ONNX session created with CPU execution ({Threads} threads)",
                    _modelDescription, Environment.ProcessorCount);
                return session;
            }
            catch (Exception ex)
            {
                session?.Dispose();
                Log.Error(ex, "Failed to create ONNX session for {Model}", _modelDescription);
                return null;
            }
        }
    }

    /// <summary>
    /// Runs one inference and reads the results out, all under the session
    /// lock. <paramref name="readResults"/> must copy what it needs (e.g.
    /// <c>AsTensor&lt;float&gt;().ToArray()</c> or writing into an image) —
    /// the result collection and its tensors are disposed when it returns.
    /// </summary>
    public T Run<T>(
        IReadOnlyCollection<NamedOnnxValue> inputs,
        Func<IDisposableReadOnlyCollection<DisposableNamedOnnxValue>, T> readResults)
    {
        _sessionLock.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var session = _session
                ?? throw new InvalidOperationException($"{_modelDescription}: ONNX session is not initialized");

            using var results = session.Run(inputs);
            return readResults(results);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>
    /// Permanently disables the GPU after a failed GPU inference and tears the
    /// session down so the next <see cref="InitializeAsync"/> rebuilds it
    /// CPU-only. There is deliberately no way back: a GPU that failed once
    /// (driver/model incompatibility) would just fail again.
    /// </summary>
    public async Task DemoteToCpuAsync(CancellationToken cancellationToken = default)
    {
        await _sessionLock.WaitAsync(cancellationToken);
        try
        {
            _session?.Dispose();
            _session = null;
            _disableGpu = true;
            _isGpuAvailable = false;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        // Taking the session lock first means an in-flight Run finishes
        // before the native session is freed. Late callers then observe
        // _disposed (their entry guard) or get a managed
        // ObjectDisposedException from the disposed semaphore — never a
        // native crash.
        _sessionLock.Wait();
        try
        {
            _disposed = true;
            _session?.Dispose();
            _session = null;
        }
        finally
        {
            _sessionLock.Release();
        }

        _sessionLock.Dispose();
    }
}
