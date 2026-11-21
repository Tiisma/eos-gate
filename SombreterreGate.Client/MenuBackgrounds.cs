using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace SombreterreGate.Client;

/// <summary>
/// The pictures behind the main menu, shown one after another with a fade.
///
/// They come from the installation's EoSBackgrounds folder, which the launcher keeps in line with the
/// server's SombreterreBackgrounds folder; without any there, from the pictures built into this mod
/// (<see cref="GateSession.Backgrounds"/>). Only the picture on screen and the next one are in memory, and
/// the next one is decoded off the main thread, so a large folder costs neither memory nor frames.
///
/// Shared with SombreterreGate.Startup, like <see cref="MainMenuPatches"/>, which calls it.
/// </summary>
internal static class MenuBackgrounds
{
    public const string FolderName = "EoSBackgrounds";

    /// <summary>How long a picture stays before the next one fades in, and how long the fade lasts.</summary>
    private const long ShowMs = 12_000;
    private const long FadeMs = 1_500;

    private static readonly string[] Extensions = { ".png", ".jpg", ".jpeg" };
    private static readonly Random Random = new();

    private static List<Func<byte[]>> _sources = new();
    private static int _next;
    private static long _shownAt;
    private static Task<BitmapExternal?>? _pending;
    private static LoadedTexture? _fading;
    private static long _fadeStart;

    private static long Now => Environment.TickCount64;

    /// <summary>Reads the list of pictures again and puts one on screen at once. Called after each compose of the menu.</summary>
    public static void ShowFirst(ICoreClientAPI api, Func<LoadedTexture?> current, Action<LoadedTexture> replace, ILogger? logger)
    {
        _sources = Sources(logger);
        _next = 0;
        _pending = null;
        if (_sources.Count == 0) return;

        var texture = Load(api, Decode(api, _sources[Advance()]));
        if (texture == null) return;
        var previous = current();
        replace(texture);
        previous?.Dispose();
        _fading?.Dispose();
        _fading = null;
        _shownAt = Now;
    }

    /// <summary>Moves the show on: starts decoding the next picture when it is due, and swaps it in once decoded. Called every frame.</summary>
    public static void Tick(ICoreClientAPI api, Func<LoadedTexture?> current, Action<LoadedTexture> replace)
    {
        if (_sources.Count < 2) return;

        if (_pending == null && Now - _shownAt > ShowMs)
        {
            var source = _sources[Advance()];
            _pending = Task.Run(() => Decode(api, source));
        }
        if (_pending is not { IsCompleted: true }) return;

        var bitmap = _pending.IsCompletedSuccessfully ? _pending.Result : null;
        _pending = null;
        _shownAt = Now;
        var texture = Load(api, bitmap);
        if (texture == null) return;

        _fading?.Dispose();
        _fading = current();
        _fadeStart = Now;
        replace(texture);
    }

    /// <summary>
    /// Stands in for the menu's own draw of its background: the picture fading out under the one fading in,
    /// then the new one alone. Same signature as IRenderAPI.Render2DTexture, which it replaces.
    /// </summary>
    public static void Draw(IRenderAPI render, int textureId, float posX, float posY, float width, float height, float z, Vec4f? color)
    {
        if (_fading != null)
        {
            var progress = (Now - _fadeStart) / (float)FadeMs;
            if (progress < 1)
            {
                render.Render2DTexture(_fading.TextureId, posX, posY, width, height, z, color);
                render.Render2DTexture(textureId, posX, posY, width, height, z, new Vec4f(1, 1, 1, GameMath.Clamp(progress, 0, 1)));
                return;
            }
            _fading.Dispose();
            _fading = null;
        }
        render.Render2DTexture(textureId, posX, posY, width, height, z, color);
    }

    private static int Advance()
    {
        var index = _next;
        _next = (_next + 1) % _sources.Count;
        return index;
    }

    private static List<Func<byte[]>> Sources(ILogger? logger)
    {
        var folder = Path.Combine(GamePaths.DataPath, FolderName);
        try
        {
            if (Directory.Exists(folder))
            {
                var files = Directory.EnumerateFiles(folder)
                    .Where(file => Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    .OrderBy(_ => Random.Next())
                    .Select(file => (Func<byte[]>)(() => File.ReadAllBytes(file)))
                    .ToList();
                if (files.Count > 0) return files;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger?.Warning($"[SombreterreGate] Could not read the backgrounds folder: {e.GetType().Name}");
        }
        return GateSession.Backgrounds.OrderBy(_ => Random.Next()).Select(bytes => (Func<byte[]>)(() => bytes)).ToList();
    }

    private static BitmapExternal? Decode(ICoreClientAPI api, Func<byte[]> source)
    {
        try
        {
            // Despite its name, this decodes whatever the game's image library reads: PNG and JPEG alike.
            return api.Render.BitmapCreateFromPng(source());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static LoadedTexture? Load(ICoreClientAPI api, BitmapExternal? bitmap)
    {
        if (bitmap == null) return null;
        try
        {
            var texture = new LoadedTexture(api);
            api.Render.LoadTexture(bitmap, ref texture, linearMag: true);
            return texture;
        }
        finally
        {
            bitmap.Dispose();
        }
    }
}
