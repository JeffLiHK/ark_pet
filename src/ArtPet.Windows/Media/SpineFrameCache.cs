using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace ArtPet.Windows.Media;

internal static class SpineFrameCache
{
    internal const int SchemaVersion = 2;
    internal const long MaximumSourceFileSize = 8L * 1024 * 1024;
    private const int CanvasSize = 320;
    private const int Fps = 60;
    private const int MaximumFramesPerModel = 4096;
    private static readonly IReadOnlyDictionary<string, string> Animations = new Dictionary<string, string>
    {
        ["idle"] = "Relax",
        ["move"] = "Move",
        ["sit"] = "Sit",
        ["sleep"] = "Sleep",
        ["touch"] = "Interact"
    };

    internal static async Task GenerateAsync(string source, string destination, CancellationToken token)
    {
        SourcePack pack;
        try
        {
            pack = ReadSourcePack(source);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("無法讀取 Spine 美術資源清單。", error);
        }

        // WebView2 needs an STA with a message pump. Keep rendering and disk I/O off the pet's dispatcher.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            _ = dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(TimeSpan.FromMinutes(10));
                    try
                    {
                        await RenderAsync(pack, source, destination, deadline.Token);
                    }
                    catch (OperationCanceledException error) when (!token.IsCancellationRequested)
                    {
                        throw new InvalidDataException("產生動畫影格逾時。", error);
                    }
                    completion.SetResult();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    completion.SetCanceled(token);
                }
                catch (Exception error)
                {
                    completion.SetException(error);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Spine frame generation" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task;
    }

    private static SourcePack ReadSourcePack(string source)
    {
        using FileStream manifest = File.OpenRead(Path.Combine(source, "character.json"));
        if (manifest.Length > 16 * 1024 ||
            JsonSerializer.Deserialize<SourcePack>(manifest, ManagedCharacterAssets.JsonOptions) is not
                { SchemaVersion: SchemaVersion, Id: ManagedCharacterAssets.PackId, Outfits: { Length: > 0 and <= 16 } outfits } pack ||
            string.IsNullOrWhiteSpace(pack.DisplayName) ||
            outfits.Any(outfit => outfit is null || !IsIdentifier(outfit.Id) || !IsIdentifier(outfit.ModelId) ||
                string.IsNullOrWhiteSpace(outfit.DisplayName)) ||
            outfits.Select(outfit => outfit.Id).Distinct(StringComparer.Ordinal).Count() != outfits.Length ||
            !outfits.Any(outfit => outfit.Id == pack.DefaultOutfitId))
        {
            throw new InvalidDataException("Spine 美術資源清單無效。");
        }

        return pack;
    }

    private static bool IsIdentifier(string? value) =>
        value is not null && Regex.IsMatch(value, @"\A[a-z0-9][a-z0-9-]{0,39}\z");

