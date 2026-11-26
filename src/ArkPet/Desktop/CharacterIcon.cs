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
            Int32Rect bounds = HeadBounds(frame);
            if (bounds.IsEmpty)
            {
                continue;
            }

            const int size = 64;
            var cropped = new CroppedBitmap(frame, bounds);
            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            using (DrawingContext drawing = visual.RenderOpen())
            {
                drawing.DrawImage(cropped, new Rect(0, 0, size, size));
            }

            var icon = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            icon.Render(visual);
            icon.Freeze();
            return icon;
        }

        return null;
    }

    private static Int32Rect HeadBounds(BitmapSource frame)
    {
        Int32Rect body = ArtworkBounds.VisiblePixels(frame);
        if (body.IsEmpty)
        {
            return Int32Rect.Empty;
        }

        // Include the chin below the hair and head accessories in standing chibi previews.
        int height = Math.Max(1, (int)Math.Round(body.Height * 0.44));
        var upper = new CroppedBitmap(frame, new Int32Rect(body.X, body.Y, body.Width, height));
        Int32Rect head = ArtworkBounds.VisiblePixels(upper);
        // Frame the face in a square and keep the chin in view when cropping tall accessories.
        int side = Math.Max(1, (int)Math.Round(Math.Min(head.Width, head.Height) * 0.8));
        int x = body.X + head.X + (head.Width - side) / 2;
        int y = body.Y + head.Y + head.Height - side;
        return new Int32Rect(x, y, side, side);
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
