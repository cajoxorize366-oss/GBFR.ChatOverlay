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
        KeyboardBinding player2MuteKeyboard,
        ControllerBinding player2MuteController,
        KeyboardBinding player3MuteKeyboard,
        ControllerBinding player3MuteController,
        KeyboardBinding player4MuteKeyboard,
        ControllerBinding player4MuteController,
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
        Player2MuteKeyboard = player2MuteKeyboard;
        Player2MuteController = player2MuteController;
        Player3MuteKeyboard = player3MuteKeyboard;
        Player3MuteController = player3MuteController;
        Player4MuteKeyboard = player4MuteKeyboard;
        Player4MuteController = player4MuteController;
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
    internal KeyboardBinding Player2MuteKeyboard { get; }
    internal ControllerBinding Player2MuteController { get; }
    internal KeyboardBinding Player3MuteKeyboard { get; }
    internal ControllerBinding Player3MuteController { get; }
    internal KeyboardBinding Player4MuteKeyboard { get; }
    internal ControllerBinding Player4MuteController { get; }
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
        var player2MuteKeyboard = ParseKeyboard(configuration.Player2MuteKeyboardBinding);
        var player2MuteController = ParseController(configuration.Player2MuteControllerBinding);
        var player3MuteKeyboard = ParseKeyboard(configuration.Player3MuteKeyboardBinding);
        var player3MuteController = ParseController(configuration.Player3MuteControllerBinding);
        var player4MuteKeyboard = ParseKeyboard(configuration.Player4MuteKeyboardBinding);
        var player4MuteController = ParseController(configuration.Player4MuteControllerBinding);
        var quickActions = (configuration.QuickActions ?? [])
            .Where(action => action is not null)
            .Select(action => new ConfiguredQuickAction(
                action.Id,
                action.Enabled,
                action.Name ?? string.Empty,
                action.Kind,
                action.OfficialId,
                action.Text ?? string.Empty,
                ParseKeyboard(action.KeyboardBinding),
                ParseController(action.ControllerBinding)))
            .DistinctBy(action => action.Id, StringComparer.Ordinal)
            .ToArray();

        var nativeBindings = new List<DirectInputHotkeyBinding>(8 + quickActions.Length);
        AddNativeBinding(nativeBindings, openChatKeyboard, ActivationPolicy);
        AddNativeBinding(nativeBindings, settingsKeyboard, SettingsPolicy);
        AddNativeBinding(nativeBindings, EmergencySettingsKeyboard, SettingsPolicy);
        AddNativeBinding(nativeBindings, pushToTalkKeyboard, PushToTalkPolicy);
        AddNativeBinding(nativeBindings, quickActionsKeyboard, QuickActionsPolicy);
        AddNativeBinding(nativeBindings, player2MuteKeyboard, QuickActionsPolicy);
        AddNativeBinding(nativeBindings, player3MuteKeyboard, QuickActionsPolicy);
        AddNativeBinding(nativeBindings, player4MuteKeyboard, QuickActionsPolicy);
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
            configuration.Player2MuteKeyboardBinding,
            configuration.Player2MuteControllerBinding,
            configuration.Player3MuteKeyboardBinding,
            configuration.Player3MuteControllerBinding,
            configuration.Player4MuteKeyboardBinding,
            configuration.Player4MuteControllerBinding,
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
            player2MuteKeyboard,
            player2MuteController,
            player3MuteKeyboard,
            player3MuteController,
            player4MuteKeyboard,
            player4MuteController,
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

internal readonly record struct ConfiguredQuickAction(
    string Id,
    bool Enabled,
    string Name,
    QuickActionKind Kind,
    int OfficialId,
    string Text,
    KeyboardBinding Keyboard,
    ControllerBinding Controller)
{
    internal bool IsConfigured => Kind == QuickActionKind.CustomText
        ? !string.IsNullOrWhiteSpace(Text)
        : OfficialId >= 0 && CommunicationCatalog.TryGetEntry(Kind, OfficialId, out _);

    internal string Signature =>
        $"{Id}\u001D{Enabled}\u001D{Name}\u001D{Kind}\u001D{OfficialId}\u001D{Text}\u001D{Keyboard.Format()}\u001D{Controller.Format()}";
}
