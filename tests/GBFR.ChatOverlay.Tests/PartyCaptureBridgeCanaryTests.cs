using System.Runtime.InteropServices;
using GBFR.ChatOverlay.Audio;
using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class PartyCaptureBridgeCanaryTests
{
    private static readonly nint Manager = 0x1000;
    private static readonly nint Network = 0x2000;
    private static readonly nint LocalUser = 0x3000;
    private static readonly nint LocalDevice = 0x4000;
    private static readonly nint LocalChatControl = 0x5000;
    private static readonly nint RemoteChatControl = 0x6000;
    private static readonly nint Endpoint = 0x7000;
    private static readonly nint CaptureStream = 0x8000;

    [Fact]
    public void CaptureStreamCompletion_IsRequiredBeforeConnect()
    {
        var api = new FakePartyApi();
        var backend = new FakeCaptureBackend();
        using var canary = CreateCanary(api, backend);

        AdvanceThroughAudioConfiguration(canary);

        Assert.DoesNotContain("GetCaptureStream", api.Calls);
        Assert.DoesNotContain("ConnectChatControl", api.Calls);

        ObserveCaptureConfigured(canary);
        canary.OnBatchFinished(Manager);

        Assert.Contains("GetCaptureStream", api.Calls);
        Assert.Contains("GetCaptureFormat", api.Calls);
        Assert.Contains("ConnectChatControl", api.Calls);
    }

    [Fact]
    public void HoldU_SubmitsExactPartyFrame_AndReleaseRejectsStaleFrames()
    {
        var api = new FakePartyApi();
        var backend = new FakeCaptureBackend();
        var logs = new List<string>();
        using var canary = CreateCanary(api, backend, logs.Add);
        AdvanceToVoiceReady(canary);

        canary.SetPushToTalkPressed(true);
        backend.EmitFrame(peak: 0.25f);

        Assert.True(backend.Started);
        Assert.Contains("SetAudioInputMuted:False", api.Calls);
        Assert.Equal(
            WasapiPartyMicrophoneCaptureBackend.PartyBytesPerFrame,
            Assert.Single(api.SubmittedBuffers));
        Assert.Contains(logs, line =>
            line.Contains("capture sink accepted microphone signal", StringComparison.Ordinal));

        canary.SetPushToTalkPressed(false);
        var submittedAtRelease = api.SubmittedBuffers.Count;
        backend.EmitFrame(peak: 0.5f);

        Assert.True(SpinWait.SpinUntil(() => backend.Stopped, TimeSpan.FromSeconds(1)));
        Assert.Contains("SetAudioInputMuted:True", api.Calls);
        Assert.Equal(submittedAtRelease, api.SubmittedBuffers.Count);
        Assert.Contains(logs, line =>
            line.Contains("PASS_PARTY_CAPTURE_SINK_ACCEPTED_MICROPHONE_SIGNAL", StringComparison.Ordinal));
    }

    [Fact]
    public void ReleaseGateRejectsAFrameAndFaultRaisedSynchronouslyByBackendStop()
    {
        var api = new FakePartyApi();
        var backend = new FakeCaptureBackend();
        using var canary = CreateCanary(api, backend);
        AdvanceToVoiceReady(canary);
        canary.SetPushToTalkPressed(true);
        backend.EmitFrame(0.25f);
        var submittedBeforeRelease = api.SubmittedBuffers.Count;
        backend.WhenStopped = () =>
        {
            backend.EmitFrame(0.75f);
            backend.EmitFault(new InvalidOperationException("synchronous stop callback"));
        };

        canary.SetPushToTalkPressed(false);

        Assert.True(SpinWait.SpinUntil(() => backend.Stopped, TimeSpan.FromSeconds(1)));
        Assert.Equal(submittedBeforeRelease, api.SubmittedBuffers.Count);
        Assert.Contains("SetAudioInputMuted:True", api.Calls);
    }

    [Fact]
    public void ThreeConsecutiveSinkFailures_StopCaptureAndFailClosed()
    {
        var api = new FakePartyApi();
        api.SubmitResults.Enqueue(0x55);
        api.SubmitResults.Enqueue(0x55);
        api.SubmitResults.Enqueue(0x55);
        var backend = new FakeCaptureBackend();
        using var canary = CreateCanary(api, backend);
        AdvanceToVoiceReady(canary);
        canary.SetPushToTalkPressed(true);

        backend.EmitFrame(0.2f);
        backend.EmitFrame(0.2f);
        Assert.False(backend.Stopped);

        backend.EmitFrame(0.2f);

        Assert.True(SpinWait.SpinUntil(() => backend.Stopped, TimeSpan.FromSeconds(1)));
        Assert.Contains("SetAudioInputMuted:True", api.Calls);
        Assert.Contains(api.Calls, call => call.StartsWith("DestroyChatControl", StringComparison.Ordinal));
    }

    [Fact]
    public void QueueFullBackpressure_DropsFramesWithoutTeardown_AndLaterSubmissionRecovers()
    {
        var api = new FakePartyApi();
        for (var index = 0; index < 6; index++)
            api.SubmitResults.Enqueue(0x000010D8);
        api.SubmitResults.Enqueue(0);
        var backend = new FakeCaptureBackend();
        var logs = new List<string>();
        using var canary = CreateCanary(api, backend, logs.Add);
        AdvanceToVoiceReady(canary);
        canary.SetPushToTalkPressed(true);

        for (var index = 0; index < 6; index++)
            backend.EmitFrame(0.2f);

        Assert.False(backend.Stopped);
        Assert.DoesNotContain(api.Calls, call => call.StartsWith("DestroyChatControl", StringComparison.Ordinal));
        Assert.Equal(
            3,
            logs.Count(line => line.Contains("capture sink backpressure", StringComparison.Ordinal)));

        backend.EmitFrame(0.2f);
        canary.SetPushToTalkPressed(false);

        Assert.True(SpinWait.SpinUntil(() => backend.Stopped, TimeSpan.FromSeconds(1)));
        Assert.DoesNotContain(api.Calls, call => call.StartsWith("DestroyChatControl", StringComparison.Ordinal));
        Assert.Contains(logs, line =>
            line.Contains("PASS_PARTY_CAPTURE_SINK_ACCEPTED_MICROPHONE_SIGNAL", StringComparison.Ordinal) &&
            line.Contains("backpressureDrops=6", StringComparison.Ordinal));
    }

    [Fact]
    public void SubmitAndEmergencyMuteExceptions_StillStopCaptureAndQueueTeardown()
    {
        var api = new FakePartyApi();
        var backend = new FakeCaptureBackend();
        var logs = new List<string>();
        using var canary = CreateCanary(api, backend, logs.Add);
        AdvanceToVoiceReady(canary);
        canary.SetPushToTalkPressed(true);
        api.SubmitException = new InvalidOperationException("submit exploded");
        api.ThrowOnNextMuteTrue = new InvalidOperationException("mute exploded");

        backend.EmitFrame(0.2f);

        Assert.True(SpinWait.SpinUntil(() => backend.Stopped, TimeSpan.FromSeconds(1)));
        Assert.Contains(api.Calls, call => call.StartsWith("DestroyChatControl", StringComparison.Ordinal));
        Assert.Contains(logs, line =>
            line.Contains("submission threw InvalidOperationException: submit exploded", StringComparison.Ordinal));
        Assert.Contains(logs, line =>
            line.Contains("emergencyMute=threw InvalidOperationException: mute exploded", StringComparison.Ordinal));
    }

    [Fact]
    public void CaptureCompletionStateReader_UsesOfficialPartyOffsets()
    {
        var memory = Marshal.AllocHGlobal(40);
        try
        {
            for (var offset = 0; offset < 40; offset += 4)
                Marshal.WriteInt32(memory, offset, 0);
            Marshal.WriteInt32(
                memory,
                0,
                (int)PartyStateChangeType.ConfigureAudioManipulationCaptureStreamCompleted);
            Marshal.WriteInt32(memory, 4, 0);
            Marshal.WriteInt32(memory, 8, 0x1234);
            Marshal.WriteIntPtr(memory, 16, LocalChatControl);
            Marshal.WriteIntPtr(memory, 24, (nint)0x7777);
            Marshal.WriteIntPtr(memory, 32, (nint)0x8888);

            var snapshot = PartyStateChangeReader.Read(memory);

            Assert.Equal(0u, snapshot.Result);
            Assert.Equal(0x1234u, snapshot.ErrorDetail);
            Assert.Equal(LocalChatControl, snapshot.ChatControl);
            Assert.Equal((nint)0x8888, snapshot.AsyncIdentifier);
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    private static PartyChatControlCanary CreateCanary(
        FakePartyApi api,
        FakeCaptureBackend backend,
        Action<string>? log = null) =>
        new(
            api,
            log ?? (_ => { }),
            action => action(),
            enableVoiceTest: true,
            captureBackendFactory: new FakeCaptureBackendFactory(backend));

    private static void AdvanceThroughAudioConfiguration(PartyChatControlCanary canary)
    {
        canary.CaptureManager(Manager, "test");
        canary.Observe(Manager, new PartyStateChangeSnapshot(
            (uint)PartyStateChangeType.AuthenticateLocalUserCompleted)
        {
            Result = 0,
            Network = Network,
            LocalUser = LocalUser,
        });
        canary.Observe(Manager, new PartyStateChangeSnapshot(
            (uint)PartyStateChangeType.CreateEndpointCompleted)
        {
            Result = 0,
            Network = Network,
            LocalUser = LocalUser,
            Endpoint = Endpoint,
        });
        canary.OnBatchFinished(Manager);

        canary.Observe(Manager, new PartyStateChangeSnapshot(
            (uint)PartyStateChangeType.CreateChatControlCompleted)
        {
            Result = 0,
            LocalDevice = LocalDevice,
            LocalUser = LocalUser,
            ChatControl = LocalChatControl,
            AsyncIdentifier = canary.CreateAsyncIdentifier,
        });
        canary.Observe(Manager, new PartyStateChangeSnapshot(
            (uint)PartyStateChangeType.ChatControlCreated)
        {
            ChatControl = LocalChatControl,
        });
        canary.Observe(Manager, new PartyStateChangeSnapshot(
            (uint)PartyStateChangeType.LocalChatAudioInputChanged)
        {
            ChatControl = LocalChatControl,
            AudioInputState = PartyAudioInputState.Initialized,
        });
        canary.Observe(Manager, new PartyStateChangeSnapshot(
            (uint)PartyStateChangeType.LocalChatAudioOutputChanged)
        {
            ChatControl = LocalChatControl,
            AudioOutputState = PartyAudioOutputState.Initialized,
        });
        canary.Observe(Manager, AudioCompleted(
            PartyStateChangeType.SetChatAudioInputCompleted,
            canary.AudioInputAsyncIdentifier));
        canary.Observe(Manager, AudioCompleted(
            PartyStateChangeType.SetChatAudioOutputCompleted,
            canary.AudioOutputAsyncIdentifier));
    }

    private static void ObserveCaptureConfigured(PartyChatControlCanary canary)
    {
        canary.Observe(Manager, new PartyStateChangeSnapshot(
            (uint)PartyStateChangeType.ConfigureAudioManipulationCaptureStreamCompleted)
        {
            Result = 0,
            ChatControl = LocalChatControl,
            AsyncIdentifier = canary.CaptureStreamAsyncIdentifier,
        });
    }

    private static void AdvanceToVoiceReady(PartyChatControlCanary canary)
    {
        AdvanceThroughAudioConfiguration(canary);
        ObserveCaptureConfigured(canary);
        canary.OnBatchFinished(Manager);
        canary.Observe(Manager, new PartyStateChangeSnapshot(
            (uint)PartyStateChangeType.ConnectChatControlCompleted)
        {
            Result = 0,
            Network = Network,
            ChatControl = LocalChatControl,
            AsyncIdentifier = canary.ConnectAsyncIdentifier,
        });
        canary.Observe(Manager, new PartyStateChangeSnapshot(
            (uint)PartyStateChangeType.ChatControlJoinedNetwork)
        {
            Network = Network,
            ChatControl = LocalChatControl,
        });
        canary.Observe(Manager, new PartyStateChangeSnapshot(
            (uint)PartyStateChangeType.ChatControlJoinedNetwork)
        {
            Network = Network,
            ChatControl = RemoteChatControl,
        });
        canary.OnBatchFinished(Manager);
        Assert.True(canary.IsRemotePushToTalkReady);
    }

    private static PartyStateChangeSnapshot AudioCompleted(
        PartyStateChangeType type,
        nint asyncIdentifier) =>
        new((uint)type)
        {
            Result = 0,
            ChatControl = LocalChatControl,
            Value = (uint)PartyAudioDeviceSelectionType.SystemDefault,
            AudioDeviceSelectionContext = null,
            AsyncIdentifier = asyncIdentifier,
        };

    private sealed class FakeCaptureBackendFactory : IPartyMicrophoneCaptureBackendFactory
    {
        private readonly FakeCaptureBackend _backend;

        public FakeCaptureBackendFactory(FakeCaptureBackend backend)
        {
            _backend = backend;
        }

        public IPartyMicrophoneCaptureBackend Create(ResolvedAudioEndpointSelection inputSelection) =>
            _backend;
    }

    private sealed class FakeCaptureBackend : IPartyMicrophoneCaptureBackend
    {
        public event Action<PartyMicrophoneCaptureFrame>? FrameReady;
        public event Action<Exception>? Faulted;

        public string CaptureFormatDescription => "48000 Hz, 1 channel, float32";
        public bool Started { get; private set; }
        public bool Stopped { get; private set; }
        public Action? WhenStopped { get; set; }

        public void Start() => Started = true;

        public void StopImmediately()
        {
            if (Stopped)
                return;
            Stopped = true;
            WhenStopped?.Invoke();
        }

        public void Dispose() => StopImmediately();

        public void EmitFrame(float peak)
        {
            var samples = Enumerable.Repeat(peak, WasapiPartyMicrophoneCaptureBackend.PartySamplesPerFrame)
                .ToArray();
            var buffer = new byte[WasapiPartyMicrophoneCaptureBackend.PartyBytesPerFrame];
            Buffer.BlockCopy(samples, 0, buffer, 0, buffer.Length);
            FrameReady?.Invoke(new PartyMicrophoneCaptureFrame(buffer, samples.Length, peak));
        }

        public void EmitFault(Exception exception) => Faulted?.Invoke(exception);
    }

    private sealed class FakePartyApi : IPartyChatControlApi
    {
        private bool _muted = true;

        public List<string> Calls { get; } = [];
        public List<int> SubmittedBuffers { get; } = [];
        public Queue<uint> SubmitResults { get; } = new();
        public Exception? SubmitException { get; set; }
        public Exception? ThrowOnNextMuteTrue { get; set; }

        public uint GetLocalDevice(nint manager, out nint localDevice)
        {
            Calls.Add("GetLocalDevice");
            localDevice = LocalDevice;
            return 0;
        }

        public uint GetLocalChatControlCount(nint localDevice, out uint chatControlCount)
        {
            Calls.Add("GetLocalChatControlCount");
            chatControlCount = 0;
            return 0;
        }

        public uint GetNetworkChatControls(nint network, out nint[] chatControls)
        {
            Calls.Add("GetNetworkChatControls");
            chatControls = [];
            return 0;
        }

        public uint CreateChatControl(
            nint localDevice,
            nint localUser,
            nint asyncIdentifier,
            out nint localChatControl)
        {
            Calls.Add("CreateChatControl");
            localChatControl = LocalChatControl;
            return 0;
        }

        public uint DestroyChatControl(nint localDevice, nint localChatControl, nint asyncIdentifier)
        {
            Calls.Add("DestroyChatControl");
            return 0;
        }

        public uint SetAudioInputMuted(nint localChatControl, bool muted)
        {
            Calls.Add($"SetAudioInputMuted:{muted}");
            if (muted && ThrowOnNextMuteTrue is { } exception)
            {
                ThrowOnNextMuteTrue = null;
                throw exception;
            }
            _muted = muted;
            return 0;
        }

        public uint GetAudioInputMuted(nint localChatControl, out bool muted)
        {
            Calls.Add("GetAudioInputMuted");
            muted = _muted;
            return 0;
        }

        public uint GetPermissions(
            nint localChatControl,
            nint targetChatControl,
            out PartyChatPermissionOptions permissions)
        {
            permissions = PartyChatPermissionOptions.SendMicrophoneAudio |
                          PartyChatPermissionOptions.ReceiveMicrophoneAudio;
            return 0;
        }

        public uint GetAudioInput(
            nint localChatControl,
            out PartyAudioDeviceSelectionType selectionType,
            out string? selectionContext,
            out string? deviceId)
        {
            selectionType = PartyAudioDeviceSelectionType.SystemDefault;
            selectionContext = null;
            deviceId = "mic";
            return 0;
        }

        public uint GetAudioOutput(
            nint localChatControl,
            out PartyAudioDeviceSelectionType selectionType,
            out string? selectionContext,
            out string? deviceId)
        {
            selectionType = PartyAudioDeviceSelectionType.SystemDefault;
            selectionContext = null;
            deviceId = "speaker";
            return 0;
        }

        public uint GetAudioRenderVolume(
            nint localChatControl,
            nint targetChatControl,
            out float volume)
        {
            volume = 1;
            return 0;
        }

        public uint GetIncomingAudioMuted(
            nint localChatControl,
            nint targetChatControl,
            out bool muted)
        {
            muted = false;
            return 0;
        }

        public uint GetLocalChatIndicator(
            nint localChatControl,
            out PartyLocalChatControlChatIndicator indicator)
        {
            indicator = SubmittedBuffers.Count == 0
                ? PartyLocalChatControlChatIndicator.Silent
                : PartyLocalChatControlChatIndicator.Talking;
            return 0;
        }

        public uint GetChatIndicator(
            nint localChatControl,
            nint targetChatControl,
            out PartyChatControlChatIndicator indicator)
        {
            indicator = PartyChatControlChatIndicator.Silent;
            return 0;
        }

        public uint GetErrorMessage(uint error, out string? errorMessage)
        {
            errorMessage = "fake";
            return 0;
        }

        public uint SetPermissions(
            nint localChatControl,
            nint targetChatControl,
            PartyChatPermissionOptions permissions)
        {
            Calls.Add("SetPermissions");
            return 0;
        }

        public uint SetAudioInput(
            nint localChatControl,
            PartyAudioDeviceSelectionType selectionType,
            string? selectionContext,
            nint asyncIdentifier)
        {
            Calls.Add("SetAudioInput");
            return 0;
        }

        public uint SetAudioOutput(
            nint localChatControl,
            PartyAudioDeviceSelectionType selectionType,
            string? selectionContext,
            nint asyncIdentifier)
        {
            Calls.Add("SetAudioOutput");
            return 0;
        }

        public uint ConnectChatControl(nint network, nint localChatControl, nint asyncIdentifier)
        {
            Calls.Add("ConnectChatControl");
            return 0;
        }

        public uint DisconnectChatControl(nint network, nint localChatControl, nint asyncIdentifier)
        {
            Calls.Add("DisconnectChatControl");
            return 0;
        }

        public uint ConfigureAudioManipulationCaptureStream(
            nint localChatControl,
            nint asyncIdentifier)
        {
            Calls.Add("ConfigureCaptureStream");
            return 0;
        }

        public uint GetAudioManipulationCaptureStream(
            nint localChatControl,
            out nint captureStream)
        {
            Calls.Add("GetCaptureStream");
            captureStream = CaptureStream;
            return 0;
        }

        public uint GetAudioManipulationSinkFormat(
            nint captureStream,
            out PartyAudioFormatDescriptor format)
        {
            Calls.Add("GetCaptureFormat");
            format = new PartyAudioFormatDescriptor(
                24_000,
                0,
                1,
                32,
                PartyAudioSampleType.Float,
                Interleaved: false);
            return 0;
        }

        public uint SubmitAudioManipulationCaptureBuffer(
            nint captureStream,
            byte[] buffer,
            int count)
        {
            Calls.Add("SubmitCaptureBuffer");
            SubmittedBuffers.Add(count);
            if (SubmitException is { } exception)
            {
                SubmitException = null;
                throw exception;
            }
            return SubmitResults.Count == 0 ? 0 : SubmitResults.Dequeue();
        }
    }
}
