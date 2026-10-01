using SharpTurns.Core;
using SkiaSharp;

namespace SharpTurns.App.Services;

/// <summary>
/// Prepares an image for a turn: validates it, applies EXIF orientation, and re-encodes large images as WebP.
/// Earlier turns' images are resent when a session is reseeded, so every image stays within the API's
/// many-image limit of 2000 px per side and well under its 10 MB (base64) per-image limit.
/// </summary>
internal static class ImageAttachmentProcessor
{
    private const int MaxLongEdge = 2000;
    private const long MaxSourceBytes = 50 * 1024 * 1024;
    private const long MaxDecodedPixels = 100_000_000;
    private const long MaxAttachmentBytes = 5 * 1024 * 1024;
    // Small images that need no resize are kept as they are.
    private const long OptimizeAboveBytes = 1024 * 1024;

    public static async Task<ImageAttachment> ProcessAsync(string fileName, Stream source, CancellationToken cancellationToken = default)
    {
        await using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (memory.Length + read > MaxSourceBytes) throw new InvalidOperationException("The image is larger than 50 MB.");
            memory.Write(buffer, 0, read);
        }
        var bytes = memory.ToArray();
        return await Task.Run(() => Process(fileName, bytes), cancellationToken);
    }

    internal static ImageAttachment Process(string fileName, byte[] bytes)
    {
        if (bytes.Length == 0) throw new InvalidOperationException("The image is empty.");
        using var codec = SKCodec.Create(new MemoryStream(bytes, writable: false))
            ?? throw new InvalidOperationException("The file is not a supported image.");
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0) throw new InvalidOperationException("The image has invalid dimensions.");
        if ((long)info.Width * info.Height > MaxDecodedPixels)
            throw new InvalidOperationException($"The image is too large to process ({info.Width}×{info.Height}).");
        var mediaType = codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Png => "image/png",
            SKEncodedImageFormat.Jpeg => "image/jpeg",
            SKEncodedImageFormat.Webp => "image/webp",
            SKEncodedImageFormat.Gif => "image/gif",
            SKEncodedImageFormat.Bmp => null, // Supported as input; always re-encoded.
            _ => throw new InvalidOperationException($"{codec.EncodedFormat} images are not supported."),
        };

        var swapsAxes = codec.EncodedOrigin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var scale = Math.Min(1d, (double)MaxLongEdge / Math.Max(info.Width, info.Height));
        var width = Math.Max(1, (int)Math.Floor(info.Width * scale));
        var height = Math.Max(1, (int)Math.Floor(info.Height * scale));
        // Animated GIFs are kept as they are; the model sees their first frame.
        if (codec.FrameCount > 1 && scale == 1 && mediaType is not null)
            return Original(fileName, mediaType, bytes);
        if (mediaType is not null && scale == 1 && codec.EncodedOrigin == SKEncodedOrigin.TopLeft && bytes.Length <= OptimizeAboveBytes)
            return Original(fileName, mediaType, bytes);

        using var decoded = SKBitmap.Decode(codec, new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("The image could not be decoded.");
        using var resized = scale == 1 ? null
            : decoded.Resize(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul),
                new SKSamplingOptions(SKCubicResampler.Mitchell))
              ?? throw new InvalidOperationException("The image could not be resized.");
        using var oriented = Orient(resized ?? decoded, codec.EncodedOrigin, swapsAxes);
        using var pixmap = oriented.PeekPixels();
        // Lossless keeps screenshots and transparency sharp; photos use lossy WebP when it is smaller.
        var lossless = Encode(pixmap, SKWebpEncoderCompression.Lossless, 90);
        var lossy = pixmap.ComputeIsOpaque() && codec.EncodedFormat == SKEncodedImageFormat.Jpeg
            ? Encode(pixmap, SKWebpEncoderCompression.Lossy, 88) : null;
        var encoded = lossy is not null && lossy.Length < lossless.Length ? lossy : lossless;
        if (encoded.Length > MaxAttachmentBytes)
            throw new InvalidOperationException("The image is still larger than 5 MB after resizing.");
        return new(Path.ChangeExtension(fileName, ".webp"), "image/webp", Convert.ToBase64String(encoded));
    }

    private static ImageAttachment Original(string fileName, string mediaType, byte[] bytes) =>
        bytes.Length <= MaxAttachmentBytes
            ? new(fileName, mediaType, Convert.ToBase64String(bytes))
            : throw new InvalidOperationException("The image is larger than 5 MB.");

    private static byte[] Encode(SKPixmap pixmap, SKWebpEncoderCompression compression, float quality)
    {
        using var data = pixmap.Encode(new SKWebpEncoderOptions(compression, quality))
            ?? throw new InvalidOperationException("The image could not be encoded.");
        return data.ToArray();
    }

    private static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin, bool swapsAxes)
    {
        var output = new SKBitmap(new SKImageInfo(swapsAxes ? source.Height : source.Width,
            swapsAxes ? source.Width : source.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(output);
        canvas.Clear(SKColors.Transparent);
        float w = source.Width, h = source.Height;
        canvas.SetMatrix(origin switch
        {
            SKEncodedOrigin.TopRight => Matrix(-1, 0, w, 0, 1, 0),
            SKEncodedOrigin.BottomRight => Matrix(-1, 0, w, 0, -1, h),
            SKEncodedOrigin.BottomLeft => Matrix(1, 0, 0, 0, -1, h),
            SKEncodedOrigin.LeftTop => Matrix(0, 1, 0, 1, 0, 0),
            SKEncodedOrigin.RightTop => Matrix(0, -1, h, 1, 0, 0),
            SKEncodedOrigin.RightBottom => Matrix(0, -1, h, -1, 0, w),
            SKEncodedOrigin.LeftBottom => Matrix(0, 1, 0, -1, 0, w),
            _ => SKMatrix.Identity,
        });
        canvas.DrawBitmap(source, 0, 0);
        return output;
    }

    private static SKMatrix Matrix(float scaleX, float skewX, float transX, float skewY, float scaleY, float transY) =>
        new() { ScaleX = scaleX, SkewX = skewX, TransX = transX, SkewY = skewY, ScaleY = scaleY, TransY = transY, Persp2 = 1 };
}
