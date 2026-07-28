using GBFR.OverlayHub.Contracts;
using System.IO;
using System.Runtime.Loader;
using Xunit;

namespace GBFR.ChatOverlay.Tests;

public sealed class SharedOverlayHubTests
{
    private static readonly OverlayGraphicsBinding TestGraphicsBinding = new(
        OverlayHubProtocol.GraphicsBindingVersion,
        new nint(1),
        new nint(2));

    [Fact]
    public void TickAndRenderClients_UsesOneHostFrameAndHonorsVisibility()
    {
        var endpoints = OverlayBrokerFactory.Create("test-host", _ => { });
        var hub = endpoints.Hub;
        var host = endpoints.Host;
        var visible = new FakeClient("visible") { WantsRenderValue = true };
        var hidden = new FakeClient("hidden") { WantsRenderValue = false };
        using var visibleRegistration = hub.Register(visible);
        using var hiddenRegistration = hub.Register(hidden);
        Assert.True(visibleRegistration.SetEnabled(true));
        Assert.True(hiddenRegistration.SetEnabled(true));
        host.PublishGraphicsBinding(TestGraphicsBinding);
        host.MarkGraphicsReady();

        host.TickClients();
        Assert.True(host.HasRenderableClients());
        host.RenderClients();

        Assert.Equal(1, visible.TickCount);
        Assert.Equal(1, visible.RenderCount);
        Assert.Equal(1, hidden.TickCount);
        Assert.Equal(0, hidden.RenderCount);
    }

    [Fact]
    public void InputCapture_IsUnionedAndReleasedOnlyAfterLastOwner()
    {
        var transitions = new List<OverlayInputDevices>();
        var endpoints = OverlayBrokerFactory.Create("test-host", _ => { });
        var hub = endpoints.Hub;
        var host = endpoints.Host;
        host.SetInputCaptureChangedCallback(transitions.Add);
        using var first = hub.Register(new FakeClient("first"));
        using var second = hub.Register(new FakeClient("second"));
        Assert.True(first.SetEnabled(true));
        Assert.True(second.SetEnabled(true));
        host.PublishGraphicsBinding(TestGraphicsBinding);
        host.MarkGraphicsReady();

        Assert.True(first.SetInputCapture(OverlayInputDevices.Mouse));
        Assert.True(second.SetInputCapture(OverlayInputDevices.Mouse));
        Assert.True(first.SetInputCapture(OverlayInputDevices.None));
        Assert.True(second.SetInputCapture(OverlayInputDevices.None));

        Assert.Equal(
            [OverlayInputDevices.None, OverlayInputDevices.Mouse, OverlayInputDevices.None],
            transitions);
    }

    [Fact]
    public void GraphicsSuspension_ReleasesGuestInputAndResumeRestoresItsRequest()
    {
        var transitions = new List<OverlayInputDevices>();
        var endpoints = OverlayBrokerFactory.Create("test-host", _ => { });
        var hub = endpoints.Hub;
        var host = endpoints.Host;
        host.SetInputCaptureChangedCallback(transitions.Add);
        using var registration = hub.Register(new FakeClient("input"));
        Assert.True(registration.SetEnabled(true));
        host.PublishGraphicsBinding(TestGraphicsBinding);
        host.MarkGraphicsReady();

        Assert.True(registration.SetInputCapture(
            OverlayInputDevices.Keyboard | OverlayInputDevices.Mouse | OverlayInputDevices.Text));
        Assert.Equal(
            OverlayInputDevices.Keyboard | OverlayInputDevices.Mouse | OverlayInputDevices.Text,
            hub.CapturedInputDevices);

        host.MarkGraphicsSuspended();

        Assert.Equal(OverlayInputDevices.None, hub.CapturedInputDevices);

        host.MarkGraphicsReady();

        Assert.Equal(
            OverlayInputDevices.Keyboard | OverlayInputDevices.Mouse | OverlayInputDevices.Text,
            hub.CapturedInputDevices);
        Assert.Equal(
            [
                OverlayInputDevices.None,
                OverlayInputDevices.Keyboard | OverlayInputDevices.Mouse | OverlayInputDevices.Text,
                OverlayInputDevices.None,
                OverlayInputDevices.Keyboard | OverlayInputDevices.Mouse | OverlayInputDevices.Text,
            ],
            transitions);
    }

