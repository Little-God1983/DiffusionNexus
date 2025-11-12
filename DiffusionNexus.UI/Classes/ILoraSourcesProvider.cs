using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DiffusionNexus.UI.Classes;

public interface ILoraSourcesProvider
{
    Task<IReadOnlyList<LoraSourceInfo>> GetSourcesAsync(CancellationToken cancellationToken);
}

public record LoraSourceInfo(string DisplayName, string Path);
