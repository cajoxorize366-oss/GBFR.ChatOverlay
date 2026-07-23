using System.Runtime.InteropServices;

namespace GBFR.ChatOverlay.Native;

internal readonly record struct PartyStateChangeSnapshot(uint Type)
{
    public uint Result { get; init; }

    public uint ErrorDetail { get; init; }

    public uint Reason { get; init; }

    public uint Value { get; init; }

    public nint Network { get; init; }

    public nint LocalUser { get; init; }

    public nint LocalDevice { get; init; }

    public nint ChatControl { get; init; }

    public nint AsyncIdentifier { get; init; }

    public nint Endpoint { get; init; }
}

/// <summary>
/// Copies the small set of Party 1.10.12 state-change fields needed by the canary while the
/// game's StartProcessingStateChanges batch is still valid. Offsets come from the official
/// 64-bit Party_c.h structures under #pragma pack(push, 8).
/// </summary>
internal static class PartyStateChangeReader
{
    public static PartyStateChangeSnapshot Read(nint stateChange)
    {
        if (stateChange == nint.Zero)
            throw new ArgumentException("The Party state-change pointer is null.", nameof(stateChange));
        if (nint.Size != 8)
            throw new PlatformNotSupportedException("Party state-change parsing requires a 64-bit process.");

        var type = ReadUInt32(stateChange, 0);
        return (PartyStateChangeType)type switch
        {
            PartyStateChangeType.AuthenticateLocalUserCompleted =>
                ReadAuthenticateLocalUserCompleted(stateChange, type),
            PartyStateChangeType.CreateEndpointCompleted =>
                ReadCreateEndpointCompleted(stateChange, type),
            PartyStateChangeType.DestroyLocalUserCompleted =>
                ReadDestroyLocalUserCompleted(stateChange, type),
            PartyStateChangeType.LocalUserRemoved =>
                ReadLocalUserRemoved(stateChange, type),
            PartyStateChangeType.RemoveLocalUserCompleted =>
                ReadRemoveLocalUserCompleted(stateChange, type),
            PartyStateChangeType.LeaveNetworkCompleted =>
                ReadLeaveNetworkCompleted(stateChange, type),
            PartyStateChangeType.NetworkDestroyed =>
                ReadNetworkDestroyed(stateChange, type),
            PartyStateChangeType.CreateChatControlCompleted =>
                ReadCreateChatControlCompleted(stateChange, type),
            PartyStateChangeType.DestroyChatControlCompleted =>
                ReadDestroyChatControlCompleted(stateChange, type),
            PartyStateChangeType.ChatControlCreated =>
                new PartyStateChangeSnapshot(type)
                {
                    ChatControl = Marshal.ReadIntPtr(stateChange, 8),
                },
            PartyStateChangeType.ChatControlDestroyed =>
                new PartyStateChangeSnapshot(type)
                {
                    ChatControl = Marshal.ReadIntPtr(stateChange, 8),
                    Reason = ReadUInt32(stateChange, 16),
                    ErrorDetail = ReadUInt32(stateChange, 20),
                },
            PartyStateChangeType.SetChatAudioInputCompleted or
            PartyStateChangeType.SetChatAudioOutputCompleted =>
                ReadSetChatAudioDeviceCompleted(stateChange, type),
            PartyStateChangeType.ChatControlJoinedNetwork =>
                new PartyStateChangeSnapshot(type)
                {
                    Network = Marshal.ReadIntPtr(stateChange, 8),
                    ChatControl = Marshal.ReadIntPtr(stateChange, 16),
                },
            PartyStateChangeType.ChatControlLeftNetwork =>
                new PartyStateChangeSnapshot(type)
                {
                    Reason = ReadUInt32(stateChange, 4),
                    ErrorDetail = ReadUInt32(stateChange, 8),
                    Network = Marshal.ReadIntPtr(stateChange, 16),
                    ChatControl = Marshal.ReadIntPtr(stateChange, 24),
                },
            PartyStateChangeType.ConnectChatControlCompleted or
            PartyStateChangeType.DisconnectChatControlCompleted =>
                ReadChatControlNetworkOperationCompleted(stateChange, type),
            _ => new PartyStateChangeSnapshot(type),
        };
    }

    private static PartyStateChangeSnapshot ReadAuthenticateLocalUserCompleted(nint pointer, uint type) =>
        new(type)
        {
            Result = ReadUInt32(pointer, 4),
            ErrorDetail = ReadUInt32(pointer, 8),
            Network = Marshal.ReadIntPtr(pointer, 16),
            LocalUser = Marshal.ReadIntPtr(pointer, 24),
            AsyncIdentifier = Marshal.ReadIntPtr(pointer, 40),
        };

