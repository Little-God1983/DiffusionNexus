using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DiffusionNexus.Service.Classes;

namespace DiffusionNexus.Service.Services;

public class CivitaiFileDownloader
{
    private readonly HttpClient _httpClient;

    public CivitaiFileDownloader(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task DownloadAsync(Uri downloadUri, string destinationFilePath, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFilePath)!);

        using var response = await _httpClient.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength;
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var fileStream = new FileStream(destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long totalRead = 0;
        var stopwatch = Stopwatch.StartNew();

        while (true)
        {
            var read = await responseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
                break;

            await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            totalRead += read;

            double? bytesPerSecond = null;
            var elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
            if (elapsedSeconds > 0)
            {
                bytesPerSecond = totalRead / elapsedSeconds;
            }

            progress?.Report(new ModelDownloadProgress(totalRead, contentLength, bytesPerSecond));
        }

        progress?.Report(new ModelDownloadProgress(totalRead, contentLength, null));
    }
}
