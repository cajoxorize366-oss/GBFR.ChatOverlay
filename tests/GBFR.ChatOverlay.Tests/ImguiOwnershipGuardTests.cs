using System.Reflection;
using GBFR.OverlayHub.Runtime;
using Reloaded.Imgui.Hook;

namespace GBFR.ChatOverlay.Tests;

public sealed class ImguiOwnershipGuardTests
{
    [Fact]
    public void SharedImguiHookClaim_DetectsAnExistingUncoordinatedRenderOwner()
    {
        var renderField = typeof(ImguiHook).GetField(
            "<Render>k__BackingField",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(renderField);

        lock (typeof(ImguiHook))
        {
            object? previous = renderField.GetValue(null);
            try
            {
                renderField.SetValue(null, (Action)(static () => { }));
                Assert.True(OverlayBrokerHost.IsSharedImguiHookClaimed());
            }
            finally
            {
                renderField.SetValue(null, previous);
            }
        }
    }
}