    private static PartyStateChangeSnapshot ReadCreateEndpointCompleted(nint pointer, uint type) =>
        new(type)
        {
            Result = ReadUInt32(pointer, 4),
            ErrorDetail = ReadUInt32(pointer, 8),
            Network = Marshal.ReadIntPtr(pointer, 16),
            LocalUser = Marshal.ReadIntPtr(pointer, 24),
            AsyncIdentifier = Marshal.ReadIntPtr(pointer, 32),
            Endpoint = Marshal.ReadIntPtr(pointer, 40),
        };

    private static PartyStateChangeSnapshot ReadDestroyLocalUserCompleted(nint pointer, uint type) =>
        new(type)
        {
            Result = ReadUInt32(pointer, 4),
            ErrorDetail = ReadUInt32(pointer, 8),
            LocalUser = Marshal.ReadIntPtr(pointer, 16),
            AsyncIdentifier = Marshal.ReadIntPtr(pointer, 24),
        };

    private static PartyStateChangeSnapshot ReadLocalUserRemoved(nint pointer, uint type) =>
        new(type)
        {
            Network = Marshal.ReadIntPtr(pointer, 8),
            LocalUser = Marshal.ReadIntPtr(pointer, 16),
            Reason = ReadUInt32(pointer, 24),
        };

    private static PartyStateChangeSnapshot ReadRemoveLocalUserCompleted(nint pointer, uint type) =>
        new(type)
        {
            Result = ReadUInt32(pointer, 4),
            ErrorDetail = ReadUInt32(pointer, 8),
            Network = Marshal.ReadIntPtr(pointer, 16),
            LocalUser = Marshal.ReadIntPtr(pointer, 24),
            AsyncIdentifier = Marshal.ReadIntPtr(pointer, 32),
        };

    private static PartyStateChangeSnapshot ReadLeaveNetworkCompleted(nint pointer, uint type) =>
        new(type)
        {
            Result = ReadUInt32(pointer, 4),
            ErrorDetail = ReadUInt32(pointer, 8),
            Network = Marshal.ReadIntPtr(pointer, 16),
            AsyncIdentifier = Marshal.ReadIntPtr(pointer, 24),
        };

    private static PartyStateChangeSnapshot ReadNetworkDestroyed(nint pointer, uint type) =>
        new(type)
        {
            Reason = ReadUInt32(pointer, 4),
            ErrorDetail = ReadUInt32(pointer, 8),
            Network = Marshal.ReadIntPtr(pointer, 16),
        };

    private static PartyStateChangeSnapshot ReadCreateChatControlCompleted(nint pointer, uint type) =>
        new(type)
        {
            Result = ReadUInt32(pointer, 4),
            ErrorDetail = ReadUInt32(pointer, 8),
            LocalDevice = Marshal.ReadIntPtr(pointer, 16),
            LocalUser = Marshal.ReadIntPtr(pointer, 24),
            AsyncIdentifier = Marshal.ReadIntPtr(pointer, 40),
            ChatControl = Marshal.ReadIntPtr(pointer, 48),
        };

    private static PartyStateChangeSnapshot ReadDestroyChatControlCompleted(nint pointer, uint type) =>
        new(type)
        {
            Result = ReadUInt32(pointer, 4),
            ErrorDetail = ReadUInt32(pointer, 8),
            LocalDevice = Marshal.ReadIntPtr(pointer, 16),
            ChatControl = Marshal.ReadIntPtr(pointer, 24),
            AsyncIdentifier = Marshal.ReadIntPtr(pointer, 32),
        };

    private static PartyStateChangeSnapshot ReadSetChatAudioDeviceCompleted(nint pointer, uint type) =>
        new(type)
        {
            Result = ReadUInt32(pointer, 4),
            ErrorDetail = ReadUInt32(pointer, 8),
            ChatControl = Marshal.ReadIntPtr(pointer, 16),
            Value = ReadUInt32(pointer, 24),
            AsyncIdentifier = Marshal.ReadIntPtr(pointer, 40),
        };

    private static PartyStateChangeSnapshot ReadChatControlNetworkOperationCompleted(nint pointer, uint type) =>
        new(type)
        {
            Result = ReadUInt32(pointer, 4),
            ErrorDetail = ReadUInt32(pointer, 8),
            Network = Marshal.ReadIntPtr(pointer, 16),
            ChatControl = Marshal.ReadIntPtr(pointer, 24),
            AsyncIdentifier = Marshal.ReadIntPtr(pointer, 32),
        };

    private static uint ReadUInt32(nint pointer, int offset) =>
        unchecked((uint)Marshal.ReadInt32(pointer, offset));
}
