using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using Vintagestory.Server;

namespace SombreterreGate.Client;

/// <summary>
/// The Exiles of Shadowland main menu: Play (back to the server), one creative world to build in alone,
/// Settings and Quit, over the server's own backgrounds. The game's single player screens, multiplayer,
/// the server dashboard, mods and credits are not offered.
///
/// Nothing of the game's menu is rebuilt here. The game's left menu adds its buttons through
/// GuiComposerHelpers.AddButton; in its Compose those calls are rerouted to <see cref="AddMenuButton"/>,
/// which filters them: the single player button becomes Play, the multiplayer one opens the creative
/// world, the ones the server does not offer are
/// skipped, and the next button is moved up into the place a skipped one would have taken. (Patching
/// AddButton itself does not work: the JIT inlines that small method into the recompiled Compose.)
/// After the menu is composed, its background is swapped for one of this mod's, picked at random each time.
///
/// The patches are applied once per game process and deliberately never removed: the menu is shown
/// after the world (and this mod's systems) have been unloaded. The launcher applies them before the
/// game's first menu, through SombreterreGate.Startup; the mod applies them on joining otherwise.
/// </summary>
internal static class MainMenuPatches
{
    private const string HarmonyId = "sombreterregate.mainmenu";

    private const string CreativePlayStyle = "creativebuilding";
    private const string CreativeWorldFile = "eos-creative.vcdbs";
    private const string CreativeWorldName = "The Exiles of Shadowland - Creative";

    private static readonly HashSet<string> Hidden = new() { "gameserver", "mods", "credits" };
    private static readonly string[] Kept = { "singleplayer", "multiplayer", "settings", "quit" };

    private static readonly FieldInfo ScreenManagerField = AccessTools.Field(typeof(GuiCompositeMainMenuLeft), "screenManager");
    private static readonly FieldInfo BackgroundField = AccessTools.Field(typeof(GuiCompositeMainMenuLeft), "bgtex");
    private static readonly FieldInfo ComposerField = AccessTools.Field(typeof(ScreenManager), "mainMenuComposer");
    private static readonly FieldInfo? VerifiedModsField = AccessTools.Field(typeof(ScreenManager), "verifiedMods");
    private static readonly MethodInfo? AddButtonMethod = AccessTools.Method(typeof(GuiComposerHelpers), nameof(GuiComposerHelpers.AddButton),
        new[] { typeof(GuiComposer), typeof(string), typeof(ActionConsumable), typeof(ElementBounds), typeof(CairoFont), typeof(EnumButtonStyle), typeof(string) });

    private static readonly Random Random = new();
    private static bool _applied;
    private static ILogger? _logger;

    [ThreadStatic] private static ElementBounds? _lastKept;
    [ThreadStatic] private static ScreenManager? _screenManager;

    public static void Apply(ILogger logger)
    {
        if (_applied) return;
        _logger = logger;
        if (ScreenManagerField == null || BackgroundField == null || ComposerField == null || AddButtonMethod == null)
        {
            logger.Error("[SombreterreGate] This game version's main menu is not the one expected; keeping the game's own.");
            return;
        }
        if (Harmony.HasAnyPatches(HarmonyId))
        {
            // The startup hook got here first, with its own copy of this class.
            _applied = true;
            return;
        }
        try
        {
            var harmony = new Harmony(HarmonyId);
            var self = typeof(MainMenuPatches);
            harmony.Patch(AccessTools.Method(typeof(GuiCompositeMainMenuLeft), "Compose"),
                prefix: new HarmonyMethod(self, nameof(BeforeCompose)),
                postfix: new HarmonyMethod(self, nameof(AfterCompose)),
                transpiler: new HarmonyMethod(self, nameof(RerouteButtons)));
            harmony.Patch(AccessTools.Method(typeof(GuiCompositeMainMenuLeft), "updateButtonActiveFlag"),
                prefix: new HarmonyMethod(self, nameof(SetActiveButton)));
            _applied = true;
            logger.Notification("[SombreterreGate] Main menu trimmed to Play, the creative world, Settings and Quit.");
        }
        catch (Exception e)
        {
            // A game update that renamed something leaves the vanilla menu rather than a broken one.
            logger.Error($"[SombreterreGate] Could not patch the main menu, keeping the game's own: {e.Message}");
        }
    }

