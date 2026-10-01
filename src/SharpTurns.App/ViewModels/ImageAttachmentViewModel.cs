using System.Globalization;
using Avalonia.Media.Imaging;
using SharpTurns.Core;

namespace SharpTurns.App.ViewModels;

/// <summary>An attached image with a thumbnail decoded on first use.</summary>
public sealed class ImageAttachmentViewModel(ImageAttachment attachment)
{
    private const int ThumbnailWidth = 160;
    private Bitmap? _thumbnail;
    private bool _thumbnailFailed;

    public ImageAttachment Attachment { get; } = attachment;

    public string FileName => Attachment.FileName;

    public string ToolTip
    {
        get
        {
            var bytes = Attachment.Data.Length / 4L * 3;
            var size = bytes >= 1024 * 1024
                ? (bytes / (1024d * 1024d)).ToString("0.0", CultureInfo.CurrentCulture) + " MB"
                : Math.Max(1, bytes / 1024).ToString(CultureInfo.CurrentCulture) + " KB";
            return $"{FileName} ({size})";
        }
    }

    public Bitmap? Thumbnail
    {
        get
        {
            if (_thumbnail is not null || _thumbnailFailed) return _thumbnail;
            try { _thumbnail = Bitmap.DecodeToWidth(new MemoryStream(Convert.FromBase64String(Attachment.Data)), ThumbnailWidth); }
            catch (Exception) { _thumbnailFailed = true; } // Unreadable images still show their name.
            return _thumbnail;
        }
    }

    /// <summary>A full-size bitmap for the preview window; the caller disposes it.</summary>
    public Bitmap OpenBitmap() => new(new MemoryStream(Convert.FromBase64String(Attachment.Data)));
}
