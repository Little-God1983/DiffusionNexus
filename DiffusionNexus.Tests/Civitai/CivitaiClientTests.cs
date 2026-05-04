using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using FluentAssertions;

namespace DiffusionNexus.Tests.Civitai;

public class CivitaiClientTests
{
    private const string BaseUrl = "https://civitai.com/api/v1/";

    private static (CivitaiClient client, FakeHttpHandler handler) CreateClient(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new FakeHttpHandler(responder);
        var http = new HttpClient(handler);
        return (new CivitaiClient(http, disposeHttpClient: true), handler);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };

    [Fact]
    public void Ctor_NullHttpClient_Throws()
    {
        var act = () => new CivitaiClient(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task GetModelAsync_ReturnsDeserializedModel_OnSuccess()
    {
        var (client, handler) = CreateClient(_ => Json(HttpStatusCode.OK, new
        {
            id = 42,
            name = "TestModel",
            type = "LORA",
            tags = new[] { "anime", "style" }
        }));

        using (client)
        {
            var model = await client.GetModelAsync(42);

            model.Should().NotBeNull();
            model!.Id.Should().Be(42);
            model.Name.Should().Be("TestModel");
            model.Type.Should().Be(CivitaiModelType.LORA);
            model.Tags.Should().BeEquivalentTo(new[] { "anime", "style" });
        }

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].RequestUri.Should().Be(new Uri(BaseUrl + "models/42"));
    }

    [Fact]
    public async Task GetModelAsync_Returns404AsNull()
    {
        var (client, _) = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        using (client)
        {
            var model = await client.GetModelAsync(999);
            model.Should().BeNull();
        }
    }

    [Fact]
    public async Task GetModelAsync_ThrowsHttpRequestException_OnError()
    {
        var (client, _) = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{\"error\":\"oops\"}")
        });

