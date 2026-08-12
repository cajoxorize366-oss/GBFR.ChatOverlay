using System.Runtime.InteropServices;
using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class PartyStateChangeReaderTests
{
    [Fact]
    public void Read_AuthenticateLocalUserCompleted_UsesOfficialPack8Offsets()
    {
        var pointer = Marshal.AllocHGlobal(48);
        try
        {
            Zero(pointer, 48);
            Marshal.WriteInt32(pointer, 0, (int)PartyStateChangeType.AuthenticateLocalUserCompleted);
            Marshal.WriteInt32(pointer, 4, 0);
            Marshal.WriteInt32(pointer, 8, unchecked((int)0xAABBCCDD));
            Marshal.WriteIntPtr(pointer, 16, (nint)0x1111);
            Marshal.WriteIntPtr(pointer, 24, (nint)0x2222);
            Marshal.WriteIntPtr(pointer, 40, (nint)0x3333);

            var snapshot = PartyStateChangeReader.Read(pointer);

            Assert.Equal((uint)PartyStateChangeType.AuthenticateLocalUserCompleted, snapshot.Type);
            Assert.Equal(0xAABBCCDDu, snapshot.ErrorDetail);
            Assert.Equal((nint)0x1111, snapshot.Network);
            Assert.Equal((nint)0x2222, snapshot.LocalUser);
            Assert.Equal((nint)0x3333, snapshot.AsyncIdentifier);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    [Fact]
    public void Read_CreateChatControlCompleted_CopiesOwnedHandlesAndAsyncIdentifier()
    {
        var pointer = Marshal.AllocHGlobal(56);
        try
        {
            Zero(pointer, 56);
            Marshal.WriteInt32(pointer, 0, (int)PartyStateChangeType.CreateChatControlCompleted);
            Marshal.WriteInt32(pointer, 4, 0);
            Marshal.WriteIntPtr(pointer, 16, (nint)0x1000);
            Marshal.WriteIntPtr(pointer, 24, (nint)0x2000);
            Marshal.WriteIntPtr(pointer, 40, (nint)0x3000);
            Marshal.WriteIntPtr(pointer, 48, (nint)0x4000);

            var snapshot = PartyStateChangeReader.Read(pointer);

            Assert.Equal((nint)0x1000, snapshot.LocalDevice);
            Assert.Equal((nint)0x2000, snapshot.LocalUser);
            Assert.Equal((nint)0x3000, snapshot.AsyncIdentifier);
            Assert.Equal((nint)0x4000, snapshot.ChatControl);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    [Fact]
    public void Read_DestroyEndpointCompleted_UsesOfficialPack8Offsets()
    {
        var pointer = Marshal.AllocHGlobal(40);
        try
        {
            Zero(pointer, 40);
            Marshal.WriteInt32(pointer, 0, (int)PartyStateChangeType.DestroyEndpointCompleted);
            Marshal.WriteInt32(pointer, 4, 0);
            Marshal.WriteInt32(pointer, 8, unchecked((int)0xAABBCCDD));
            Marshal.WriteIntPtr(pointer, 16, (nint)0x1111);
            Marshal.WriteIntPtr(pointer, 24, (nint)0x2222);
            Marshal.WriteIntPtr(pointer, 32, (nint)0x3333);

            var snapshot = PartyStateChangeReader.Read(pointer);

            Assert.Equal(0u, snapshot.Result);
            Assert.Equal(0xAABBCCDDu, snapshot.ErrorDetail);
            Assert.Equal((nint)0x1111, snapshot.Network);
            Assert.Equal((nint)0x2222, snapshot.Endpoint);
            Assert.Equal((nint)0x3333, snapshot.AsyncIdentifier);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    [Fact]
    public void Read_EndpointDestroyedAndLocalUserKicked_CopyTeardownHandles()
    {
        var endpointPointer = Marshal.AllocHGlobal(32);
        var kickedPointer = Marshal.AllocHGlobal(24);
        try
        {
            Zero(endpointPointer, 32);
            Marshal.WriteInt32(endpointPointer, 0, (int)PartyStateChangeType.EndpointDestroyed);
            Marshal.WriteIntPtr(endpointPointer, 8, (nint)0x1111);
            Marshal.WriteIntPtr(endpointPointer, 16, (nint)0x2222);
            Marshal.WriteInt32(endpointPointer, 24, 3);
            Marshal.WriteInt32(endpointPointer, 28, 0x1234);

            Zero(kickedPointer, 24);
            Marshal.WriteInt32(kickedPointer, 0, (int)PartyStateChangeType.LocalUserKicked);
            Marshal.WriteIntPtr(kickedPointer, 8, (nint)0x3333);
            Marshal.WriteIntPtr(kickedPointer, 16, (nint)0x4444);

            var endpoint = PartyStateChangeReader.Read(endpointPointer);
            var kicked = PartyStateChangeReader.Read(kickedPointer);

            Assert.Equal((nint)0x1111, endpoint.Network);
            Assert.Equal((nint)0x2222, endpoint.Endpoint);
            Assert.Equal(3u, endpoint.Reason);
            Assert.Equal(0x1234u, endpoint.ErrorDetail);
            Assert.Equal((nint)0x3333, kicked.Network);
            Assert.Equal((nint)0x4444, kicked.LocalUser);
        }
        finally
        {
            Marshal.FreeHGlobal(endpointPointer);
            Marshal.FreeHGlobal(kickedPointer);
        }
    }

    [Fact]
    public void Read_JoinedAndLeftNetwork_CopiesRemoteObservableHandles()
    {
        var joinedPointer = Marshal.AllocHGlobal(24);
        var leftPointer = Marshal.AllocHGlobal(32);
        try
        {
            Zero(joinedPointer, 24);
            Marshal.WriteInt32(joinedPointer, 0, (int)PartyStateChangeType.ChatControlJoinedNetwork);
            Marshal.WriteIntPtr(joinedPointer, 8, (nint)0x1111);
            Marshal.WriteIntPtr(joinedPointer, 16, (nint)0x2222);

            Zero(leftPointer, 32);
            Marshal.WriteInt32(leftPointer, 0, (int)PartyStateChangeType.ChatControlLeftNetwork);
            Marshal.WriteInt32(leftPointer, 4, 2);
            Marshal.WriteInt32(leftPointer, 8, 0x1234);
            Marshal.WriteIntPtr(leftPointer, 16, (nint)0x3333);
            Marshal.WriteIntPtr(leftPointer, 24, (nint)0x4444);

            var joined = PartyStateChangeReader.Read(joinedPointer);
            var left = PartyStateChangeReader.Read(leftPointer);

            Assert.Equal((nint)0x1111, joined.Network);
            Assert.Equal((nint)0x2222, joined.ChatControl);
            Assert.Equal(2u, left.Reason);
            Assert.Equal(0x1234u, left.ErrorDetail);
            Assert.Equal((nint)0x3333, left.Network);
            Assert.Equal((nint)0x4444, left.ChatControl);
        }
        finally
        {
            Marshal.FreeHGlobal(joinedPointer);
            Marshal.FreeHGlobal(leftPointer);
        }
    }

    [Fact]
    public void Read_SetChatAudioInputCompleted_CopiesManualEndpointContext()
    {
        var pointer = Marshal.AllocHGlobal(48);
        var context = Marshal.StringToCoTaskMemUTF8("windows-endpoint-id");
        try
        {
            Zero(pointer, 48);
            Marshal.WriteInt32(pointer, 0, (int)PartyStateChangeType.SetChatAudioInputCompleted);
            Marshal.WriteInt32(pointer, 4, 0);
            Marshal.WriteInt32(pointer, 8, 0);
            Marshal.WriteIntPtr(pointer, 16, (nint)0x1111);
            Marshal.WriteInt32(pointer, 24, (int)PartyAudioDeviceSelectionType.Manual);
            Marshal.WriteIntPtr(pointer, 32, context);
            Marshal.WriteIntPtr(pointer, 40, (nint)0x2222);

            var snapshot = PartyStateChangeReader.Read(pointer);

            Assert.Equal((nint)0x1111, snapshot.ChatControl);
            Assert.Equal((uint)PartyAudioDeviceSelectionType.Manual, snapshot.Value);
            Assert.Equal("windows-endpoint-id", snapshot.AudioDeviceSelectionContext);
            Assert.Equal((nint)0x2222, snapshot.AsyncIdentifier);
        }
        finally
        {
            Marshal.FreeCoTaskMem(context);
            Marshal.FreeHGlobal(pointer);
        }
    }

    [Theory]
    [InlineData(
        PartyStateChangeType.LocalChatAudioInputChanged,
        (uint)PartyAudioInputState.UserConsentDenied,
        true)]
    [InlineData(
        PartyStateChangeType.LocalChatAudioOutputChanged,
        (uint)PartyAudioOutputState.AlreadyInUse,
        false)]
    public void Read_LocalChatAudioChanged_UsesOfficialPack8StateAndErrorOffsets(
        PartyStateChangeType type,
        uint state,
        bool input)
    {
        var pointer = Marshal.AllocHGlobal(24);
        try
        {
            Zero(pointer, 24);
            Marshal.WriteInt32(pointer, 0, (int)type);
            Marshal.WriteIntPtr(pointer, 8, (nint)0x1234);
            Marshal.WriteInt32(pointer, 16, unchecked((int)state));
            Marshal.WriteInt32(pointer, 20, unchecked((int)0xAABBCCDD));

            var snapshot = PartyStateChangeReader.Read(pointer);

            Assert.Equal((nint)0x1234, snapshot.ChatControl);
            Assert.Equal(0xAABBCCDDu, snapshot.ErrorDetail);
            if (input)
            {
                Assert.Equal(PartyAudioInputState.UserConsentDenied, snapshot.AudioInputState);
                Assert.Null(snapshot.AudioOutputState);
            }
            else
            {
                Assert.Null(snapshot.AudioInputState);
                Assert.Equal(PartyAudioOutputState.AlreadyInUse, snapshot.AudioOutputState);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    [Fact]
    public void Read_CreateNewNetworkCompleted_UsesOfficialPack8Offsets()
    {
        var pointer = Marshal.AllocHGlobal(72);
        try
        {
            Zero(pointer, 72);
            Marshal.WriteInt32(pointer, 0, (int)PartyStateChangeType.CreateNewNetworkCompleted);
            Marshal.WriteInt32(pointer, 4, 0);
            Marshal.WriteInt32(pointer, 8, unchecked((int)0xAABBCCDD));
            Marshal.WriteIntPtr(pointer, 16, (nint)0x1111);
            Marshal.WriteIntPtr(pointer, 64, (nint)0x2222);

            var snapshot = PartyStateChangeReader.Read(pointer);

            Assert.Equal((uint)PartyStateChangeType.CreateNewNetworkCompleted, snapshot.Type);
            Assert.Equal(0u, snapshot.Result);
            Assert.Equal(0xAABBCCDDu, snapshot.ErrorDetail);
            Assert.Equal((nint)0x1111, snapshot.LocalUser);
            Assert.Equal((nint)0x2222, snapshot.AsyncIdentifier);
            Assert.Equal(nint.Zero, snapshot.Network);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    [Fact]
    public void Read_ConnectToNetworkCompleted_UsesOfficialPack8Offsets()
    {
        var pointer = Marshal.AllocHGlobal(392);
        try
        {
            Zero(pointer, 392);
            Marshal.WriteInt32(pointer, 0, (int)PartyStateChangeType.ConnectToNetworkCompleted);
            Marshal.WriteInt32(pointer, 4, 0);
            Marshal.WriteInt32(pointer, 8, unchecked((int)0xAABBCCDD));
            Marshal.WriteIntPtr(pointer, 376, (nint)0x3333);
            Marshal.WriteIntPtr(pointer, 384, (nint)0x4444);

            var snapshot = PartyStateChangeReader.Read(pointer);

            Assert.Equal((uint)PartyStateChangeType.ConnectToNetworkCompleted, snapshot.Type);
            Assert.Equal(0u, snapshot.Result);
            Assert.Equal(0xAABBCCDDu, snapshot.ErrorDetail);
            Assert.Equal((nint)0x3333, snapshot.AsyncIdentifier);
            Assert.Equal((nint)0x4444, snapshot.Network);
            Assert.Equal(nint.Zero, snapshot.LocalUser);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static void Zero(nint pointer, int length)
    {
        for (var offset = 0; offset < length; offset++)
            Marshal.WriteByte(pointer, offset, 0);
    }
}