    private static void BeforeCompose(GuiCompositeMainMenuLeft __instance)
    {
        _lastKept = null;
        _screenManager = ScreenManagerField.GetValue(__instance) as ScreenManager;
        try
        {
            GateSession.BeforeMenu?.Invoke();
        }
        catch (Exception e)
        {
            _logger?.Warning($"[SombreterreGate] Could not read the launch details for the main menu: {e.GetType().Name}");
        }
    }

    private static void AfterCompose(GuiCompositeMainMenuLeft __instance)
    {
        try
        {
            ReplaceBackground(__instance);
        }
        catch (Exception e)
        {
            _logger?.Warning($"[SombreterreGate] Could not set the main menu background: {e.GetType().Name}");
        }
    }

    private static IEnumerable<CodeInstruction> RerouteButtons(IEnumerable<CodeInstruction> instructions)
    {
        var replacement = AccessTools.Method(typeof(MainMenuPatches), nameof(AddMenuButton));
        var rerouted = 0;
        var result = instructions.Select(instruction =>
        {
            if (!instruction.Calls(AddButtonMethod)) return instruction;
            rerouted++;
            return new CodeInstruction(OpCodes.Call, replacement).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
        }).ToList();
        if (rerouted == 0) _logger?.Warning("[SombreterreGate] The main menu adds no buttons the way this mod expects; its buttons are left as they are.");
        return result;
    }

    /// <summary>Stands in for GuiComposerHelpers.AddButton in the left main menu's Compose, with the same signature.</summary>
    private static GuiComposer AddMenuButton(GuiComposer composer, string text, ActionConsumable onClick, ElementBounds bounds, CairoFont font, EnumButtonStyle style, string key)
    {
        if (key == "singleplayer" && GateSession.Server != null)
        {
            var server = GateSession.Server;
            var screenManager = _screenManager;
            text = French ? "Jouer" : "Play";
            onClick = () =>
            {
                screenManager?.ConnectToMultiplayer(server, null);
                return true;
            };
        }
        else if (key == "multiplayer" && VerifiedModsField != null)
        {
            var screenManager = _screenManager;
            text = French ? "Monde créatif" : "Creative world";
            onClick = () => OpenCreativeWorld(screenManager);
        }
        else if (key == "singleplayer" || key == "multiplayer" || Hidden.Contains(key))
        {
            // Take the place of the last kept button, so the next one lands right below it.
            if (_lastKept != null) bounds.fixedY = _lastKept.fixedY;
            return composer;
        }

        _lastKept = bounds;
        return composer.AddButton(text, onClick, bounds, font, style, key);
    }

    private static bool French => Lang.CurrentLocale.StartsWith("fr", StringComparison.OrdinalIgnoreCase);

    /// <summary>Opens the player's one creative world, creating it the first time, the way the game's own --openWorld does.</summary>
    private static bool OpenCreativeWorld(ScreenManager? screenManager)
    {
        if (screenManager == null) return false;
        var style = (VerifiedModsField?.GetValue(screenManager) as IEnumerable<ModContainer>)?
            .SelectMany(mod => mod.WorldConfig?.PlayStyles ?? Array.Empty<PlayStyle>())
            .FirstOrDefault(candidate => candidate.Code == CreativePlayStyle);
        if (style == null)
        {
            _logger?.Error($"[SombreterreGate] The game has no {CreativePlayStyle} play style; cannot open the creative world.");
            return false;
        }

        var args = new StartServerArgs
        {
            SaveFileLocation = Path.Combine(GamePaths.Saves, CreativeWorldFile),
            WorldName = CreativeWorldName,
            PlayStyle = style.Code,
            PlayStyleLangCode = style.LangCode,
            WorldType = style.WorldType,
            WorldConfiguration = style.WorldConfig,
            AllowCreativeMode = true,
            Language = ClientSettings.Language
        };
        // Only the client's copy of this class has these, and the mod is built against the server's.
        SetClientField(args, "CreatedByPlayerName", ClientSettings.PlayerName);
        SetClientField(args, "DisabledMods", ClientSettings.DisabledMods);
        SetClientField(args, "ClientModPaths", new List<string>(ClientSettings.ModPaths));
        screenManager.ConnectToSingleplayer(args);
        return true;
    }

    private static void SetClientField(StartServerArgs args, string name, object? value)
    {
        var field = AccessTools.Field(typeof(StartServerArgs), name);
        if (field != null) field.SetValue(args, value);
        else AccessTools.Property(typeof(StartServerArgs), name)?.SetValue(args, value);
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
