using Avalonia.Controls;
using Avalonia.Interactivity;
using SharpTurns.App.ViewModels;

namespace SharpTurns.App.Views;

/// <summary>Closes with the turn the user chose to go to, or null.</summary>
public sealed partial class ImagesBeingReplayedDialog : Window
{
    public ImagesBeingReplayedDialog() => InitializeComponent();

    private void Close_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void Turn_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ReplayImageTurnViewModel turn) Close(turn.Turn);
    }

    private async void Image_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ReplayImageViewModel image) return;
        try { await new ImagePreviewWindow(image.Preview).ShowDialog(this); }
        catch (Exception ex) { ((ImagesBeingReplayedDialogViewModel)DataContext!).Status = "Couldn't open the image: " + ex.Message; }
    }
}
