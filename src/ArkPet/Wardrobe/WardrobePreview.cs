using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ArtPet.Windows.Wardrobe;

internal static class WardrobePreview
{
    internal static BitmapSource? CropToVisiblePixels(BitmapSource? preview)
    {
        if (preview is null)
        {
            return null;
        }

        BitmapSource alphaSource = preview.Format == PixelFormats.Bgra32 || preview.Format == PixelFormats.Pbgra32
            ? preview
            : new FormatConvertedBitmap(preview, PixelFormats.Bgra32, null, 0);
        int width = alphaSource.PixelWidth;
        int height = alphaSource.PixelHeight;
        int stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        alphaSource.CopyPixels(pixels, stride, 0);

        int left = width;
        int top = height;
        int right = -1;
        int bottom = -1;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (pixels[y * stride + x * 4 + 3] == 0)
                {
                    continue;
                }

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        if (right < 0)
        {
            return null;
        }

        var bounds = new Int32Rect(left, top, right - left + 1, bottom - top + 1);
        BitmapSource result = bounds.Width == width && bounds.Height == height
            ? preview
            : new CroppedBitmap(preview, bounds);
        result.Freeze();
        return result;
    }
}
