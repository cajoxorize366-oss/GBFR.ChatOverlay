using GBFR.ChatOverlay.Configuration;
using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Input;

internal sealed class HotkeyConfigurationSnapshot
{
    private const byte ActivationPolicy = (byte)DirectInputBrokerPolicy.SuppressActivation;
    private const byte SettingsPolicy = (byte)DirectInputBrokerPolicy.SuppressSettings;
    private const byte PushToTalkPolicy = (byte)DirectInputBrokerPolicy.SuppressPushToTalk;
    private const byte QuickActionsPolicy = (byte)DirectInputBrokerPolicy.SuppressQuickActions;
    internal static KeyboardBinding EmergencySettingsKeyboard { get; } =
        new(0x79, KeyboardModifiers.Control); // Ctrl+F10

    private HotkeyConfigurationSnapshot(
        string signature,
        KeyboardBinding openChatKeyboard,
        ControllerBinding openChatController,
        KeyboardBinding settingsKeyboard,
        ControllerBinding settingsController,
        KeyboardBinding pushToTalkKeyboard,
        ControllerBinding pushToTalkController,
        KeyboardBinding quickActionsKeyboard,
        ControllerBinding quickActionsController,
        KeyboardBinding globalMuteKeyboard,
        ControllerBinding globalMuteController,
        IReadOnlyList<ConfiguredRemotePlayerChatMute> remotePlayerChatMutes,
        IReadOnlyList<ConfiguredQuickAction> quickActions,
        DirectInputHotkeyBinding[] nativeBindings)
    {
        Signature = signature;
        OpenChatKeyboard = openChatKeyboard;
        OpenChatController = openChatController;
        SettingsKeyboard = settingsKeyboard;
        SettingsController = settingsController;
        PushToTalkKeyboard = pushToTalkKeyboard;
        PushToTalkController = pushToTalkController;
        QuickActionsKeyboard = quickActionsKeyboard;
        QuickActionsController = quickActionsController;
        GlobalMuteKeyboard = globalMuteKeyboard;
        GlobalMuteController = globalMuteController;
        RemotePlayerChatMutes = remotePlayerChatMutes;
        QuickActions = quickActions;
        NativeBindings = nativeBindings;
    }

    internal string Signature { get; }
    internal KeyboardBinding OpenChatKeyboard { get; }
    internal ControllerBinding OpenChatController { get; }
    internal KeyboardBinding SettingsKeyboard { get; }
    internal ControllerBinding SettingsController { get; }
    internal KeyboardBinding PushToTalkKeyboard { get; }
    internal ControllerBinding PushToTalkController { get; }
    internal KeyboardBinding QuickActionsKeyboard { get; }
    internal ControllerBinding QuickActionsController { get; }
    internal KeyboardBinding GlobalMuteKeyboard { get; }
    internal ControllerBinding GlobalMuteController { get; }
    internal IReadOnlyList<ConfiguredRemotePlayerChatMute> RemotePlayerChatMutes { get; }
    internal IReadOnlyList<ConfiguredQuickAction> QuickActions { get; }
    internal DirectInputHotkeyBinding[] NativeBindings { get; }

