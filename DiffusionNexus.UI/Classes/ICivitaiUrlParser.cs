namespace DiffusionNexus.UI.Classes;

public interface ICivitaiUrlParser
{
    bool TryParse(string? url, out CivitaiLinkInfo? info, out string? errorMessage);
}

public record CivitaiLinkInfo(int ModelId, int? ModelVersionId);