    [Fact]
    public void FaultingClient_IsIsolatedFromOtherClients()
    {
        var endpoints = OverlayBrokerFactory.Create("test-host", _ => { });
        var hub = endpoints.Hub;
        var host = endpoints.Host;
        var faulting = new FakeClient("faulting")
        {
            WantsRenderValue = true,
            ThrowOnRender = true,
        };
        var healthy = new FakeClient("healthy") { WantsRenderValue = true };
        using var faultingRegistration = hub.Register(faulting);
        using var healthyRegistration = hub.Register(healthy);
        Assert.True(faultingRegistration.SetEnabled(true));
        Assert.True(healthyRegistration.SetEnabled(true));
        host.PublishGraphicsBinding(TestGraphicsBinding);
        host.MarkGraphicsReady();

        host.TickClients();
        host.RenderClients();
        host.TickClients();
        host.RenderClients();

        Assert.Equal(1, faulting.RenderCount);
        Assert.Equal(1, faulting.HostUnavailableCount);
        Assert.Equal(2, healthy.RenderCount);
    }

    [Fact]
    public void BootstrapCarrierBusinessPeerFailure_DoesNotStopGuest()
    {
        var endpoints = OverlayBrokerFactory.Create("bootstrap", _ => { });
        var bootstrapBusiness = new FakeClient("bootstrap")
        {
            WantsRenderValue = true,
            ThrowOnRender = true,
        };
        var guest = new FakeClient("guest") { WantsRenderValue = true };
        using var bootstrapRegistration = endpoints.Hub.Register(bootstrapBusiness);
        using var guestRegistration = endpoints.Hub.Register(guest);
        Assert.True(bootstrapRegistration.SetEnabled(true));
        Assert.True(guestRegistration.SetEnabled(true));
        endpoints.Host.PublishGraphicsBinding(TestGraphicsBinding);
        endpoints.Host.MarkGraphicsReady();

        endpoints.Host.RenderClients();
        endpoints.Host.RenderClients();

        Assert.True(endpoints.Hub.IsGraphicsReady);
        Assert.Equal("bootstrap", endpoints.Hub.HostModId);
        Assert.Equal(1, bootstrapBusiness.RenderCount);
        Assert.Equal(2, guest.RenderCount);
    }

    [Fact]
    public void TickAndWndProcFailures_AreQuarantinedPerPeer()
    {
        var endpoints = OverlayBrokerFactory.Create("bootstrap", _ => { });
        var tickFault = new FakeClient("tick-fault") { ThrowOnTick = true };
        var wndProcFault = new FakeClient("wndproc-fault") { ThrowOnWindowMessage = true };
        var guest = new FakeClient("guest")
        {
            MessageResult = OverlayWindowMessageResult.HandledWith(new nint(77)),
        };
        using var tickRegistration = endpoints.Hub.Register(tickFault);
        using var wndProcRegistration = endpoints.Hub.Register(wndProcFault);
        using var guestRegistration = endpoints.Hub.Register(guest);
        Assert.True(tickRegistration.SetEnabled(true));
        Assert.True(wndProcRegistration.SetEnabled(true));
        Assert.True(guestRegistration.SetEnabled(true));
        endpoints.Host.PublishGraphicsBinding(TestGraphicsBinding);
        endpoints.Host.MarkGraphicsReady();

        endpoints.Host.TickClients();
        var result = endpoints.Host.ObserveWindowMessage(nint.Zero, 0x0100, nint.Zero, nint.Zero);
        endpoints.Host.TickClients();

        Assert.Equal(1, tickFault.TickCount);
        Assert.Equal(1, tickFault.HostUnavailableCount);
        Assert.Equal(1, wndProcFault.HostUnavailableCount);
        Assert.Equal(2, guest.TickCount);
        Assert.True(result.Handled);
        Assert.Equal(new nint(77), result.Result);
        Assert.True(endpoints.Hub.IsGraphicsReady);
    }

    [Fact]
    public void WindowMessage_FirstHandledGuestResultIsReturned()
    {
        var endpoints = OverlayBrokerFactory.Create("test-host", _ => { });
        var hub = endpoints.Hub;
        var host = endpoints.Host;
        var client = new FakeClient("input")
        {
            MessageResult = OverlayWindowMessageResult.HandledWith(new nint(42)),
        };
        using var registration = hub.Register(client);
        Assert.True(registration.SetEnabled(true));
        host.PublishGraphicsBinding(TestGraphicsBinding);
        host.MarkGraphicsReady();

        var result = host.ObserveWindowMessage(nint.Zero, 0x0102, nint.Zero, nint.Zero);

        Assert.True(result.Handled);
        Assert.Equal(new nint(42), result.Result);
    }

