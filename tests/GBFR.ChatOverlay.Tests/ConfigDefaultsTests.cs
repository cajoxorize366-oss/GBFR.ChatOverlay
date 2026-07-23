using GBFR.ChatOverlay.Configuration;

namespace GBFR.ChatOverlay.Tests;

public sealed class ConfigDefaultsTests
{
    [Fact]
    public void PartyLifecycleProbe_IsEnabledForValidationBuilds()
    {
        Assert.True(new Config().EnablePartyLifecycleProbe);
    }
}
