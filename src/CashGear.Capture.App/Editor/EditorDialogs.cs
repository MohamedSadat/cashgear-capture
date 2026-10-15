using System.IO;
using System.Windows;
using CashGear.Capture.App.Platform;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Editor.Imaging;
using CashGear.Capture.Editor.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SkiaSharp;

namespace CashGear.Capture.App.Editor;

public enum SaveChangesChoice
{
    Save,
    Discard,
    Cancel,
}

/// <summary>The questions the editor asks the user (a seam, so the view model can be tested without windows).</summary>
public interface IEditorDialogs
{
    /// <summary>The full path to save to, or null if the user cancelled.</summary>
    string? AskSavePath(string folder, string fileName, ImageFileFormat format);

    SKSizeI? AskImageSize(SKSizeI current);

    (int Thickness, SKColor Color)? AskBorder(SKColor suggested);

    SaveChangesChoice AskSaveChanges(string documentName);

    void ShowError(string message);
}

internal sealed class WpfEditorDialogs(Window owner, AppTheme theme) : IEditorDialogs
{
    private const string Caption = "CashGear Capture";

    public string? AskSavePath(string folder, string fileName, ImageFileFormat format)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save capture as",
            Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg;*.jpeg|Bitmap (*.bmp)|*.bmp",
            FilterIndex = format switch
            {
                ImageFileFormat.Jpeg => 2,
                ImageFileFormat.Bmp => 3,
                _ => 1,
            },
            DefaultExt = format.FileExtension(),
            AddExtension = true,
            OverwritePrompt = true,
            FileName = fileName,
            InitialDirectory = Directory.Exists(folder) ? folder : null,
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    public SKSizeI? AskImageSize(SKSizeI current)
    {
        var viewModel = new ResizeImageViewModel(current);
        var window = new ResizeImageWindow { Owner = owner, DataContext = viewModel };
        Theming.Apply(window, theme);
        return window.ShowDialog() == true && viewModel.IsValid ? viewModel.Result : null;
    }

    public (int Thickness, SKColor Color)? AskBorder(SKColor suggested)
    {
        var viewModel = new BorderViewModel(suggested);
        var window = new BorderWindow { Owner = owner, DataContext = viewModel };
        Theming.Apply(window, theme);
        return window.ShowDialog() == true ? (viewModel.Thickness, viewModel.Color.Value) : null;
    }

    public SaveChangesChoice AskSaveChanges(string documentName) =>
        MessageBox.Show(owner, $"Save the changes to {documentName}?", Caption, MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => SaveChangesChoice.Save,
            MessageBoxResult.No => SaveChangesChoice.Discard,
            _ => SaveChangesChoice.Cancel,
        };

    public void ShowError(string message) =>
        MessageBox.Show(owner, message, Caption, MessageBoxButton.OK, MessageBoxImage.Warning);
}

/// <summary>The Resize image dialog: new width and height, proportional by default.</summary>
public sealed partial class ResizeImageViewModel(SKSizeI original) : ObservableObject
{
    private bool _syncing;

    public SKSizeI Original { get; } = original;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    private int _width = original.Width;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    private int _height = original.Height;

    [ObservableProperty]
    private bool _keepProportions = true;

    public bool IsValid => Width is >= 1 and <= ImageOperations.MaxDimension
        && Height is >= 1 and <= ImageOperations.MaxDimension;

    public SKSizeI Result => new(Width, Height);

    partial void OnWidthChanged(int value) => Sync(() => Height = Scale(value, Original.Height, Original.Width));

    partial void OnHeightChanged(int value) => Sync(() => Width = Scale(value, Original.Width, Original.Height));

    /// <summary>Quick sizes: 25 / 50 / 200 % of the original.</summary>
    [RelayCommand]
    private void SetPercent(string percent)
    {
        var factor = int.Parse(percent, System.Globalization.CultureInfo.InvariantCulture) / 100.0;
        _syncing = true;
        Width = Math.Max(1, (int)Math.Round(Original.Width * factor));
        Height = Math.Max(1, (int)Math.Round(Original.Height * factor));
        _syncing = false;
    }

    private void Sync(Action update)
    {
        if (_syncing || !KeepProportions)
        {
            return;
        }

        _syncing = true;
        update();
        _syncing = false;
    }

    private static int Scale(int value, int to, int from) => Math.Max(1, (int)Math.Round(value * (double)to / from));
}

/// <summary>The Add border dialog: thickness and colour.</summary>
public sealed partial class BorderViewModel(SKColor suggested) : ObservableObject
{
    public IReadOnlyList<ColorSwatch> Palette { get; } = [.. EditorColors.Palette.Select(c => new ColorSwatch(c))];

    [ObservableProperty]
    private int _thickness = 4;

    [ObservableProperty]
    private ColorSwatch _color = new(suggested);

    [RelayCommand]
    private void SetColor(ColorSwatch swatch) => Color = swatch;
}
