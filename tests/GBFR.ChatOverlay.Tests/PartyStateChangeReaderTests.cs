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

    private static void Zero(nint pointer, int length)
    {
        for (var offset = 0; offset < length; offset++)
            Marshal.WriteByte(pointer, offset, 0);
    }
}
