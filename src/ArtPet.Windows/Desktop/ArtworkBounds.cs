using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ArtPet.Windows.Desktop;

internal static class ArtworkBounds
{
    // Use the outfit's resting frame for a stable recovery check across animation poses.
    internal static Rect FromFrame(BitmapSource frame)
    {
        BitmapSource bitmap = frame.Format == PixelFormats.Bgra32 || frame.Format == PixelFormats.Pbgra32
            ? frame
            : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);

        int left = bitmap.PixelWidth;
        int top = bitmap.PixelHeight;
        int right = 0;
        int bottom = 0;
        for (int y = 0; y < bitmap.PixelHeight; y++)
        {
            for (int x = 0; x < bitmap.PixelWidth; x++)
            {
                if (pixels[y * stride + x * 4 + 3] != 0)
                {
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x + 1);
                    bottom = Math.Max(bottom, y + 1);
                }
            }
        }

        if (right == 0)
        {
            return Rect.Empty;
        }

        // The image is centered with Stretch="Uniform" inside the square pet window.
        double size = Math.Max(frame.Width, frame.Height);
        double width = frame.Width / size;
        double height = frame.Height / size;
        return new Rect((1 - width) / 2 + left * width / bitmap.PixelWidth,
            (1 - height) / 2 + top * height / bitmap.PixelHeight,
            (right - left) * width / bitmap.PixelWidth,
            (bottom - top) * height / bitmap.PixelHeight);
    }
}
