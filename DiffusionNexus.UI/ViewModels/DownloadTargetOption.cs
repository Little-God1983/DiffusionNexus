namespace DiffusionNexus.UI.ViewModels;

public sealed record DownloadTargetOption(string DisplayName, string FullPath)
{
    public override string ToString() => DisplayName;
}
