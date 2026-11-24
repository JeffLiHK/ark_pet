using System.Text.Json;

const int MaximumOutfitFrames = 4096;

try
{
    string packDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args.Single()));
    using var stream = File.OpenRead(Path.Combine(packDirectory, "character.json"));
    using JsonDocument document = JsonDocument.Parse(stream);
    JsonElement manifest = document.RootElement;
    if (manifest.GetProperty("id").GetString() != "hoolheyak" || manifest.GetProperty("schemaVersion").GetInt32() != 1 ||
        manifest.GetProperty("defaultOutfitId").GetString() != "default")
    {
        throw new InvalidDataException("The Hoolheyak manifest has an unexpected character, schema, or default outfit.");
    }

    foreach (string outfitId in new[] { "default", "winds-of-time" })
    {
        JsonElement outfit = manifest.GetProperty("outfits").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == outfitId);
        int remainingFrames = MaximumOutfitFrames;
        foreach (string clipName in new[] { "idle", "touch" })
        {
            JsonElement clip = outfit.GetProperty(clipName);
            string directory = clip.GetProperty("directory").GetString() ?? string.Empty;
            int frameCount = clip.GetProperty("frameCount").GetInt32();
            string clipPath = Path.GetFullPath(directory, packDirectory);
            if (Path.IsPathRooted(directory) || !clipPath.StartsWith(packDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                clip.GetProperty("fps").GetInt32() is < 1 or > 60 || frameCount <= 0 || frameCount > remainingFrames)
            {
                throw new InvalidDataException($"The {clipName} animation for {outfitId} is invalid.");
            }

            remainingFrames -= frameCount;

            for (int index = 0; index < frameCount; index++)
            {
                string frame = Path.Combine(clipPath, $"{index:D4}.png");
                if (!File.Exists(frame))
                {
                    throw new FileNotFoundException($"A required Hoolheyak frame is missing: {frame}");
                }
            }
        }
    }

    return 0;
}
catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or FormatException or
    JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
