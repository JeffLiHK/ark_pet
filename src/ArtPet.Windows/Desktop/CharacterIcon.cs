using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ArtPet.Windows.Media;
using Icon = System.Drawing.Icon;

namespace ArtPet.Windows.Desktop;

internal static class CharacterIcon
{
    internal static BitmapSource? Create(LoadedOutfit outfit)
    {
        var frames = outfit.Idle.Frames.Take(8);
        if (outfit.Preview is { } preview)
        {
            frames = frames.Prepend(preview);
        }

        foreach (BitmapSource frame in frames)
        {
            Int32Rect bounds = ArtworkBounds.VisiblePixels(frame);
            if (bounds.IsEmpty)
            {
                continue;
            }

            const int size = 64;
            const int padding = 2;
            var cropped = new CroppedBitmap(frame, bounds);
            double scale = (double)(size - 2 * padding) / Math.Max(bounds.Width, bounds.Height);
            double width = bounds.Width * scale;
            double height = bounds.Height * scale;
            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            using (DrawingContext drawing = visual.RenderOpen())
            {
                drawing.DrawImage(cropped, new Rect((size - width) / 2, (size - height) / 2, width, height));
            }

            var icon = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            icon.Render(visual);
            icon.Freeze();
            return icon;
        }

        return null;
    }

    internal static Icon ToTrayIcon(BitmapSource artwork)
    {
        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(artwork));
        encoder.Save(stream);
        stream.Position = 0;
        using var image = new System.Drawing.Bitmap(stream);
        IntPtr handle = image.GetHicon();
        try
        {
            using Icon icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }
}
