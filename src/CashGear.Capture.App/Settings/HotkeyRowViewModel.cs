using CashGear.Capture.App.Hotkeys;
using CashGear.Capture.Core.Hotkeys;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CashGear.Capture.App.Settings;

/// <summary>One hotkey in the settings window: the key text being edited and its status.</summary>
public sealed partial class HotkeyRowViewModel : ObservableObject
{
    public HotkeyRowViewModel(HotkeyAction action, string? text)
    {
        Action = action;
        Label = HotkeyRegistrar.Label(action) + (action == HotkeyAction.CaptureScrolling ? " (Phase 6)" : string.Empty);
        _text = text ?? string.Empty;
    }

    public HotkeyAction Action { get; }

    public string Label { get; }

    /// <summary>The hotkey as text (e.g. <c>Ctrl+PrintScreen</c>); empty for none.</summary>
    [ObservableProperty]
    private string _text;

    /// <summary>"Active", "Not set", or why it does not work.</summary>
    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _hasProblem;

    /// <summary>The text as the settings expect it: null when cleared.</summary>
    public string? SettingText => string.IsNullOrWhiteSpace(Text) ? null : Text.Trim();

    [RelayCommand]
    private void Clear() => Text = string.Empty;

    internal void SetStatus(string status, bool problem)
    {
        Status = status;
        HasProblem = problem;
    }
}