    [Fact]
    public void Register_RejectsClientWithoutSharedGraphicsBindingContract()
    {
        var hub = OverlayBrokerFactory.Create("test-host", _ => { }).Hub;

        var exception = Assert.Throws<NotSupportedException>(() =>
            hub.Register(new LegacyClient()));

        Assert.Contains("does not implement", exception.Message);
    }

    [Fact]
    public void GraphicsBinding_IsAcceptedBeforeClientCanRender()
    {
        var endpoints = OverlayBrokerFactory.Create("test-host", _ => { });
        var hub = endpoints.Hub;
        var host = endpoints.Host;
        var client = new FakeClient("bound") { WantsRenderValue = true };
        using var registration = hub.Register(client);
        Assert.True(registration.SetEnabled(true));

        host.PublishGraphicsBinding(TestGraphicsBinding);
        host.MarkGraphicsReady();
        host.TickClients();
        host.RenderClients();

        Assert.Equal(1, client.GraphicsBindCount);
        Assert.Equal(TestGraphicsBinding.NativeLibraryHandle, client.LastGraphicsBinding.NativeLibraryHandle);
        Assert.Equal(TestGraphicsBinding.ContextPointer, client.LastGraphicsBinding.ContextPointer);
        Assert.Equal(1, client.RenderCount);
    }

    [Fact]
    public void CanonicalContract_CanBeResolvedFromTwoPackagePaths()
    {
        string originalPath = typeof(IGbfrOverlayHub).Assembly.Location;
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "gbfr-overlay-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        string copiedPath = Path.Combine(temporaryDirectory, Path.GetFileName(originalPath));
        File.Copy(originalPath, copiedPath);
        var context = new AssemblyLoadContext(
            "GBFR.OverlayHub.ContractPathTest",
            isCollectible: true);
        try
        {
            var first = context.LoadFromAssemblyPath(originalPath);
            var second = context.LoadFromAssemblyPath(copiedPath);
            Assert.Same(first, second);
        }
        finally
        {
            context.Unload();
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private sealed class FakeClient : IGbfrOverlayGraphicsClient
    {
        internal FakeClient(string modId)
        {
            ModId = modId;
        }

        public string ModId { get; }
        public bool WantsRender => WantsRenderValue;
        internal bool WantsRenderValue { get; init; }
        internal bool ThrowOnRender { get; init; }
        internal bool ThrowOnTick { get; init; }
        internal bool ThrowOnWindowMessage { get; init; }
        internal int TickCount { get; private set; }
        internal int RenderCount { get; private set; }
        internal int HostUnavailableCount { get; private set; }
        internal int GraphicsBindCount { get; private set; }
        internal OverlayGraphicsBinding LastGraphicsBinding { get; private set; }
        internal OverlayWindowMessageResult MessageResult { get; init; }

        public bool BindGraphics(OverlayGraphicsBinding binding)
        {
            GraphicsBindCount++;
            LastGraphicsBinding = binding;
            return true;
        }

        public void Tick()
        {
            TickCount++;
            if (ThrowOnTick)
                throw new InvalidOperationException("tick failure");
        }

        public void Render()
        {
            RenderCount++;
            if (ThrowOnRender)
                throw new InvalidOperationException("test failure");
        }

        public OverlayWindowMessageResult ObserveWindowMessage(
            nint windowHandle,
            uint message,
            nint wParam,
            nint lParam)
        {
            if (ThrowOnWindowMessage)
                throw new InvalidOperationException("WndProc failure");
            return MessageResult;
        }

        public void OnHostUnavailable(string reason) => HostUnavailableCount++;
    }

    private sealed class LegacyClient : IGbfrOverlayClient
    {
        public string ModId => "legacy";
        public bool WantsRender => false;
        public void Tick() { }
        public void Render() { }
        public OverlayWindowMessageResult ObserveWindowMessage(
            nint windowHandle,
            uint message,
            nint wParam,
            nint lParam) => OverlayWindowMessageResult.Continue;
        public void OnHostUnavailable(string reason) { }
    }
}
