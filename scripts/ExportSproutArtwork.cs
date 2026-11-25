#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property AssemblyName=ArtPet.ExportSproutArtwork
#:property PublishAot=false
#:property PublishTrimmed=false
#:project ../src/ArtPet.Windows/ArtPet.Windows.csproj

using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ArtPet.Windows.Media;

internal static class ExportSproutArtwork
{
    private const int Size = 160;
    private const int Fps = 60;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Pass the output folder for the Sprout character.");
            return 1;
        }

        string destination = args[0];
        var sprout = new SproutVisual();
        sprout.Measure(new Size(Size, Size));
        sprout.Arrange(new Rect(0, 0, Size, Size));
        var idle = ExportClip(sprout, destination, "idle", SproutVisual.IdleCycleMs, reacting: false);
        var touch = ExportClip(sprout, destination, "touch", SproutVisual.ReactionMs, reacting: true);
        var manifest = new
        {
            schemaVersion = 1,
            id = "sprout",
            displayName = "小芽",
            defaultOutfitId = "default",
            canvas = new { width = Size, height = Size, anchorX = 80, anchorY = 146 },
            outfits = new[]
            {
                new { id = "default", displayName = "預設造型", preview = "idle/0000.png", portrait = "idle/0000.png", idle, touch }
            }
        };
        File.WriteAllText(Path.Combine(destination, "character.json"), JsonSerializer.Serialize(manifest));
        return 0;
    }

    private static Clip ExportClip(SproutVisual sprout, string directory, string name, double duration, bool reacting)
    {
        string clipDirectory = Path.Combine(directory, name);
        Directory.CreateDirectory(clipDirectory);
        int count = (int)Math.Ceiling(duration * Fps / 1000);
        for (int index = 0; index < count; index++)
        {
            sprout.DrawPose(index * 1000.0 / Fps, reacting);
            sprout.UpdateLayout();
            var bitmap = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(sprout);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream file = File.Create(Path.Combine(clipDirectory, $"{index:D4}.png"));
            png.Save(file);
        }
        return new(name, Fps, count);
    }

    private sealed record Clip(string directory, int fps, int frameCount);
}
