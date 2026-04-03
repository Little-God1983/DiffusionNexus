using DiffusionNexus.Domain.Services;

namespace DiffusionNexus.UI.Services.SpellCheck;

/// <summary>
/// Adapts the UI-layer <see cref="ISpellCheckService"/> to the domain-level
/// <see cref="ISpellChecker"/> contract so dataset quality checks can use
/// spell checking without depending on the UI layer.
/// </summary>
internal sealed class SpellCheckerAdapter : ISpellChecker
{
    private readonly ISpellCheckService _inner;

    public SpellCheckerAdapter(ISpellCheckService inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <inheritdoc />
    public bool IsReady => _inner.IsReady;

    /// <inheritdoc />
    public bool Check(string word) => _inner.Check(word);

    /// <inheritdoc />
    public IReadOnlyList<string> FindMisspelledWords(string text)
    {
        var errors = _inner.CheckText(text);
        return errors
            .Select(e => e.Word)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
