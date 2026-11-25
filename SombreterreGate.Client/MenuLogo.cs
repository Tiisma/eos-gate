using System;
using System.IO;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace SombreterreGate.Client;

/// <summary>
/// The server's title logo, drawn where the main menu draws the Vintage Story one.
///
/// It comes from the installation's EoSLogo.png, which the launcher keeps in line with the server's logo
/// file; without one, the game's own logo stays. The game sizes its logo to a wide strip, so this one is
/// fitted inside that strip and a share of the window's height, keeping its proportions, whatever its shape.
///
/// Shared with SombreterreGate.Startup, like <see cref="MainMenuPatches"/>, which calls it.
/// </summary>
internal static class MenuLogo
{
    public const string FileName = "EoSLogo.png";

    /// <summary>Tallest the logo may be, as a share of the window's height.</summary>
    private const float MaxHeightShare = 0.3f;

    private static LoadedTexture? _logo;
    private static float _windowHeight = 1080;
    private static float _windowWidth = 1920;

    /// <summary>Reads the logo file again. Called after each compose of the menu.</summary>
    public static void Refresh(ICoreClientAPI api, ILogger? logger)
    {
        _logo?.Dispose();
        _logo = null;
        var path = Path.Combine(GamePaths.DataPath, FileName);
        try
        {
            if (!File.Exists(path)) return;
            var bitmap = api.Render.BitmapCreateFromPng(File.ReadAllBytes(path));
            try
            {
                var texture = new LoadedTexture(api);
                api.Render.LoadTexture(bitmap, ref texture, linearMag: true);
                _logo = texture;
            }
            finally
            {
                bitmap.Dispose();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger?.Warning($"[SombreterreGate] Could not read the menu logo: {e.GetType().Name}");
        }
    }

    public static void SetWindowHeight(float height)
    {
        if (height > 0) _windowHeight = height;
    }

    public static void SetWindowWidth(float width)
    {
        if (width > 0) _windowWidth = width;
    }

    /// <summary>Stands in for the menu's draw of its logo, with the same signature as IRenderAPI.Render2DTexture.</summary>
    public static void Draw(IRenderAPI render, int textureId, float posX, float posY, float width, float height, float z, Vec4f? color)
    {
        if (_logo == null || _logo.Width <= 0 || _logo.Height <= 0)
        {
            render.Render2DTexture(textureId, posX, posY, width, height, z, color);
            return;
        }

        var aspect = _logo.Width / (float)_logo.Height;
        var drawWidth = Math.Min(width, _windowHeight * MaxHeightShare * aspect);
        var drawHeight = drawWidth / aspect;
        // Centred across the window: without the game's left bar, the strip the game gives is not.
        render.Render2DTexture(_logo.TextureId, (_windowWidth - drawWidth) / 2, posY, drawWidth, drawHeight, z, color);
    }
}
