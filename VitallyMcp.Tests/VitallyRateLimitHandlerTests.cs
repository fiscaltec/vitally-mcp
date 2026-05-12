using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System.Net;
using System.Net.Http.Headers;

namespace VitallyMcp.Tests;

/// <summary>
/// Tests for the VitallyRateLimitHandler delegating handler. Drives an inner queueable
/// HttpMessageHandler so each test can stage a sequence of responses (e.g. 429 then 200).
/// </summary>
public class VitallyRateLimitHandlerTests
{
    /// <summary>
    /// Test handler that returns responses from a queue and records all received requests.
    /// </summary>
    private sealed class QueueingHandler : HttpMessageHandler
    {
        public Queue<HttpResponseMessage> Responses { get; } = new();
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Responses.Dequeue());
        }
    }

    private static HttpResponseMessage TooManyRequests(TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        if (retryAfter.HasValue)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter.Value);
        }
        return response;
    }

    private static HttpResponseMessage Ok(int? rateLimitRemaining = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}")
        };
        if (rateLimitRemaining.HasValue)
        {
            response.Headers.Add("X-RateLimit-Remaining", rateLimitRemaining.Value.ToString());
        }
        return response;
    }

    private static (HttpClient client, QueueingHandler inner, Mock<ILogger<VitallyRateLimitHandler>> logger) BuildClient(
        Action<VitallyRateLimitHandler>? configure = null)
    {
        var inner = new QueueingHandler();
        var logger = new Mock<ILogger<VitallyRateLimitHandler>>();
        var handler = new VitallyRateLimitHandler(logger.Object)
        {
            InnerHandler = inner,
            // Use tiny fallback so tests are fast even when 429 has no Retry-After.
            FallbackRetryDelay = TimeSpan.FromMilliseconds(1),
            MaxRetryDelay = TimeSpan.FromMilliseconds(50)
        };
        configure?.Invoke(handler);
        return (new HttpClient(handler), inner, logger);
    }

    [Fact]
    public async Task SendAsync_OnSuccess_ShouldNotRetry()
    {
        var (client, inner, _) = BuildClient();
        inner.Responses.Enqueue(Ok());

        var response = await client.GetAsync("http://example.test/x");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task SendAsync_On429ThenSuccess_ShouldRetryAndReturnSuccess()
    {
        var (client, inner, _) = BuildClient();
        inner.Responses.Enqueue(TooManyRequests());
        inner.Responses.Enqueue(Ok());

        var response = await client.GetAsync("http://example.test/x");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task SendAsync_On429_ShouldHonourRetryAfterHeader()
    {
        var (client, inner, _) = BuildClient();
        // Retry-After requests 50ms (capped at MaxRetryDelay)
        inner.Responses.Enqueue(TooManyRequests(retryAfter: TimeSpan.FromMilliseconds(40)));
        inner.Responses.Enqueue(Ok());

        var start = DateTimeOffset.UtcNow;
        var response = await client.GetAsync("http://example.test/x");
        var elapsed = DateTimeOffset.UtcNow - start;

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(30));
    }

    [Fact]
    public async Task SendAsync_When429PersistsBeyondMaxRetries_ShouldReturn429ToCaller()
    {
        var (client, inner, _) = BuildClient(h => h.MaxRetries = 2);
        // 1 initial + 2 retries = 3 attempts, all 429
        inner.Responses.Enqueue(TooManyRequests());
        inner.Responses.Enqueue(TooManyRequests());
        inner.Responses.Enqueue(TooManyRequests());

        var response = await client.GetAsync("http://example.test/x");

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        inner.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task SendAsync_WhenRateLimitRemainingBelowThreshold_ShouldLogWarning()
    {
        var (client, inner, logger) = BuildClient(h => h.LowRemainingThreshold = 50);
        inner.Responses.Enqueue(Ok(rateLimitRemaining: 10));

        await client.GetAsync("http://example.test/x");

        VerifyWarningLoggedContaining(logger, "10 requests remaining");
    }

    [Fact]
    public async Task SendAsync_WhenRateLimitRemainingAboveThreshold_ShouldNotLogWarning()
    {
        var (client, inner, logger) = BuildClient(h => h.LowRemainingThreshold = 50);
        inner.Responses.Enqueue(Ok(rateLimitRemaining: 500));

        await client.GetAsync("http://example.test/x");

        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    [Fact]
    public async Task SendAsync_WhenRateLimitRemainingHeaderMissing_ShouldNotLogWarning()
    {
        var (client, inner, logger) = BuildClient();
        inner.Responses.Enqueue(Ok(rateLimitRemaining: null));

        await client.GetAsync("http://example.test/x");

        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    [Fact]
    public async Task SendAsync_On429_ShouldLogRetryWarning()
    {
        var (client, inner, logger) = BuildClient();
        inner.Responses.Enqueue(TooManyRequests());
        inner.Responses.Enqueue(Ok());

        await client.GetAsync("http://example.test/x");

        VerifyWarningLoggedContaining(logger, "rate limited");
    }

    [Fact]
    public async Task SendAsync_WhenRetriesExhausted_ShouldLogExhaustionWarning()
    {
        var (client, inner, logger) = BuildClient(h => h.MaxRetries = 1);
        inner.Responses.Enqueue(TooManyRequests());
        inner.Responses.Enqueue(TooManyRequests());

        await client.GetAsync("http://example.test/x");

        VerifyWarningLoggedContaining(logger, "retries exhausted");
    }

    [Fact]
    public async Task SendAsync_WithXRateLimitResetHeader_ShouldHonourUnixResetTime()
    {
        var (client, inner, _) = BuildClient();
        var response429 = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        // Set X-RateLimit-Reset to 50ms from now (Unix-seconds rounds to "now" or "now+1s")
        var resetUnix = DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeSeconds();
        response429.Headers.Add("X-RateLimit-Reset", resetUnix.ToString());
        inner.Responses.Enqueue(response429);
        inner.Responses.Enqueue(Ok());

        var response = await client.GetAsync("http://example.test/x");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.Requests.Should().HaveCount(2);
    }

    private static void VerifyWarningLoggedContaining(
        Mock<ILogger<VitallyRateLimitHandler>> logger, string substring)
    {
        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state!.ToString()!.Contains(substring)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }
}
