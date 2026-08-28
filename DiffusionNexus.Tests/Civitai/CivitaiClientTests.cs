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
        // Exercise the retry loop without sleeping through its real backoff.
        var client = new CivitaiClient(http, disposeHttpClient: true) { RetryDelayOverride = _ => TimeSpan.Zero };
        return (client, handler);
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
        var (client, handler) = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{\"error\":\"oops\"}")
        });

        using (client)
        {
            var act = async () => await client.GetModelAsync(1);

            var ex = await act.Should().ThrowAsync<HttpRequestException>();
            ex.Which.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        }

        // Initial attempt + 3 retries: a persistent 500 still ends in a throw.
        handler.Requests.Should().HaveCount(4);
    }

    [Fact]
    public async Task GetModelAsync_RetriesTransientServerError_ThenSucceeds()
    {
        // A single 502 from Civitai's CDN used to cost a freshly downloaded LoRA its
        // entire metadata record — the caller swallowed the throw and reported success.
        var responses = new Queue<HttpResponseMessage>(
        [
            new HttpResponseMessage(HttpStatusCode.BadGateway),
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            Json(HttpStatusCode.OK, new { id = 7, name = "recovered" })
        ]);

        var (client, handler) = CreateClient(_ => responses.Dequeue());

        using (client)
        {
            var model = await client.GetModelAsync(7);
            model.Should().NotBeNull();
            model!.Name.Should().Be("recovered");
        }

        handler.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task GetModelVersionByHashAsync_RetriesTransportFailure_ThenSucceeds()
    {
        // Connection resets surface as HttpRequestException with no StatusCode.
        var attempts = 0;
        var (client, handler) = CreateClient(_ =>
        {
            if (++attempts == 1) throw new HttpRequestException("The connection was closed unexpectedly.");
            return Json(HttpStatusCode.OK, new { id = 11, name = "v1" });
        });

        using (client)
        {
            var version = await client.GetModelVersionByHashAsync("ABC123");
            version.Should().NotBeNull();
            version!.Id.Should().Be(11);
        }

        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetModelAsync_DoesNotRetry_OnClientError()
    {
        // 401/403/404 are answers, not outages — retrying only delays the failure.
        var (client, handler) = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("forbidden")
        });

        using (client)
        {
            var act = async () => await client.GetModelAsync(1);
            await act.Should().ThrowAsync<HttpRequestException>();
        }

        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task GetModelAsync_DoesNotRetry_OnMalformedJson()
    {
        var (client, handler) = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{ not json", Encoding.UTF8, "application/json")
        });

        using (client)
        {
            var act = async () => await client.GetModelAsync(1);
            await act.Should().ThrowAsync<JsonException>("a changed response shape does not fix itself");
        }

        handler.Requests.Should().ContainSingle();
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
    public async Task GetModelsAsync_ImageWithNullWidthAndHeight_DoesNotThrow()
    {
        // Civitai returns width/height as null for some preview media (e.g. videos
        // whose dimensions haven't been probed yet) instead of omitting the field.
        var (client, _) = CreateClient(_ => Json(HttpStatusCode.OK, new
        {
            items = new object[]
            {
                new
                {
                    id = 1,
                    name = "m1",
                    type = "LORA",
                    modelVersions = new object[]
                    {
                        new
                        {
                            id = 10,
                            images = new object[]
                            {
                                new { url = "https://example.com/preview.mp4", type = "video", width = (int?)null, height = (int?)null }
                            }
                        }
                    }
                }
            },
            metadata = new { totalItems = 1, currentPage = 1, pageSize = 1, totalPages = 1 }
        }));

        using (client)
        {
            var page = await client.GetModelsAsync();

            var image = page.Items[0].ModelVersions[0].Images[0];
            image.Width.Should().BeNull();
            image.Height.Should().BeNull();
        }
    }

    [Fact]
    public async Task GetAsync_OnJsonException_IncludesRawResponseBodyInMessage()
    {
        // Any future Civitai response-shape drift (not just width/height) should
        // leave a diagnosable trace instead of a bare "could not be converted" message.
        const string badJson = "{\"items\":[{\"id\":\"not-a-number\"}]}";
        var (client, _) = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(badJson, Encoding.UTF8, "application/json")
        });

        using (client)
        {
            var act = async () => await client.GetModelsAsync();

            var ex = await act.Should().ThrowAsync<JsonException>();
            ex.Which.Message.Should().Contain("not-a-number");
        }
    }

    [Fact]
    public async Task GetAsync_OnJsonException_DoesNotSplitSurrogatePair_WhenTruncatingBody()
    {
        // The raw-body snippet is cut at 4000 chars. Civitai payloads carry user-authored
        // names/descriptions that can contain emoji, so the cut point can land mid-surrogate
        // pair; slicing there would leave a lone surrogate (an ill-formed string) in the message.
        // Position an emoji so its high surrogate sits exactly at index 3999.
        const string prefix = "{\"items\":[{\"id\":\"";   // 17 chars -> indices 0..16
        var filler = new string('x', 3999 - prefix.Length);
        var badJson = prefix + filler + "\U0001F600" + "\"}]}";
        badJson[3999].Should().Match<char>(c => char.IsHighSurrogate(c), "test fixture must straddle the cut point");

        var (client, _) = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(badJson, Encoding.UTF8, "application/json")
        });

        using (client)
        {
            var act = async () => await client.GetModelsAsync();

            var ex = await act.Should().ThrowAsync<JsonException>();
            HasLoneSurrogate(ex.Which.Message).Should().BeFalse();
        }
    }

    /// <summary>Returns true if the string contains an unpaired UTF-16 surrogate.</summary>
    private static bool HasLoneSurrogate(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1])) return true;
                i++; // consume the pair
            }
            else if (char.IsLowSurrogate(value[i]))
            {
                return true;
            }
        }
        return false;
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

        // Initial attempt + 1 retry = 2 total. The gateway's shared cooldown does the waiting now;
        // three in-call retries used to sleep 10+20+40s while holding a download slot.
        handler.Requests.Should().HaveCount(2);
    }

    private sealed class RecordingRateLimitObserver : ICivitaiRateLimitObserver
    {
        public List<TimeSpan?> Observed { get; } = [];
        public void OnRateLimited(TimeSpan? retryAfter) => Observed.Add(retryAfter);
    }

    [Fact]
    public async Task GetAsync_NotifiesObserver_OnEveryRateLimit_EvenWhenTheRetrySucceeds()
    {
        var responses = new Queue<HttpResponseMessage>(new[]
        {
            new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7)) }
            },
            Json(HttpStatusCode.OK, new { id = 1, name = "ok" })
        });

        var observer = new RecordingRateLimitObserver();
        var handler = new FakeHttpHandler(_ => responses.Dequeue());
        using var client = new CivitaiClient(new HttpClient(handler), disposeHttpClient: true, rateLimitObserver: observer)
        {
            RetryDelayOverride = _ => TimeSpan.Zero
        };

        var model = await client.GetModelAsync(1);

        model.Should().NotBeNull();
        observer.Observed.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public async Task GetAsync_ParsesRetryAfterAsHttpDate()
    {
        var observer = new RecordingRateLimitObserver();
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(45)) }
        });
        using var client = new CivitaiClient(new HttpClient(handler), disposeHttpClient: true, rateLimitObserver: observer)
        {
            RetryDelayOverride = _ => TimeSpan.Zero
        };

        var act = async () => await client.GetModelAsync(1);
        var ex = await act.Should().ThrowAsync<CivitaiRateLimitedException>();

        ex.Which.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        ex.Which.RetryAfter.Should().NotBeNull();
        ex.Which.RetryAfter!.Value.Should().BeCloseTo(TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(5));
        observer.Observed.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetAsync_ClampsPastRetryAfterHttpDate_ToZero()
    {
        // A past HTTP-date must clamp to TimeSpan.Zero, not go negative — the gateway feeds
        // this value straight into a cooldown deadline.
        var observer = new RecordingRateLimitObserver();
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(-45)) }
        });
        using var client = new CivitaiClient(new HttpClient(handler), disposeHttpClient: true, rateLimitObserver: observer)
        {
            RetryDelayOverride = _ => TimeSpan.Zero
        };

        var act = async () => await client.GetModelAsync(1);
        var ex = await act.Should().ThrowAsync<CivitaiRateLimitedException>();

        ex.Which.RetryAfter.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task GetAsync_ClampsAnOversizedRetryAfterDelta_ToTheCeiling()
    {
        // Regression test for finding 2 of the gateway fix-wave review. An unclamped delta flows
        // straight into Task.Delay in both this client's own retry and (via the gateway, on
        // CancellationToken.None) CivitaiRateLimitCooldown.WaitAsync — over ~49.7 days Task.Delay
        // itself throws ArgumentOutOfRangeException, and short of that a huge-but-legal delta would
        // freeze every Civitai call in the process for as long as the server named.
        var observer = new RecordingRateLimitObserver();
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromDays(400)) }
        });
        using var client = new CivitaiClient(new HttpClient(handler), disposeHttpClient: true, rateLimitObserver: observer)
        {
            RetryDelayOverride = _ => TimeSpan.Zero
        };

        var act = async () => await client.GetModelAsync(1);
        var ex = await act.Should().ThrowAsync<CivitaiRateLimitedException>();

        ex.Which.RetryAfter.Should().Be(CivitaiClient.MaxRetryAfter);
    }

    [Fact]
    public async Task GetAsync_ClampsAnOversizedRetryAfterHttpDate_ToTheCeiling()
    {
        // The HTTP-date form is the one the finding calls out specifically: `date - UtcNow` uses the
        // local wall clock, so a machine whose clock is years behind (or a server emitting a bad
        // date) yields a multi-year TimeSpan that must be clamped the same way the delta form is.
        var observer = new RecordingRateLimitObserver();
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddYears(3)) }
        });
        using var client = new CivitaiClient(new HttpClient(handler), disposeHttpClient: true, rateLimitObserver: observer)
        {
            RetryDelayOverride = _ => TimeSpan.Zero
        };

        var act = async () => await client.GetModelAsync(1);
        var ex = await act.Should().ThrowAsync<CivitaiRateLimitedException>();

        ex.Which.RetryAfter.Should().Be(CivitaiClient.MaxRetryAfter);
    }

    [Fact]
    public async Task RateLimitedException_IsStillCaughtAsHttpRequestException()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        using var client = new CivitaiClient(new HttpClient(handler), disposeHttpClient: true)
        {
            RetryDelayOverride = _ => TimeSpan.Zero
        };

        var caught = false;
        try
        {
            await client.GetModelAsync(1);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            caught = true;
        }

        caught.Should().BeTrue();
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
