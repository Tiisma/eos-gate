using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.Client;

namespace SombreterreGate.Client;

/// <summary>
/// The Exiles of Shadowland main menu: Play (back to the server), Settings and Quit, over the server's
/// own backgrounds. Single player, multiplayer, the server dashboard, mods and credits are not offered.
///
/// Nothing of the game's menu is rebuilt here. While the game composes its left menu, the buttons it
/// adds are filtered: the single player button becomes Play, the ones the server does not offer are
/// skipped, and the next button is moved up into the place a skipped one would have taken. After the
/// menu is composed, its background is swapped for one of this mod's, picked at random each time.
///
/// The patches are applied once per game process and deliberately never removed: the menu is shown
/// after the world (and this mod's systems) have been unloaded.
/// </summary>
internal static class MainMenuPatches
{
    private const string HarmonyId = "sombreterregate.mainmenu";

    private static readonly HashSet<string> Hidden = new() { "multiplayer", "gameserver", "mods", "credits" };
    private static readonly string[] Kept = { "singleplayer", "settings", "quit" };

    private static readonly FieldInfo ScreenManagerField = AccessTools.Field(typeof(GuiCompositeMainMenuLeft), "screenManager");
    private static readonly FieldInfo BackgroundField = AccessTools.Field(typeof(GuiCompositeMainMenuLeft), "bgtex");
    private static readonly FieldInfo ComposerField = AccessTools.Field(typeof(ScreenManager), "mainMenuComposer");

    private static readonly Random Random = new();
    private static bool _applied;
    private static ILogger? _logger;

    [ThreadStatic] private static bool _composing;
    [ThreadStatic] private static ElementBounds? _lastKept;
    [ThreadStatic] private static ScreenManager? _screenManager;

    public static void Apply(ILogger logger)
    {
        if (_applied) return;
        _logger = logger;
        if (ScreenManagerField == null || BackgroundField == null || ComposerField == null)
        {
            logger.Error("[SombreterreGate] This game version's main menu is not the one expected; keeping the game's own.");
            return;
        }
        try
        {
            var harmony = new Harmony(HarmonyId);
            var self = typeof(MainMenuPatches);
            harmony.Patch(AccessTools.Method(typeof(GuiCompositeMainMenuLeft), "Compose"),
                prefix: new HarmonyMethod(self, nameof(BeforeCompose)),
                postfix: new HarmonyMethod(self, nameof(AfterCompose)));
            harmony.Patch(AccessTools.Method(typeof(GuiCompositeMainMenuLeft), "updateButtonActiveFlag"),
                prefix: new HarmonyMethod(self, nameof(SetActiveButton)));
            harmony.Patch(AccessTools.Method(typeof(GuiComposerHelpers), nameof(GuiComposerHelpers.AddButton),
                    new[] { typeof(GuiComposer), typeof(string), typeof(ActionConsumable), typeof(ElementBounds), typeof(CairoFont), typeof(EnumButtonStyle), typeof(string) }),
                prefix: new HarmonyMethod(self, nameof(FilterButton)));
            _applied = true;
            logger.Notification("[SombreterreGate] Main menu trimmed to Play, Settings and Quit.");
        }
        catch (Exception e)
        {
            // A game update that renamed something leaves the vanilla menu rather than a broken one.
            logger.Error($"[SombreterreGate] Could not patch the main menu, keeping the game's own: {e.Message}");
        }
    }

    private static void BeforeCompose(GuiCompositeMainMenuLeft __instance)
    {
        _composing = true;
        _lastKept = null;
        _screenManager = ScreenManagerField.GetValue(__instance) as ScreenManager;
    }

    private static void AfterCompose(GuiCompositeMainMenuLeft __instance)
    {
        _composing = false;
        try
        {
            ReplaceBackground(__instance);
        }
        catch (Exception e)
        {
            _logger?.Warning($"[SombreterreGate] Could not set the main menu background: {e.GetType().Name}");
        }
    }

    /// <summary>Prefix of GuiComposerHelpers.AddButton, active only while the left main menu composes.</summary>
    private static bool FilterButton(GuiComposer composer, ref string text, ref ActionConsumable onClick, ElementBounds bounds, string key, ref GuiComposer __result)
    {
        if (!_composing) return true;

        if (key == "singleplayer" && GateSession.Server != null)
        {
            var server = GateSession.Server;
            var screenManager = _screenManager;
            text = Lang.CurrentLocale.StartsWith("fr", StringComparison.OrdinalIgnoreCase) ? "Jouer" : "Play";
            onClick = () =>
            {
                screenManager?.ConnectToMultiplayer(server, null);
                return true;
            };
            _lastKept = bounds;
            return true;
        }

        if (key == "singleplayer" || Hidden.Contains(key))
        {
            // Take the place of the last kept button, so the next one lands right below it.
            if (_lastKept != null) bounds.fixedY = _lastKept.fixedY;
            __result = composer;
            return false;
        }

        _lastKept = bounds;
        return true;
    }

    /// <summary>The game's version marks every menu button, some of which are no longer there.</summary>
    private static bool SetActiveButton(GuiCompositeMainMenuLeft __instance, string key)
    {
        if (ScreenManagerField.GetValue(__instance) is not ScreenManager screenManager) return false;
        if (ComposerField.GetValue(screenManager) is not GuiComposer composer) return false;
        foreach (var name in Kept) composer.GetButton(name)?.SetActive(key == name);
        return false;
    }

    private static void ReplaceBackground(GuiCompositeMainMenuLeft menu)
    {
        if (GateSession.Backgrounds.Count == 0) return;
        if (ScreenManagerField.GetValue(menu) is not ScreenManager screenManager) return;
        var api = screenManager.api;

        var png = GateSession.Backgrounds[Random.Next(GateSession.Backgrounds.Count)];
        var bitmap = api.Render.BitmapCreateFromPng(png);
        try
        {
            var texture = new LoadedTexture(api);
            api.Render.LoadTexture(bitmap, ref texture, linearMag: true);
            var previous = BackgroundField.GetValue(menu) as LoadedTexture;
            BackgroundField.SetValue(menu, texture);
            previous?.Dispose();
        }
        finally
        {
            bitmap.Dispose();
        }
    }
}