        using (client)
        {
            var act = async () => await client.GetModelAsync(1);

            var ex = await act.Should().ThrowAsync<HttpRequestException>();
            ex.Which.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        }
    }

    [Fact]
    public async Task GetModelAsync_Throws401_WhenUnauthorized()
    {
        var (client, _) = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("unauthorized")
        });

        using (client)
        {
            var act = async () => await client.GetModelAsync(1, apiKey: "bad-key");

            var ex = await act.Should().ThrowAsync<HttpRequestException>();
            ex.Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
    }

    [Fact]
    public async Task GetModelAsync_AddsApiKeyAuthorizationHeader_WhenProvided()
    {
        var (client, handler) = CreateClient(_ => Json(HttpStatusCode.OK, new { id = 1, name = "x" }));

        using (client)
        {
            await client.GetModelAsync(1, apiKey: "secret-key");
        }

        handler.Requests[0].Headers.TryGetValues("Authorization", out var values).Should().BeTrue();
        values!.Single().Should().Be("ApiKey secret-key");
    }

    [Fact]
    public async Task GetModelAsync_DoesNotAddAuthorizationHeader_WhenApiKeyIsNullOrEmpty()
    {
        var (client, handler) = CreateClient(_ => Json(HttpStatusCode.OK, new { id = 1, name = "x" }));

        using (client)
        {
            await client.GetModelAsync(1);
            await client.GetModelAsync(1, apiKey: "  ");
        }

        handler.Requests.Should().HaveCount(2);
        handler.Requests.Should().OnlyContain(r => !r.Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task GetModelsAsync_NoQuery_HitsBareEndpoint()
    {
        var (client, handler) = CreateClient(_ => Json(HttpStatusCode.OK, new
        {
            items = new object[] { },
            metadata = new { totalItems = 0, currentPage = 1, pageSize = 0, totalPages = 0 }
        }));

        using (client)
        {
            var page = await client.GetModelsAsync();
            page.Items.Should().BeEmpty();
        }

        handler.Requests[0].RequestUri.Should().Be(new Uri(BaseUrl + "models"));
    }

    [Fact]
    public async Task GetModelsAsync_WithQuery_AppendsQueryString()
    {
        var (client, handler) = CreateClient(_ => Json(HttpStatusCode.OK, new
        {
            items = new[] { new { id = 1, name = "m1", type = "LORA" } },
            metadata = new { totalItems = 1, currentPage = 1, pageSize = 1, totalPages = 1 }
        }));

        using (client)
        {
            var page = await client.GetModelsAsync(new CivitaiModelsQuery { Limit = 5, Query = "anime" });

            page.Items.Should().HaveCount(1);
            page.Metadata!.TotalItems.Should().Be(1);
        }

        handler.Requests[0].RequestUri!.Query.Should().Contain("limit=5").And.Contain("query=anime");
    }

    [Fact]
    public async Task GetModelsAsync_NullResponseBody_ReturnsEmptyPage()
    {
        var (client, _) = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("null", Encoding.UTF8, "application/json")
        });

        using (client)
        {
            var page = await client.GetModelsAsync();
            page.Should().NotBeNull();
            page.Items.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task GetModelVersionByHashAsync_NullOrWhitespace_Throws()
    {
        var (client, _) = CreateClient(_ => Json(HttpStatusCode.OK, new { }));

        using (client)
        {
            var act1 = async () => await client.GetModelVersionByHashAsync(null!);
            var act2 = async () => await client.GetModelVersionByHashAsync("   ");

            await act1.Should().ThrowAsync<ArgumentException>();
            await act2.Should().ThrowAsync<ArgumentException>();
        }
    }

    [Fact]
    public async Task GetModelVersionByHashAsync_HitsHashEndpoint()
    {
        var (client, handler) = CreateClient(_ => Json(HttpStatusCode.OK, new { id = 7 }));

        using (client)
        {
            await client.GetModelVersionByHashAsync("DEADBEEF");
        }

        handler.Requests[0].RequestUri.Should().Be(new Uri(BaseUrl + "model-versions/by-hash/DEADBEEF"));
    }

    [Fact]
    public async Task GetAsync_RetriesOnTooManyRequests_ThenSucceeds()
    {
        var responses = new Queue<HttpResponseMessage>(new[]
        {
            new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1)) }
            },
            Json(HttpStatusCode.OK, new { id = 1, name = "ok" })
        });

        var (client, handler) = CreateClient(_ => responses.Dequeue());

        using (client)
        {
            var model = await client.GetModelAsync(1);
            model.Should().NotBeNull();
            model!.Name.Should().Be("ok");
        }

        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetAsync_ThrowsAfterMaxRateLimitRetries()
    {
        var (client, handler) = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1)) }
        });

        using (client)
        {
            var act = async () => await client.GetModelAsync(1);

            var ex = await act.Should().ThrowAsync<HttpRequestException>();
            ex.Which.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        }

        // Initial attempt + 3 retries = 4 total
        handler.Requests.Should().HaveCount(4);
    }

    [Fact]
    public async Task GetTagsAsync_BuildsExpectedUrl()
    {
        var (client, handler) = CreateClient(_ => Json(HttpStatusCode.OK, new
        {
            items = Array.Empty<object>()
        }));

        using (client)
        {
            await client.GetTagsAsync(limit: 20, page: 2, query: "style");
        }

        handler.Requests[0].RequestUri!.ToString().Should().Be(BaseUrl + "tags?limit=20&page=2&query=style");
    }

    [Fact]
    public async Task GetCreatorsAsync_NoArgs_HitsBareEndpoint()
    {
        var (client, handler) = CreateClient(_ => Json(HttpStatusCode.OK, new
        {
            items = Array.Empty<object>()
        }));

        using (client)
        {
            await client.GetCreatorsAsync();
        }

        handler.Requests[0].RequestUri.Should().Be(new Uri(BaseUrl + "creators"));
    }

    [Fact]
    public async Task Dispose_DoesNotDisposeExternallyOwnedHttpClient()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
        var sut = new CivitaiClient(http, disposeHttpClient: false);

        sut.Dispose();

        // If HttpClient was disposed, SendAsync would throw ObjectDisposedException.
        var act = async () => await http.GetAsync("models");
        await act.Should().NotThrowAsync<ObjectDisposedException>();

        http.Dispose();
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public List<HttpRequestMessage> Requests { get; } = new();

        public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_responder(request));
        }
    }
}
