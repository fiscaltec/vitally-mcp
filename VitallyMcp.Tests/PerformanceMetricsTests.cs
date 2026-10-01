using System.Net;
using Azure;
using Azure.Security.KeyVault.Secrets;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace VitallyMcp.Tests;

/// <summary>
/// Phase 6 of #94: the counters and the upstream-call duration. The rate-limit and the Graph / OIDC
/// cache counters are tested beside their components; these are the ones without a natural home.
/// </summary>
/// <remarks>
/// The earlier "slow requests" diagnosis concluded model inference rather than the server, and that
/// was reasoned rather than measured because there was nothing to measure with. These are what make
/// that conclusion checkable.
/// </remarks>
public class PerformanceMetricsTests
{
    [Fact]
    public async Task ApiKeyProvider_CountsAMissOnTheVaultFetch_AndAHitOnTheCachedRead()
    {
        using var capture = new MetricCapture();
        var secrets = new Mock<SecretClient>();
        secrets.Setup(s => s.GetSecretAsync("vitally-shared", null, It.IsAny<SecretContentType?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(
                SecretModelFactory.KeyVaultSecret(new SecretProperties("vitally-shared"), value: "sk_test"),
                Mock.Of<Response>()));
        var provider = new VitallyApiKeyProvider(
            Options.Create(new VitallyServerOptions()),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<VitallyApiKeyProvider>.Instance,
            secrets.Object,
            capture.Metrics);

        await provider.GetApiKeyAsync();
        await provider.GetApiKeyAsync();

        capture.Total("vitally.cache.lookups", ("cache", "api_key"), ("result", "miss")).Should().Be(1);
        capture.Total("vitally.cache.lookups", ("cache", "api_key"), ("result", "hit")).Should().Be(1);
    }

    [Fact]
    public async Task ApiKeyProvider_CountsNothing_OnTheDevelopmentKeyPath()
    {
        // No cache is involved without Key Vault, so counting a "hit" would inflate the rate with
        // lookups that never happened.
        using var capture = new MetricCapture();
        var provider = new VitallyApiKeyProvider(
            Options.Create(new VitallyServerOptions { DevelopmentApiKey = "sk_dev" }),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<VitallyApiKeyProvider>.Instance,
            metrics: capture.Metrics);

        await provider.GetApiKeyAsync();

        capture.Total("vitally.cache.lookups").Should().Be(0);
    }

    [Fact]
    public async Task Pager_CountsATruncation_TaggedWithTheResourceType()
    {
        // `truncated: true` means the pager stopped at MaxAutoPageFetches and the matching total is
        // unknowable. The rate of it says whether the cap is set where users hit it.
        using var capture = new MetricCapture();
        var page = """{"results":[{"id":"org-1","name":"Acme"}],"next":"more"}""";
        var (client, _) = TestHelpers.CreateMockHttpClientPaged(page, page, page);
        using var _client = client;
        var service = TestHelpers.BuildVitallyService(client, maxAutoPageFetches: 2, metrics: capture.Metrics);

        await service.GetByNameContainsAsync("organizations", "Acme");

        capture.Total("vitally.autopager.truncations", ("resource", "organizations")).Should().Be(1);
    }

    [Fact]
    public async Task Pager_TagsTheFixedResourceKind_NeverTheCallersIdInThePath()
    {
        // List_conversations_by_account pages `accounts/{accountId}/conversations`, so the path the
        // pager receives carries the caller's id. A metric tag stores every distinct value it is
        // given — that would be a cardinality explosion and customer text in a metrics store — so the
        // tag is the fixed defaults key the tool layer chose instead.
        using var capture = new MetricCapture();
        var page = """{"results":[{"id":"c-1","createdAt":"2026-09-01T00:00:00Z"}],"next":"more"}""";
        var (client, _) = TestHelpers.CreateMockHttpClientPaged(page, page, page);
        using var _client = client;
        var service = TestHelpers.BuildVitallyService(client, maxAutoPageFetches: 2, metrics: capture.Metrics);

        await service.GetByCreatedRangeAsync("accounts/acctsentinel/conversations",
            "2026-01-01T00:00:00Z", null, null, defaultsKey: "conversations");

        capture.Total("vitally.autopager.truncations", ("resource", "conversations")).Should().Be(1);
        capture.Total("vitally.autopager.truncations", ("resource", "accounts/acctsentinel/conversations"))
            .Should().Be(0, "the caller's id must never become a metric dimension");
    }

    [Fact]
    public async Task Pager_CountsNoTruncation_WhenTheEndpointIsExhausted()
    {
        using var capture = new MetricCapture();
        using var client = TestHelpers.CreateMockHttpClient("""{"results":[{"id":"org-1","name":"Acme"}]}""");
        var service = TestHelpers.BuildVitallyService(client, metrics: capture.Metrics);

        await service.GetByNameContainsAsync("organizations", "Acme");

        capture.Total("vitally.autopager.truncations").Should().Be(0);
    }

    [Fact]
    public async Task UpstreamAuditRecord_CarriesItsDuration_AsANamedProperty()
    {
        // The tool-call record has carried a duration since #147; the upstream record did not, so a
        // slow Get_organization_summary could not say which of its four calls was slow. A NAMED
        // property, so it becomes a queryable AppEvents dimension rather than text to parse.
        var logger = new StateCapturingLogger<AuditLogger>();
        var audit = new AuditLogger(
            Options.Create(new AuditOptions { Enabled = true, IncludeReads = true }), logger);
        using var client = TestHelpers.CreateMockHttpClient("""{"results":[]}""");
        var service = TestHelpers.BuildVitallyService(client, audit: audit);

        await service.GetResourcesAsync("organizations");

        var state = logger.States.Should().ContainSingle().Subject;
        state.Should().Contain(kv => kv.Key == "AuditDurationMs" && kv.Value is long,
            "the duration is recorded in whole milliseconds, like the tool-call record's");
    }
}
