namespace GBFR.ChatOverlay.Native;

public enum PartyStateChangeType : uint
{
    RegionsChanged = 0,
    DestroyLocalUserCompleted = 1,
    CreateNewNetworkCompleted = 2,
    ConnectToNetworkCompleted = 3,
    AuthenticateLocalUserCompleted = 4,
    NetworkConfigurationMadeAvailable = 5,
    NetworkDescriptorChanged = 6,
    LocalUserRemoved = 7,
    RemoveLocalUserCompleted = 8,
    LocalUserKicked = 9,
    CreateEndpointCompleted = 10,
    DestroyEndpointCompleted = 11,
    EndpointCreated = 12,
    EndpointDestroyed = 13,
    RemoteDeviceCreated = 14,
    RemoteDeviceDestroyed = 15,
    RemoteDeviceJoinedNetwork = 16,
    RemoteDeviceLeftNetwork = 17,
    DevicePropertiesChanged = 18,
    LeaveNetworkCompleted = 19,
    NetworkDestroyed = 20,
    EndpointMessageReceived = 21,
    DataBuffersReturned = 22,
    EndpointPropertiesChanged = 23,
    SynchronizeMessagesBetweenEndpointsCompleted = 26,
    NetworkPropertiesChanged = 27,
    KickDeviceCompleted = 28,
    KickUserCompleted = 29,
    CreateChatControlCompleted = 31,
    DestroyChatControlCompleted = 32,
    ChatControlCreated = 33,
    ChatControlDestroyed = 34,
    SetChatAudioEncoderBitrateCompleted = 35,
    ChatTextReceived = 36,
    VoiceChatTranscriptionReceived = 37,
    SetChatAudioInputCompleted = 38,
    SetChatAudioOutputCompleted = 39,
    LocalChatAudioInputChanged = 40,
    LocalChatAudioOutputChanged = 41,
    SetTextToSpeechProfileCompleted = 42,
    SynthesizeTextToSpeechCompleted = 43,
    ChatControlPropertiesChanged = 45,
    ChatControlJoinedNetwork = 46,
    ChatControlLeftNetwork = 47,
    ConnectChatControlCompleted = 48,
    DisconnectChatControlCompleted = 49,
    PopulateAvailableTextToSpeechProfilesCompleted = 50,
    CreateInvitationCompleted = 51,
    RevokeInvitationCompleted = 52,
    InvitationCreated = 53,
    InvitationDestroyed = 54,
    SetLanguageCompleted = 55,
    SetTranscriptionOptionsCompleted = 56,
    SetTextChatOptionsCompleted = 57,
    ConfigureAudioManipulationVoiceStreamCompleted = 58,
    ConfigureAudioManipulationCaptureStreamCompleted = 59,
    ConfigureAudioManipulationRenderStreamCompleted = 60,
}

public static class PartyStateChangeCatalog
{
    public static string GetName(uint value)
    {
        var type = (PartyStateChangeType)value;
        return Enum.IsDefined(type) ? type.ToString() : $"Unknown({value})";
    }

    public static bool IsLifecycle(uint value)
    {
        return (PartyStateChangeType)value is
            PartyStateChangeType.RegionsChanged or
            PartyStateChangeType.DestroyLocalUserCompleted or
            PartyStateChangeType.CreateNewNetworkCompleted or
            PartyStateChangeType.ConnectToNetworkCompleted or
            PartyStateChangeType.AuthenticateLocalUserCompleted or
            PartyStateChangeType.NetworkConfigurationMadeAvailable or
            PartyStateChangeType.NetworkDescriptorChanged or
            PartyStateChangeType.LocalUserRemoved or
            PartyStateChangeType.RemoveLocalUserCompleted or
            PartyStateChangeType.LocalUserKicked or
            PartyStateChangeType.CreateEndpointCompleted or
            PartyStateChangeType.DestroyEndpointCompleted or
            PartyStateChangeType.EndpointCreated or
            PartyStateChangeType.EndpointDestroyed or
            PartyStateChangeType.RemoteDeviceCreated or
            PartyStateChangeType.RemoteDeviceDestroyed or
            PartyStateChangeType.RemoteDeviceJoinedNetwork or
            PartyStateChangeType.RemoteDeviceLeftNetwork or
            PartyStateChangeType.LeaveNetworkCompleted or
            PartyStateChangeType.NetworkDestroyed or
            PartyStateChangeType.CreateChatControlCompleted or
            PartyStateChangeType.DestroyChatControlCompleted or
            PartyStateChangeType.ChatControlCreated or
            PartyStateChangeType.ChatControlDestroyed or
            PartyStateChangeType.SetChatAudioInputCompleted or
            PartyStateChangeType.SetChatAudioOutputCompleted or
            PartyStateChangeType.LocalChatAudioInputChanged or
            PartyStateChangeType.LocalChatAudioOutputChanged or
            PartyStateChangeType.ChatControlPropertiesChanged or
            PartyStateChangeType.ChatControlJoinedNetwork or
            PartyStateChangeType.ChatControlLeftNetwork or
            PartyStateChangeType.ConnectChatControlCompleted or
            PartyStateChangeType.DisconnectChatControlCompleted or
            PartyStateChangeType.CreateInvitationCompleted or
            PartyStateChangeType.RevokeInvitationCompleted or
            PartyStateChangeType.InvitationCreated or
            PartyStateChangeType.InvitationDestroyed;
    }
}
