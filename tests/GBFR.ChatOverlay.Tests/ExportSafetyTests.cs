using GBFR.ChatOverlay.Template;
using GBFR.OverlayHub.Contracts;

namespace GBFR.ChatOverlay.Tests;

public sealed class ExportSafetyTests
{
    [Fact]
    public void Startup_ExportsOnlyDependencyFreeOverlayContract()
    {
        Type[] exports = new Startup().GetTypes();

        Type export = Assert.Single(exports);
        Assert.Same(typeof(IGbfrOverlayHub), export);
        Assert.Equal("GBFR.OverlayHub.Contracts", export.Assembly.GetName().Name);
        Assert.DoesNotContain(exports, type =>
            type.Assembly.GetName().Name is "DearImguiSharp" or "Reloaded.Imgui.Hook");
    }
}
