using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using SharpTurns.App.ViewModels;

namespace SharpTurns.App.Views;

/// <summary>
/// An image gallery. Given add and remove callbacks it edits the collection it shows, which is the composer's
/// live attachment list; show it modally so nothing else changes the list meanwhile.
/// </summary>
public sealed partial class ImagePreviewWindow : Window
{
    private readonly ObservableCollection<ImageAttachmentViewModel> _images = [];
    private readonly Func<Window, Task>? _addAsync;
    private readonly Action<ImageAttachmentViewModel>? _remove;
    private ImageAttachmentViewModel? _shown;
    private Bitmap? _bitmap;

    public ImagePreviewWindow() => InitializeComponent();

    /// <summary>Shows one image, read-only.</summary>
    public ImagePreviewWindow(ImageAttachmentViewModel image) : this([image], image, null, null)
    {
    }

    /// <param name="addAsync">Lets the user choose more images, with this window as the picker's owner.</param>
    public ImagePreviewWindow(ObservableCollection<ImageAttachmentViewModel> images, ImageAttachmentViewModel selected,
        Func<Window, Task>? addAsync, Action<ImageAttachmentViewModel>? remove) : this()
    {
        _images = images;
        _addAsync = addAsync;
        _remove = remove;
        AddButton.IsVisible = addAsync is not null;
        RemoveButton.IsVisible = remove is not null;
        Thumbnails.ItemsSource = images;
        Thumbnails.SelectedItem = selected;
        ShowSelected();
    }

    private void Thumbnails_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Removing the selected image clears the selection briefly; the remove handler picks the next one.
        if (Thumbnails.SelectedItem is null) return;
        ShowSelected();
        Thumbnails.ScrollIntoView(Thumbnails.SelectedItem);
    }

    private void ShowSelected()
    {
        if (Thumbnails.SelectedItem is not ImageAttachmentViewModel image) return;
        var position = _images.Count > 1 ? $" ({Thumbnails.SelectedIndex + 1} of {_images.Count})" : "";
        Title = image.FileName;
        HeaderText.Text = image.FileName + position;
        DetailsText.Text = $"{image.Attachment.MediaType} · {image.ToolTip}";
        Thumbnails.IsVisible = _images.Count > 1;
        AddButton.IsEnabled = _images.Count < ConversationViewModel.MaxAttachments;
        ToolTip.SetTip(AddButton, AddButton.IsEnabled
            ? "Choose more images to attach"
            : $"A turn can include up to {ConversationViewModel.MaxAttachments} images");
        if (ReferenceEquals(image, _shown)) return;

        _shown = image;
        var previous = _bitmap;
        try { _bitmap = image.OpenBitmap(); }
        catch (Exception) { _bitmap = null; } // Saved images that no longer decode still show their details.
        PreviewImage.Source = _bitmap;
        UnavailableText.IsVisible = _bitmap is null;
        previous?.Dispose();
    }

    // Arrow keys page through the gallery even when the thumbnail strip isn't focused.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var offset = e.Key switch { Key.Left => -1, Key.Right => 1, _ => 0 };
        if (e.Handled || offset == 0 || _images.Count < 2) return;
        Thumbnails.SelectedIndex = (Thumbnails.SelectedIndex + offset + _images.Count) % _images.Count;
        e.Handled = true;
    }

    private async void Add_Click(object? sender, RoutedEventArgs e)
    {
        if (_addAsync is null) return;
        var previousCount = _images.Count;
        AddButton.IsEnabled = RemoveButton.IsEnabled = false;
        try { await _addAsync(this); }
        finally
        {
            // Show the first new image; otherwise just refresh the position and the Add button.
            RemoveButton.IsEnabled = true;
            if (_images.Count > previousCount) Thumbnails.SelectedIndex = previousCount;
            ShowSelected();
        }
    }

    private void Remove_Click(object? sender, RoutedEventArgs e)
    {
        if (Thumbnails.SelectedItem is ImageAttachmentViewModel image) Remove(image);
    }

    private void RemoveThumbnail_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Control)?.DataContext is ImageAttachmentViewModel image) Remove(image);
    }

    private void Remove(ImageAttachmentViewModel image)
    {
        if (_remove is null) return;
        var selectedIndex = Math.Max(0, Thumbnails.SelectedIndex);
        var removedIndex = _images.IndexOf(image);
        _remove(image);
        if (_images.Count == 0)
        {
            Close();
            return;
        }

        // Keep the displayed image when another one is removed; otherwise show the one that took its place.
        Thumbnails.SelectedIndex = Math.Min(removedIndex < selectedIndex ? selectedIndex - 1 : selectedIndex, _images.Count - 1);
        ShowSelected();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _bitmap?.Dispose();
    }
}
