using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SharpTurns.App.ViewModels;

namespace SharpTurns.App.Views;

public sealed partial class ConversationView : UserControl
{
    private static readonly FilePickerFileType ImageFiles = new("Images")
    {
        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.bmp"],
        MimeTypes = ["image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp"],
        AppleUniformTypeIdentifiers = ["public.image"],
    };

    private ConversationViewModel? _viewModel;

    public ConversationView()
    {
        InitializeComponent();
        // Tunnel so the shortcuts run before the TextBox inserts a newline or pastes text.
        Composer.AddHandler(KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null) _viewModel.TurnContentChanged -= OnTurnContentChanged;
        _viewModel = DataContext as ConversationViewModel;
        if (_viewModel is null) return;
        _viewModel.TurnContentChanged += OnTurnContentChanged;
        // The view is reused across conversations, so a turn running in the background keeps these hooks.
        _viewModel.ShowQuestionAsync = ShowQuestionAsync;
        _viewModel.ShowPermissionAsync = ShowPermissionAsync;
        _viewModel.ConfirmAsync = ConfirmAsync;
        _viewModel.CopyTextAsync = CopyTextAsync;
        _viewModel.ShowReplayImagesAsync = ShowReplayImagesAsync;
        // Show the newest turn.
        Dispatcher.UIThread.Post(TurnsScroller.ScrollToEnd, DispatcherPriority.Background);
    }

    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        var command = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        if (command && e.Key == Key.Enter)
        {
            e.Handled = true;
            if (_viewModel?.SendCommand.CanExecute(null) == true) _viewModel.SendCommand.Execute(null);
        }
        else if (command && e.Key == Key.V)
        {
            // Clipboard formats can only be read asynchronously, so take over the paste and fall back to text.
            e.Handled = true;
            _ = PasteAsync();
        }
    }

    private async Task PasteAsync()
    {
        if (_viewModel is not { } viewModel || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try
        {
            if (await clipboard.TryGetBitmapAsync() is { } bitmap)
            {
                using (bitmap)
                {
                    using var png = new MemoryStream();
                    bitmap.Save(png);
                    png.Position = 0;
                    await viewModel.AddImageAsync("pasted-image.png", png);
                }
                return;
            }
            var images = (await clipboard.TryGetFilesAsync() ?? []).OfType<IStorageFile>().Where(IsImageFile).ToArray();
            if (images.Length > 0)
            {
                await AddFilesAsync(viewModel, images);
                return;
            }
        }
        catch (Exception e) { viewModel.Status = "Couldn't read the clipboard: " + e.Message; }
        Composer.Paste();
    }

    private async void AttachImage_Click(object? sender, RoutedEventArgs e) => await AttachImagesAsync(TopLevel.GetTopLevel(this));

    /// <summary>Owns its errors. The gallery passes itself as the owner so the picker opens over it.</summary>
    private async Task AttachImagesAsync(TopLevel? owner)
    {
        if (_viewModel is not { } viewModel || owner is null) return;
        try
        {
            var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Attach Images",
                AllowMultiple = true,
                FileTypeFilter = [ImageFiles],
            });
            await AddFilesAsync(viewModel, files);
        }
        catch (Exception ex) { viewModel.Status = "Couldn't open the images: " + ex.Message; }
    }

    private static async Task AddFilesAsync(ConversationViewModel viewModel, IReadOnlyList<IStorageFile> files)
    {
        foreach (var file in files)
        {
            await using var stream = await file.OpenReadAsync();
            await viewModel.AddImageAsync(file.Name, stream);
        }
    }

    private static bool IsImageFile(IStorageFile file) =>
        Path.GetExtension(file.Name).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";

    private async void Image_Click(object? sender, RoutedEventArgs e)
    {
        var image = (sender as Control)?.DataContext switch
        {
            ImageAttachmentViewModel attachment => attachment,
            TurnImageViewModel sent => sent.Preview,
            _ => null,
        };
        if (image is null) return;
        try
        {
            var owner = TopLevel.GetTopLevel(this) as Window;
            // The composer's images open as an editable gallery of all of them; images in sent turns open alone.
            if (_viewModel is { } viewModel && viewModel.Attachments.Contains(image) && owner is not null)
            {
                await new ImagePreviewWindow(viewModel.Attachments, image, AttachImagesAsync, viewModel.RemoveAttachmentCommand.Execute)
                    .ShowDialog(owner);
                return;
            }
            var window = new ImagePreviewWindow(image);
            if (owner is not null) window.Show(owner);
            else window.Show();
        }
        catch (Exception ex)
        {
            if (_viewModel is not null) _viewModel.Status = "Couldn't open the image: " + ex.Message;
        }
    }

    private Task<string?> ShowQuestionAsync(QuestionDialogViewModel question, CancellationToken token) =>
        ShowAsync(new QuestionDialog { DataContext = question }, dialog => dialog.Answer, token);

    private Task<bool> ShowPermissionAsync(string toolName, string input, CancellationToken token) =>
        ShowAsync(new PermissionDialog(toolName, input), dialog => dialog.Allowed, token);

    private async Task<bool> ConfirmAsync(string title, string message) =>
        TopLevel.GetTopLevel(this) is Window owner
        && await new PromptDialog(title, message, null, "Delete", destructive: true).ShowDialog<string?>(owner) is not null;

    private async Task ShowReplayImagesAsync(ImagesBeingReplayedDialogViewModel images)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var turn = await new ImagesBeingReplayedDialog { DataContext = images }.ShowDialog<TurnViewModel?>(owner);
        // Scroll the chosen turn's card to the top.
        if (turn is not null && TurnsList.ContainerFromItem(turn)?.TranslatePoint(default, TurnsList) is { } top)
            TurnsScroller.Offset = TurnsScroller.Offset.WithY(top.Y + TurnsList.Margin.Top);
    }

    private async Task CopyTextAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    // Non-modal, so the conversation stays readable while the user decides. Canceling the token closes the dialog.
    private async Task<T> ShowAsync<TDialog, T>(TDialog dialog, Func<TDialog, T> result, CancellationToken token)
        where TDialog : Window
    {
        var closed = new TaskCompletionSource();
        dialog.Closed += (_, _) => closed.TrySetResult();
        await using var registration = token.Register(() => Dispatcher.UIThread.Post(dialog.Close));
        if (TopLevel.GetTopLevel(this) is Window owner) dialog.Show(owner);
        else dialog.Show();
        await closed.Task;
        token.ThrowIfCancellationRequested();
        return result(dialog);
    }

    private void OnTurnContentChanged(object? sender, EventArgs e)
    {
        // Follow new output only when the reader is already at the bottom; this runs before layout grows the extent.
        var atBottom = TurnsScroller.Offset.Y >= TurnsScroller.Extent.Height - TurnsScroller.Viewport.Height - 48;
        if (atBottom) Dispatcher.UIThread.Post(TurnsScroller.ScrollToEnd, DispatcherPriority.Background);
    }
}