    private static async Task RenderAsync(SourcePack pack, string source, string destination, CancellationToken token)
    {
        try
        {
            var options = new CoreWebView2EnvironmentOptions(
                "--use-angle=swiftshader --enable-unsafe-swiftshader --disable-background-timer-throttling");
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null, Path.Combine(ManagedCharacterAssets.RootDirectory, "renderer"), options)
                .WaitAsync(token);
            using var window = new HwndSource(new HwndSourceParameters("ArtPet Spine renderer", 1, 1)
            {
                WindowStyle = 0
            });
            CoreWebView2Controller controller = await environment.CreateCoreWebView2ControllerAsync(window.Handle)
                .WaitAsync(token);

            try
            {
                controller.IsVisible = false;
                CoreWebView2 webView = controller.CoreWebView2;
                var navigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                webView.NavigationCompleted += (_, args) => navigation.TrySetResult(args.IsSuccess);
                webView.NavigateToString("<!doctype html><meta charset=utf-8>");
                if (!await navigation.Task.WaitAsync(token))
                {
                    throw new InvalidDataException("無法開啟本機 Spine 轉檔頁面。");
                }

                await EvaluateAsync(webView, ReadScript("ArtPet.SpineWebGL.js") + "\nvoid 0", token);
                await EvaluateAsync(webView, ReadScript("ArtPet.SpineFrameRenderer.js") + "\nvoid 0", token);
                await EvaluateAsync(webView, $"window.startRenderer({Json(new { size = CanvasSize, fps = Fps, Animations })})", token);
                await ExportPackAsync(webView, pack, source, destination, token);
            }
            finally
            {
                controller.Close();
            }
        }
        catch (WebView2RuntimeNotFoundException error)
        {
            throw new InvalidDataException("需要 Microsoft Edge WebView2 Runtime 才能產生動畫影格。", error);
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or JsonException or FormatException)
        {
            throw new InvalidDataException("無法產生 Spine 動畫影格。", error);
        }
    }

    private static async Task ExportPackAsync(CoreWebView2 webView, SourcePack pack, string source,
        string destination, CancellationToken token)
    {
        var models = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (string id in pack.Outfits.Select(outfit => outfit.ModelId).Distinct(StringComparer.Ordinal))
        {
            string directory = Path.Combine(source, "models", id);
            string atlas = await File.ReadAllTextAsync(Path.Combine(directory, "model.atlas"), token);
            string texture = atlas.TrimStart().Split('\n', 2)[0].TrimEnd('\r');
            if (texture != Path.GetFileName(texture) || texture.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                !texture.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Spine 圖集的貼圖路徑無效。");
            }
            string png = Convert.ToBase64String(await File.ReadAllBytesAsync(Path.Combine(directory, texture), token));
            string binary = Convert.ToBase64String(await File.ReadAllBytesAsync(Path.Combine(directory, "model.skel"), token));
            JsonElement result = await EvaluateAsync(webView, $"window.renderer.load({Json(new { id, atlas, texture, png, binary })})", token);
            var counts = result.Deserialize<Dictionary<string, int>>(ManagedCharacterAssets.JsonOptions);
            if (counts is null || counts.Count != Animations.Count ||
                Animations.Keys.Any(clip => !counts.TryGetValue(clip, out int count) || count is < 1 or > MaximumFramesPerModel) ||
                counts.Values.Sum() > MaximumFramesPerModel)
            {
                throw new InvalidDataException("Spine 模型的動畫影格數量無效。");
            }
            models.Add(id, counts);
        }

        JsonElement canvas = await EvaluateAsync(webView, "window.renderer.frame()", token);
        long remaining = 1024L * 1024 * 1024;
        var clipsByModel = new Dictionary<string, Dictionary<string, ClipDefinition>>(StringComparer.Ordinal);
        foreach ((string id, Dictionary<string, int> counts) in models)
        {
            var clips = new Dictionary<string, ClipDefinition>(StringComparer.Ordinal);
            foreach ((string clip, int frameCount) in counts)
            {
                string relative = $"frames/{id}/{clip}";
                string directory = Path.Combine(destination, relative);
                Directory.CreateDirectory(directory);
                clips.Add(clip, new(relative, Fps, frameCount));
                const int batchSize = 8;
                for (int start = 0; start < frameCount; start += batchSize)
                {
                    token.ThrowIfCancellationRequested();
                    int end = Math.Min(start + batchSize, frameCount);
                    JsonElement frames = await EvaluateAsync(webView,
                        $"window.renderer.render({Json(new { id, clip, start, end })})", token);
                    if (frames.ValueKind != JsonValueKind.Array || frames.GetArrayLength() != end - start)
                    {
                        throw new InvalidDataException("產生的動畫影格數量不符。");
                    }
                    int index = start;
                    foreach (JsonElement frame in frames.EnumerateArray())
                    {
                        byte[] png = Convert.FromBase64String(frame.GetString() ?? "");
                        remaining -= png.Length;
                        if (remaining < 0)
                        {
                            throw new InvalidDataException("產生的動畫影格超出儲存上限。");
                        }
                        await File.WriteAllBytesAsync(Path.Combine(directory, $"{index++:D4}.png"), png, token);
                    }
                }
            }
            clipsByModel.Add(id, clips);
        }

        Directory.CreateDirectory(Path.Combine(destination, "portraits"));
        var outfits = new List<OutfitDefinition>();
        foreach (SourceOutfit outfit in pack.Outfits)
        {
            token.ThrowIfCancellationRequested();
            string portrait = $"portraits/{outfit.Id}.png";
            File.Copy(Path.Combine(source, portrait), Path.Combine(destination, portrait));
            Dictionary<string, ClipDefinition> clips = clipsByModel[outfit.ModelId];
            outfits.Add(new(outfit.Id, outfit.DisplayName, $"{clips["idle"].Directory}/0000.png",
                clips["idle"], clips["touch"], clips["move"], clips["sit"], clips["sleep"], portrait));
        }

        await File.WriteAllTextAsync(Path.Combine(destination, "character.json"), Json(new
        {
            schemaVersion = CharacterPack.SupportedSchemaVersion, pack.Id, pack.DisplayName, pack.DefaultOutfitId, canvas, outfits
        }), token);
    }

    private static async Task<JsonElement> EvaluateAsync(CoreWebView2 webView, string expression, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string response = await webView.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
            Json(new { expression, returnByValue = true, awaitPromise = true })).WaitAsync(token);
        using JsonDocument document = JsonDocument.Parse(response);
        JsonElement root = document.RootElement;
        if (root.TryGetProperty("exceptionDetails", out JsonElement error))
        {
            string? message = error.TryGetProperty("exception", out JsonElement exception) &&
                exception.TryGetProperty("description", out JsonElement detail) ? detail.GetString()?.Split('\n', 2)[0] : null;
            throw new InvalidDataException($"Spine 轉檔失敗。{message}");
        }
        if (!root.TryGetProperty("result", out JsonElement result))
        {
            throw new InvalidDataException("Spine 轉檔未收到瀏覽器回應。");
        }
        return result.TryGetProperty("value", out JsonElement value) ? value.Clone() : default;
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, ManagedCharacterAssets.JsonOptions);

    private static string ReadScript(string name)
    {
        using var stream = typeof(SpineFrameCache).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing renderer resource {name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed record SourcePack(int SchemaVersion, string Id, string DisplayName, string DefaultOutfitId, SourceOutfit[] Outfits);
    private sealed record SourceOutfit(string Id, string DisplayName, string ModelId);
}
