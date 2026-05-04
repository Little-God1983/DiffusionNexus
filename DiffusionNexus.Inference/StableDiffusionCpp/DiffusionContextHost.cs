using DiffusionNexus.Inference.Models;
using SDNet = StableDiffusion.NET;

namespace DiffusionNexus.Inference.StableDiffusionCpp;

/// <summary>
/// Owns the lifetime of loaded <see cref="SDNet.DiffusionModel"/> contexts.
/// Per the design decision: <b>load on first request, keep alive forever (until app exit)</b>.
/// stable-diffusion.cpp contexts are not thread-safe, so each context is guarded by its
/// own <see cref="SemaphoreSlim"/> — concurrent <see cref="GetOrLoadAsync"/> calls for the
/// same model serialize cleanly, calls for different models proceed in parallel.
/// </summary>
internal sealed class DiffusionContextHost : IDisposable
{
    private readonly Dictionary<string, ContextEntry> _contexts = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _registryLock = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Returns a loaded context for the given descriptor, creating it on first call.
    /// The returned <see cref="ContextLease"/> must be disposed; while held it owns the
    /// context's per-instance lock so the caller has exclusive access for one generation.
    /// </summary>
    public async Task<ContextLease> GetOrLoadAsync(
        ModelDescriptor descriptor,
        Action<string>? onLoading,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(descriptor);

        ContextEntry entry;
        await _registryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_contexts.TryGetValue(descriptor.Key, out var existing))
            {
                existing = new ContextEntry(descriptor);
                _contexts[descriptor.Key] = existing;
            }
            entry = existing;
        }
        finally
        {
            _registryLock.Release();
        }

        // Acquire the per-context lock BEFORE loading so we don't load the same model twice.
        await entry.UseLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (entry.Model is null)
            {
                onLoading?.Invoke($"Loading {descriptor.DisplayName}…");
                var parameters = StableDiffusionCppLoader.Build(descriptor);
                // Native load is synchronous + CPU/disk-bound. Run it off the calling thread.
                entry.Model = await Task.Run(() => new SDNet.DiffusionModel(parameters), cancellationToken)
                                        .ConfigureAwait(false);
            }
            return new ContextLease(entry);
        }
        catch
        {
            entry.UseLock.Release();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var entry in _contexts.Values)
        {
            try { entry.Model?.Dispose(); } catch { /* native cleanup swallowed by design */ }
            entry.UseLock.Dispose();
        }
        _contexts.Clear();
        _registryLock.Dispose();
    }

    /// <summary>One model + the lock that serializes its single-threaded native context.</summary>
    internal sealed class ContextEntry(ModelDescriptor descriptor)
    {
        public ModelDescriptor Descriptor { get; } = descriptor;
        public SemaphoreSlim UseLock { get; } = new(1, 1);
        public SDNet.DiffusionModel? Model { get; set; }
    }

    /// <summary>
    /// Exclusive lease on a loaded native context. Disposing releases the use-lock so
    /// another caller can run on the same model. Does NOT unload the model — that's the
    /// host's job at app shutdown.
    /// </summary>
    internal sealed class ContextLease : IDisposable
    {
        private readonly ContextEntry _entry;
        private bool _released;

        internal ContextLease(ContextEntry entry) => _entry = entry;

        public SDNet.DiffusionModel Model => _entry.Model
            ?? throw new InvalidOperationException("Context lease used after model was released.");

        public ModelDescriptor Descriptor => _entry.Descriptor;

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            _entry.UseLock.Release();
        }
    }
}