    internal static HotkeyConfigurationSnapshot Create(Config configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var openChatKeyboard = ParseKeyboard(configuration.OpenChatKeyboardBinding);
        var openChatController = ParseController(configuration.OpenChatControllerBinding);
        var settingsKeyboard = ParseKeyboard(configuration.SettingsMenuKeyboardBinding);
        var settingsController = ParseController(configuration.SettingsMenuControllerBinding);
        var pushToTalkKeyboard = ParseKeyboard(configuration.PushToTalkKeyboardBinding);
        var pushToTalkController = ParseController(configuration.PushToTalkControllerBinding);
        var quickActionsKeyboard = ParseKeyboard(configuration.QuickActionsKeyboardBinding);
        var quickActionsController = ParseController(configuration.QuickActionsControllerBinding);
        var globalMuteKeyboard = ParseKeyboard(configuration.GlobalMuteKeyboardBinding);
        var globalMuteController = ParseController(configuration.GlobalMuteControllerBinding);
        ConfiguredRemotePlayerChatMute[] remotePlayerChatMutes =
        [
            new(1, 2, ParseKeyboard(configuration.RemotePlayer1ChatMuteKeyboardBinding), ParseController(configuration.RemotePlayer1ChatMuteControllerBinding)),
            new(2, 3, ParseKeyboard(configuration.RemotePlayer2ChatMuteKeyboardBinding), ParseController(configuration.RemotePlayer2ChatMuteControllerBinding)),
            new(3, 4, ParseKeyboard(configuration.RemotePlayer3ChatMuteKeyboardBinding), ParseController(configuration.RemotePlayer3ChatMuteControllerBinding)),
        ];
        var quickActions = (configuration.QuickActions ?? [])
            .Where(action => action is not null)
            .Select(action => new ConfiguredQuickAction(
                action.Id,
                action.Enabled,
                action.Name ?? string.Empty,
                action.Kind,
                action.OfficialId,
                action.Text ?? string.Empty,
                ParseKeyboard(action.KeyboardBinding)))
            .DistinctBy(action => action.Id, StringComparer.Ordinal)
            .ToArray();

        var nativeBindings = new List<DirectInputHotkeyBinding>(9 + quickActions.Length);
        AddNativeBinding(nativeBindings, openChatKeyboard, ActivationPolicy);
        AddNativeBinding(nativeBindings, settingsKeyboard, SettingsPolicy);
        AddNativeBinding(nativeBindings, EmergencySettingsKeyboard, SettingsPolicy);
        AddNativeBinding(nativeBindings, pushToTalkKeyboard, PushToTalkPolicy);
        AddNativeBinding(nativeBindings, quickActionsKeyboard, QuickActionsPolicy);
        AddNativeBinding(nativeBindings, globalMuteKeyboard, QuickActionsPolicy);
        foreach (var remotePlayerChatMute in remotePlayerChatMutes)
            AddNativeBinding(nativeBindings, remotePlayerChatMute.Keyboard, QuickActionsPolicy);
        foreach (var action in quickActions)
        {
            if (action.Enabled && action.IsConfigured)
                AddNativeBinding(nativeBindings, action.Keyboard, QuickActionsPolicy);
        }

        var distinctBindings = nativeBindings.Distinct().Take(64).ToArray();
        var signature = string.Join('\u001F',
            configuration.OpenChatKeyboardBinding,
            configuration.OpenChatControllerBinding,
            configuration.SettingsMenuKeyboardBinding,
            configuration.SettingsMenuControllerBinding,
            configuration.PushToTalkKeyboardBinding,
            configuration.PushToTalkControllerBinding,
            configuration.QuickActionsKeyboardBinding,
            configuration.QuickActionsControllerBinding,
            configuration.GlobalMuteKeyboardBinding,
            configuration.GlobalMuteControllerBinding,
            configuration.RemotePlayer1ChatMuteKeyboardBinding,
            configuration.RemotePlayer1ChatMuteControllerBinding,
            configuration.RemotePlayer2ChatMuteKeyboardBinding,
            configuration.RemotePlayer2ChatMuteControllerBinding,
            configuration.RemotePlayer3ChatMuteKeyboardBinding,
            configuration.RemotePlayer3ChatMuteControllerBinding,
            string.Join('\u001E', quickActions.Select(action => action.Signature)));

        return new HotkeyConfigurationSnapshot(
            signature,
            openChatKeyboard,
            openChatController,
            settingsKeyboard,
            settingsController,
            pushToTalkKeyboard,
            pushToTalkController,
            quickActionsKeyboard,
            quickActionsController,
            globalMuteKeyboard,
            globalMuteController,
            remotePlayerChatMutes,
            quickActions,
            distinctBindings);
    }

    internal static bool IsPressed(
        in DirectInputBrokerSnapshot snapshot,
        KeyboardBinding keyboard,
        ControllerBinding controller)
    {
        return IsKeyboardPressed(snapshot, keyboard) ||
               controller.IsPressed(snapshot.ControllerButtons);
    }

    internal static bool IsKeyboardPressed(
        in DirectInputBrokerSnapshot snapshot,
        KeyboardBinding binding)
    {
        if (!binding.TryGetDirectInputScanCode(out var scanCode) ||
            !snapshot.IsScanCodePressed(scanCode))
        {
            return false;
        }

        var control = snapshot.IsScanCodePressed(0x1D) || snapshot.IsScanCodePressed(0x9D);
        var shift = snapshot.IsScanCodePressed(0x2A) || snapshot.IsScanCodePressed(0x36);
        var alt = snapshot.IsScanCodePressed(0x38) || snapshot.IsScanCodePressed(0xB8);
        return (!binding.Modifiers.HasFlag(KeyboardModifiers.Control) || control) &&
               (!binding.Modifiers.HasFlag(KeyboardModifiers.Shift) || shift) &&
               (!binding.Modifiers.HasFlag(KeyboardModifiers.Alt) || alt);
    }

    private static KeyboardBinding ParseKeyboard(string? value) =>
        KeyboardBinding.TryParse(value, out var binding) ? binding : default;

    private static ControllerBinding ParseController(string? value) =>
        ControllerBinding.TryParse(value, out var binding) ? binding : default;

    private static void AddNativeBinding(
        ICollection<DirectInputHotkeyBinding> destination,
        KeyboardBinding binding,
        byte policy)
    {
        if (binding.TryGetDirectInputScanCode(out var scanCode))
        {
            destination.Add(new DirectInputHotkeyBinding(
                scanCode,
                binding.Modifiers,
                policy));
        }
    }
}

internal readonly record struct ConfiguredRemotePlayerChatMute(
    int RemotePlayerNumber,
    int ChatPlayerNumber,
    KeyboardBinding Keyboard,
    ControllerBinding Controller);

internal readonly record struct ConfiguredQuickAction(
    string Id,
    bool Enabled,
    string Name,
    QuickActionKind Kind,
    int OfficialId,
    string Text,
    KeyboardBinding Keyboard)
{
    internal bool IsConfigured => Kind == QuickActionKind.CustomText
        ? !string.IsNullOrWhiteSpace(Text)
        : OfficialId >= 0 && CommunicationCatalog.TryGetEntry(Kind, OfficialId, out _);

    internal string Signature =>
        $"{Id}\u001D{Enabled}\u001D{Name}\u001D{Kind}\u001D{OfficialId}\u001D{Text}\u001D{Keyboard.Format()}";
}
