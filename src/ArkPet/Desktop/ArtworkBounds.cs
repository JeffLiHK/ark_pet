using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ArtPet.Windows.Desktop;

internal static class ArtworkBounds
{
    internal static Rect FromFrame(BitmapSource frame)
    {
        Int32Rect pixels = VisiblePixels(frame);
        if (pixels.IsEmpty)
        {
            return Rect.Empty;
        }

        Rect area = FrameArea(frame);
        return new Rect(area.X + pixels.X * area.Width / frame.PixelWidth,
            area.Y + pixels.Y * area.Height / frame.PixelHeight,
            pixels.Width * area.Width / frame.PixelWidth, pixels.Height * area.Height / frame.PixelHeight);
    }

    internal static Rect FrameArea(BitmapSource frame)
    {
        // Stretch="Uniform" centers the bitmap in the square artwork canvas.
        double size = Math.Max(frame.Width, frame.Height);
        double width = frame.Width / size;
        double height = frame.Height / size;
        return new Rect((1 - width) / 2, (1 - height) / 2, width, height);
    }

    internal static Int32Rect VisiblePixels(BitmapSource frame)
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

        return right == 0 ? Int32Rect.Empty : new Int32Rect(left, top, right - left, bottom - top);
    }
}
