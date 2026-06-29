using FluentAssertions;
using VitallyMcp;

namespace VitallyMcp.Tests;

public class StartupGuardsTests
{
    [Fact]
    public void EnsureSafeAuthConfig_NoAuthWithKeyVault_Throws()
    {
        var act = () => StartupGuards.EnsureSafeAuthConfig(noAuth: true, keyVaultUri: "https://kv.vault.azure.net/");
        act.Should().Throw<InvalidOperationException>().WithMessage("*NoAuth*");
    }

    [Theory]
    [InlineData(false, "https://kv.vault.azure.net/")] // auth on + KV: fine (production)
    [InlineData(true, null)]                            // NoAuth + no KV: fine (local dev)
    [InlineData(true, "")]                              // NoAuth + blank KV: fine
    [InlineData(false, null)]                           // auth on + no KV: fine (dev key)
    public void EnsureSafeAuthConfig_SafeCombinations_DoNotThrow(bool noAuth, string? keyVaultUri)
    {
        var act = () => StartupGuards.EnsureSafeAuthConfig(noAuth, keyVaultUri);
        act.Should().NotThrow();
    }
}
