using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using GBFR.ChatOverlay.Configuration;
using HandyControl.Controls;

namespace GBFR.ChatOverlay.Tests;

public sealed class AudioEndpointPropertyEditorTests
{
    [Fact]
    public void GameModAssembly_DoesNotReferenceLauncherUiFrameworks()
    {
        var references = typeof(Config).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();

        Assert.DoesNotContain("GBFR.ChatOverlay.ConfiguratorUI", references);
        Assert.DoesNotContain("HandyControl", references);
        Assert.DoesNotContain("PresentationFramework", references);
    }

    [Fact]
    public void ReloadedPropertyResolver_DefaultsBothDeviceListsToExplicitDefault()
    {
        RunSta(() =>
        {
            var config = new Config();
            var legacyBlankConfig = new Config
            {
                VoiceMicrophoneDeviceId = string.Empty,
                VoicePlaybackDeviceId = string.Empty,
            };

            VerifyDefaultSelection(config, nameof(Config.VoiceMicrophoneDeviceId));
            VerifyDefaultSelection(config, nameof(Config.VoicePlaybackDeviceId));
            VerifyDefaultSelection(legacyBlankConfig, nameof(Config.VoiceMicrophoneDeviceId));
            VerifyDefaultSelection(legacyBlankConfig, nameof(Config.VoicePlaybackDeviceId));
        });
    }

    [Fact]
    public void ReloadedPropertyResolver_UsesFriendlyNameComboBoxesThatPersistRawIds()
    {
        RunSta(() =>
        {
            const string staleInputId = "synthetic-stale-input-endpoint-id";
            const string staleOutputId = "synthetic-stale-output-endpoint-id";
            var config = new Config
            {
                VoiceMicrophoneDeviceId = staleInputId,
                VoicePlaybackDeviceId = staleOutputId,
            };

            VerifyEditor(
                config,
                nameof(Config.VoiceMicrophoneDeviceId),
                staleInputId,
                "GBFR.ChatOverlay.ConfiguratorUI.VoiceMicrophonePropertyEditor");
            VerifyEditor(
                config,
                nameof(Config.VoicePlaybackDeviceId),
                staleOutputId,
                "GBFR.ChatOverlay.ConfiguratorUI.VoicePlaybackPropertyEditor");
        });
    }

    private static void VerifyEditor(
        Config config,
        string propertyName,
        string staleEndpointId,
        string expectedEditorType)
    {
        var descriptor = TypeDescriptor.GetProperties(config)[propertyName]!;
        var editor = new PropertyResolver().ResolveEditor(descriptor);
        Assert.Equal(expectedEditorType, editor.GetType().FullName);

        var propertyItem = new PropertyItem
        {
            Value = config,
            PropertyName = propertyName,
            PropertyType = typeof(string),
            IsReadOnly = false,
        };
        var comboBox = Assert.IsType<System.Windows.Controls.ComboBox>(editor.CreateElement(propertyItem));
        editor.CreateBinding(propertyItem, comboBox);

        Assert.Equal("DisplayName", comboBox.DisplayMemberPath);
        Assert.Equal("Id", comboBox.SelectedValuePath);
        Assert.Equal(staleEndpointId, comboBox.SelectedValue);
        Assert.Contains(
            comboBox.Items.Cast<object>(),
            choice => GetChoiceValue(choice, "Id") == staleEndpointId &&
                      GetChoiceValue(choice, "DisplayName")
                          .Contains("Unavailable saved device", StringComparison.Ordinal));
        Assert.Contains(
            comboBox.Items.Cast<object>(),
            choice => GetChoiceValue(choice, "Id") == "default" &&
                      GetChoiceValue(choice, "DisplayName")
                          .StartsWith("Default", StringComparison.Ordinal));

        comboBox.SelectedValue = "default";
        BindingOperations.GetBindingExpression(comboBox, Selector.SelectedValueProperty)!.UpdateSource();
        Assert.Equal("default", descriptor.GetValue(config));
    }

    private static void VerifyDefaultSelection(Config config, string propertyName)
    {
        var descriptor = TypeDescriptor.GetProperties(config)[propertyName]!;
        var editor = new PropertyResolver().ResolveEditor(descriptor);
        var propertyItem = new PropertyItem
        {
            Value = config,
            PropertyName = propertyName,
            PropertyType = typeof(string),
            IsReadOnly = false,
        };
        var comboBox = Assert.IsType<System.Windows.Controls.ComboBox>(editor.CreateElement(propertyItem));
        editor.CreateBinding(propertyItem, comboBox);

        Assert.Equal("default", comboBox.SelectedValue);
        Assert.Equal("default", descriptor.GetValue(config));
        Assert.StartsWith("Default", GetChoiceValue(comboBox.SelectedItem!, "DisplayName"));
    }

    private static string GetChoiceValue(object choice, string propertyName) =>
        (string)choice.GetType().GetProperty(propertyName)!.GetValue(choice)!;

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
