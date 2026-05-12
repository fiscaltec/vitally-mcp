using System.Net;
using Microsoft.Extensions.Logging;

namespace VitallyMcp;

/// <summary>
/// DelegatingHandler that makes the HTTP pipeline rate-limit-aware against the Vitally REST API:
///   - Logs a warning when the X-RateLimit-Remaining header drops below LowRemainingThreshold.
///   - On HTTP 429 Too Many Requests, waits for the Retry-After or X-RateLimit-Reset hint
///     (capped at MaxRetryDelay) and retries up to MaxRetries times.
/// Vitally's documented limit is 1000 requests / minute (sliding window).
/// </summary>
public class VitallyRateLimitHandler : DelegatingHandler
{
    public int MaxRetries { get; set; } = 3;
    public int LowRemainingThreshold { get; set; } = 50;
    public TimeSpan FallbackRetryDelay { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(60);

    private readonly ILogger<VitallyRateLimitHandler>? _logger;
    private readonly TimeProvider _timeProvider;

    public VitallyRateLimitHandler(ILogger<VitallyRateLimitHandler>? logger = null, TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var response = await base.SendAsync(request, cancellationToken);

            if (response.StatusCode != HttpStatusCode.TooManyRequests)
            {
                WarnIfNearingLimit(response);
                return response;
            }

            if (attempt >= MaxRetries)
            {
                _logger?.LogWarning(
                    "Vitally rate limit exceeded ({StatusCode}); {MaxRetries} retries exhausted, returning 429 to caller.",
                    (int)response.StatusCode, MaxRetries);
                return response;
            }

            var delay = GetRetryDelay(response);
            _logger?.LogWarning(
                "Vitally rate limited (429); retrying in {DelayMs}ms (attempt {Attempt}/{MaxRetries}).",
                delay.TotalMilliseconds, attempt + 1, MaxRetries);

            response.Dispose();
            await Task.Delay(delay, cancellationToken);
        }
    }

    private void WarnIfNearingLimit(HttpResponseMessage response)
    {
        if (_logger is null)
        {
            return;
        }

        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var values)
            && int.TryParse(values.FirstOrDefault(), out var remaining)
            && remaining < LowRemainingThreshold)
        {
            _logger.LogWarning(
                "Vitally rate limit nearing exhaustion: {Remaining} requests remaining in current window.",
                remaining);
        }
    }

    private TimeSpan GetRetryDelay(HttpResponseMessage response)
    {
        // Retry-After: relative seconds
        if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return Cap(delta);
        }

        // Retry-After: HTTP date
        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var wait = date - _timeProvider.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                return Cap(wait);
            }
        }

        // X-RateLimit-Reset: Unix timestamp (seconds)
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues)
            && long.TryParse(resetValues.FirstOrDefault(), out var resetUnix))
        {
            var resetTime = DateTimeOffset.FromUnixTimeSeconds(resetUnix);
            var wait = resetTime - _timeProvider.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                return Cap(wait);
            }
        }

        return FallbackRetryDelay;
    }

    private TimeSpan Cap(TimeSpan value) => value > MaxRetryDelay ? MaxRetryDelay : value;
}
