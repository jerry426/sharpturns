using Avalonia.Controls;
using Avalonia.Media.Imaging;
using SharpTurns.App.ViewModels;

namespace SharpTurns.App.Views;

public sealed partial class ImagePreviewWindow : Window
{
    private readonly Bitmap? _bitmap;

    public ImagePreviewWindow() => InitializeComponent();

    public ImagePreviewWindow(ImageAttachmentViewModel image) : this()
    {
        _bitmap = image.OpenBitmap();
        Title = image.ToolTip;
        PreviewImage.Source = _bitmap;
        // Open no larger than the image itself, plus the margin.
        Width = Math.Clamp(_bitmap.Size.Width + 24, 240, 1400);
        Height = Math.Clamp(_bitmap.Size.Height + 24, 180, 1000);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _bitmap?.Dispose();
    }
}
