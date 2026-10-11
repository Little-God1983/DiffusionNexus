namespace DiffusionNexus.UI.Services;

/// <summary>
/// Which install is running right now, app-wide: Engine Features, the base Engine install, a workload
/// install, a package update. The catalog apply waits while one runs (#618): it never swaps the catalog
/// under a running plan.
/// </summary>
public interface IInstallActivity
{
    /// <summary>The newest running install's name; null when none runs.</summary>
    string? Running { get; }

    /// <summary>Raised after every Begin and every end, on the caller's thread.</summary>
    event Action? Changed;

    /// <summary>Marks <paramref name="name"/> as running until the returned handle is disposed (twice is fine).</summary>
    IDisposable Begin(string name);
}

public sealed class InstallActivity : IInstallActivity
{
    private readonly Lock _gate = new();
    private readonly List<Entry> _running = [];

    public string? Running { get { lock (_gate) return _running.Count == 0 ? null : _running[^1].Name; } }

    public event Action? Changed;

    public IDisposable Begin(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var entry = new Entry(this, name);
        lock (_gate) _running.Add(entry);
        Changed?.Invoke();
        return entry;
    }

    private void End(Entry entry)
    {
        bool removed;
        lock (_gate) removed = _running.Remove(entry);
        if (removed) Changed?.Invoke();
    }

    private sealed class Entry(InstallActivity owner, string name) : IDisposable
    {
        public string Name { get; } = name;
        public void Dispose() => owner.End(this);
    }
}
