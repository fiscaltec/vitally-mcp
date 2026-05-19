using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace VitallyMcp.Tests;

public class VitallyApiKeyProviderTests
{
    [Fact]
    public async Task GetApiKeyAsync_NoSecretClient_ReturnsDevelopmentApiKey()
    {
        var provider = BuildProvider(new VitallyServerOptions
        {
            DevelopmentApiKey = "sk_live_dev_test"
        });

        var key = await provider.GetApiKeyAsync();

        key.Should().Be("sk_live_dev_test");
    }

    [Fact]
    public async Task GetApiKeyAsync_NoSecretClient_NoDevApiKey_Throws()
    {
        var provider = BuildProvider(new VitallyServerOptions
        {
            DevelopmentApiKey = null
        });

        var act = async () => await provider.GetApiKeyAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no Key Vault client*DevelopmentApiKey*");
    }

    private static VitallyApiKeyProvider BuildProvider(VitallyServerOptions options) =>
        new(
            Options.Create(options),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<VitallyApiKeyProvider>.Instance);
}
