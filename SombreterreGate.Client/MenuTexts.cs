using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace SombreterreGate.Client;

/// <summary>
/// The server's main menu texts, from the installation's EoSMenu folder, which the launcher keeps in line
/// with the server's SombreterreMenu folder: tips.txt (one tip per line, shown in turn at the bottom of the
/// menu) and panel.txt (the panel on the right, in the game's rich text). Each may come per language,
/// tips.fr.txt for instance, chosen by the game's language before the plain file.
///
/// Shared with SombreterreGate.Startup, like <see cref="MainMenuPatches"/>, which calls it.
/// </summary>
internal static class MenuTexts
{
    public const string FolderName = "EoSMenu";

    /// <summary>How long a tip stays before the next one.</summary>
    private const long TipMs = 15_000;

    /// <summary>Size of a tip at a GUI scale of 1, against the game's small italic.</summary>
    private const float TipFontSize = 22;

    private static readonly Random Random = new();
    private static List<string> _tips = new();
    private static int _nextTip;
    private static long _tipShownAt;
    private static float _windowWidth = 1920;

    /// <summary>The text of one menu file in the game's language, else the plain one; null when neither is there.</summary>
    public static string? Read(string name)
    {
        var folder = Path.Combine(GamePaths.DataPath, FolderName);
        var language = Lang.CurrentLocale.Length >= 2 ? Lang.CurrentLocale[..2].ToLowerInvariant() : "";
        foreach (var file in new[] { $"{name}.{language}.txt", $"{name}.txt" })
        {
            var path = Path.Combine(folder, file);
            try
            {
                if (File.Exists(path)) return File.ReadAllText(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>Reads the tips again, in a new order. True when the server has some.</summary>
    public static bool LoadTips()
    {
        _tips = (Read("tips") ?? "")
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .OrderBy(_ => Random.Next())
            .ToList();
        _nextTip = 0;
        _tipShownAt = Environment.TickCount64;
        return _tips.Count > 0;
    }

    /// <summary>The next tip, or null when the server has none.</summary>
    public static string? NextTip()
    {
        if (_tips.Count == 0) return null;
        var tip = _tips[_nextTip];
        _nextTip = (_nextTip + 1) % _tips.Count;
        _tipShownAt = Environment.TickCount64;
        return tip;
    }

    /// <summary>True when the tip on screen has been there long enough and there is another one.</summary>
    public static bool TipDue => _tips.Count > 1 && Environment.TickCount64 - _tipShownAt > TipMs;

    /// <summary>A tip as a texture, larger and plainer than the game's own quote.</summary>
    public static LoadedTexture TipTexture(ICoreClientAPI api, string tip)
    {
        var font = CairoFont.WhiteMediumText().WithFontSize((float)GuiElement.scaled(TipFontSize));
        return api.Gui.TextTexture.GenUnscaledTextTexture(tip, font);
    }

    /// <summary>A one-pixel dark band behind the tips, darker than the game's, so they read over any picture.</summary>
    public static void TipBand(ICoreClientAPI api, ref LoadedTexture band)
    {
        api.Render.LoadOrUpdateTextureFromRgba(new[] { ColorUtil.ToRgba(150, 0, 0, 0) }, false, 0, ref band);
    }

    public static void SetWindowWidth(float width)
    {
        if (width > 0) _windowWidth = width;
    }

    /// <summary>Stands in for the screen's draw of its quote: the same texture, centred across the window.</summary>
    public static void DrawTip(IRenderAPI render, int textureId, double posX, double posY, double width, double height, float z, Vec4f? color)
    {
        render.Render2DTexturePremultipliedAlpha(textureId, (_windowWidth - width) / 2, posY, width, height, z, color);
    }

    /// <summary>Opens a link of the panel. Only https addresses, as the game's own menu does.</summary>
    public static void OpenLink(LinkTextComponent link)
    {
        if (link.Href != null && link.Href.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) NetUtil.OpenUrlInBrowser(link.Href);
    }
}
