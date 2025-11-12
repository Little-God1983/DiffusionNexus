using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DiffusionNexus.Service.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.LoraSort.Services;

public class CivitaiFileDownloaderTests
{
    [Fact]
    public async Task DownloadAsync_ShouldSendBearerTokenWhenApiKeyProvided()
    {
        string? authorizationHeader = null;
        using var handler = new TestHandler(request =>
        {
            authorizationHeader = request.Headers.Authorization?.ToString();
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
            };
            response.Content.Headers.ContentLength = 3;
            return response;
        });

        using var httpClient = new HttpClient(handler);
        var downloader = new CivitaiFileDownloader(httpClient);
        var tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bin");

        try
        {
            await downloader.DownloadAsync(new Uri("https://example.com/model"), tempFile, "secret", null, CancellationToken.None);

            authorizationHeader.Should().Be("Bearer secret");
            File.Exists(tempFile).Should().BeTrue();
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    private sealed class TestHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public TestHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }
}
