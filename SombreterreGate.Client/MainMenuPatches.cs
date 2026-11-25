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
using Vintagestory.API.MathTools;
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
/// After the menu is composed, its background is replaced by the server's pictures, which then follow one
/// another (see <see cref="MenuBackgrounds"/>): the menu's own draw of its background is rerouted to
/// <see cref="MenuBackgrounds.Draw"/>, which fades from one picture to the next, and its draw of the
/// Vintage Story logo to <see cref="MenuLogo.Draw"/>, which puts the server's own logo there.
///
/// The game's left bar (wood plank, tree, account links, update notice) is then replaced by a column of
/// buttons in the middle of the screen, shown on the main screen only. On that screen the quote at the
/// bottom becomes the server's tips, larger, centred and in turn, and the welcome panel on the right the
/// server's own panel, or nothing (see <see cref="MenuTexts"/>).
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

    private static readonly MethodInfo? RenderMethod = AccessTools.Method(typeof(GuiCompositeMainMenuLeft), "Render", new[] { typeof(float), typeof(long), typeof(bool), typeof(bool) });
    private static readonly MethodInfo? Render2DTextureMethod = AccessTools.Method(typeof(IRenderAPI), nameof(IRenderAPI.Render2DTexture),
        new[] { typeof(int), typeof(float), typeof(float), typeof(float), typeof(float), typeof(float), typeof(Vec4f) });
    private static readonly MethodInfo? Render2DPremultipliedMethod = AccessTools.Method(typeof(IRenderAPI), nameof(IRenderAPI.Render2DTexturePremultipliedAlpha),
        new[] { typeof(int), typeof(double), typeof(double), typeof(double), typeof(double), typeof(float), typeof(Vec4f) });

    private static readonly FieldInfo? SidebarField = AccessTools.Field(typeof(GuiCompositeMainMenuLeft), "sidebarBounds");
    private static readonly MethodInfo? OnSettingsMethod = AccessTools.Method(typeof(GuiCompositeMainMenuLeft), "OnSettings");
    private static readonly FieldInfo? CurrentScreenField = AccessTools.Field(typeof(ScreenManager), "CurrentScreen");
    private static readonly FieldInfo? MainScreenField = AccessTools.Field(typeof(ScreenManager), "mainScreen");

    /// <summary>The one button shown on the other screens (settings...), which the game's bar used to lead back from.</summary>
    private static GuiComposer? _backComposer;
    private static readonly FieldInfo? QuoteTextureField = AccessTools.Field(typeof(GuiScreenMainRight), "quoteTexture");
    private static readonly FieldInfo? GrayBgField = AccessTools.Field(typeof(GuiScreenMainRight), "grayBg");

    /// <summary>Width of a centred button and the gap between two, at a GUI scale of 1.</summary>
    private const double ButtonWidth = 340;
    private const double ButtonGap = 14;

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
            if (SidebarField != null && OnSettingsMethod != null && CurrentScreenField != null)
            {
                harmony.Patch(AccessTools.Method(typeof(ScreenManager), "RenderMainMenuParts"), prefix: new HarmonyMethod(self, nameof(MainMenuOnMainScreenOnly)));
                foreach (var name in new[] { "OnMouseDown", "OnMouseUp", "OnMouseMove" })
                    harmony.Patch(AccessTools.Method(typeof(GuiCompositeMainMenuLeft), name), prefix: new HarmonyMethod(self, nameof(MenuInputOnMainScreenOnly)));
            }
            if (QuoteTextureField != null && GrayBgField != null && Render2DPremultipliedMethod != null)
            {
                harmony.Patch(AccessTools.Method(typeof(GuiScreenMainRight), "getQuote"), prefix: new HarmonyMethod(self, nameof(ServerTip)));
                harmony.Patch(AccessTools.Method(typeof(GuiScreenMainRight), nameof(GuiScreenMainRight.Compose)), postfix: new HarmonyMethod(self, nameof(AfterMainScreenCompose)));
                harmony.Patch(AccessTools.Method(typeof(GuiScreenMainRight), nameof(GuiScreenMainRight.Render)), prefix: new HarmonyMethod(self, nameof(BeforeMainScreenRender)), transpiler: new HarmonyMethod(self, nameof(RerouteQuoteDraw)));
            }
            if (RenderMethod != null && Render2DTextureMethod != null)
                harmony.Patch(RenderMethod, prefix: new HarmonyMethod(self, nameof(BeforeRender)), transpiler: new HarmonyMethod(self, nameof(RerouteBackgroundDraw)));
            else logger.Warning("[SombreterreGate] The main menu draws its background in a way this mod does not expect; the pictures will not follow one another.");
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

    private static void BeforeRender(GuiCompositeMainMenuLeft __instance)
    {
        try
        {
            if (ScreenManagerField.GetValue(__instance) is not ScreenManager screenManager) return;
            MenuLogo.SetWindowHeight(screenManager.GamePlatform.WindowSize.Height);
            MenuTexts.SetWindowWidth(screenManager.GamePlatform.WindowSize.Width);
            MenuLogo.SetWindowWidth(screenManager.GamePlatform.WindowSize.Width);
            MenuBackgrounds.Tick(screenManager.api, () => BackgroundField.GetValue(__instance) as LoadedTexture, texture => BackgroundField.SetValue(__instance, texture));
        }
        catch (Exception e)
        {
            _logger?.Warning($"[SombreterreGate] Could not change the main menu background: {e.GetType().Name}");
        }
    }

    /// <summary>The first Render2DTexture call in Render draws the background, the second the logo.</summary>
    private static IEnumerable<CodeInstruction> RerouteBackgroundDraw(IEnumerable<CodeInstruction> instructions)
    {
        var replacements = new[] { AccessTools.Method(typeof(MenuBackgrounds), nameof(MenuBackgrounds.Draw)), AccessTools.Method(typeof(MenuLogo), nameof(MenuLogo.Draw)) };
        var seen = 0;
        foreach (var instruction in instructions)
        {
            if (seen < replacements.Length && instruction.Calls(Render2DTextureMethod))
            {
                yield return new CodeInstruction(OpCodes.Call, replacements[seen++]).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                continue;
            }
            yield return instruction;
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
        if (ScreenManagerField.GetValue(menu) is not ScreenManager screenManager) return;
        MenuBackgrounds.ShowFirst(screenManager.api, () => BackgroundField.GetValue(menu) as LoadedTexture, texture => BackgroundField.SetValue(menu, texture), _logger);
        MenuLogo.Refresh(screenManager.api, _logger);
        CentreButtons(menu, screenManager);
    }

    private static bool OnMainScreen(ScreenManager screenManager) => CurrentScreenField?.GetValue(screenManager) is GuiScreenMainRight;

    /// <summary>
    /// Puts the game's composed menu aside for a column of buttons in the middle of the screen, under the
    /// logo, with the same keys so the rest of the game (and <see cref="SetActiveButton"/>) still finds them.
    /// The bar's width drops to nothing, so what the game places from it (logo, tips) recentres itself.
    /// </summary>
    private static void CentreButtons(GuiCompositeMainMenuLeft menu, ScreenManager screenManager)
    {
        if (SidebarField == null || OnSettingsMethod == null) return;
        try
        {
            var buttons = new List<(string Key, string Text, ActionConsumable OnClick)>();
            if (GateSession.Server != null)
            {
                var server = GateSession.Server;
                buttons.Add(("singleplayer", French ? "Jouer" : "Play", () =>
                {
                    screenManager.ConnectToMultiplayer(server, null);
                    return true;
                }));
            }
            if (VerifiedModsField != null) buttons.Add(("multiplayer", French ? "Monde créatif" : "Creative world", () => OpenCreativeWorld(screenManager)));
            buttons.Add(("settings", Lang.Get("mainmenu-settings"), () => (bool)OnSettingsMethod.Invoke(menu, null)!));
            buttons.Add(("quit", Lang.Get("mainmenu-quit"), menu.OnQuit));

            var scale = GuiElement.scaled(1);
            var top = screenManager.GamePlatform.WindowSize.Height * 0.42 / scale;
            var font = CairoFont.ButtonText().WithFontSize(24);
            const double height = 52;

            var composer = ScreenManager.GuiComposers.Create("compositemainmenu", ElementBounds.Fill);
            for (var i = 0; i < buttons.Count; i++)
            {
                var bounds = ElementBounds.Fixed(EnumDialogArea.CenterTop, 0, top + i * (height + ButtonGap), ButtonWidth, height);
                composer.AddButton(buttons[i].Text, buttons[i].OnClick, bounds, font, EnumButtonStyle.Normal, buttons[i].Key);
            }
            // Composed before the game's is let go, so a failure leaves the game's menu working.
            composer.Compose();

            _backComposer?.Dispose();
            _backComposer = MainScreenField == null ? null : ScreenManager.GuiComposers.Create("eosmenuback", ElementBounds.Fill)
                .AddButton(French ? "Retour" : "Back", () =>
                {
                    if (MainScreenField.GetValue(screenManager) is GuiScreen main) screenManager.LoadScreen(main);
                    return true;
                }, ElementBounds.Fixed(EnumDialogArea.LeftTop, 15, 15, 160, 44), CairoFont.ButtonText().WithFontSize(20), EnumButtonStyle.Normal, "eosback")
                .Compose();

            (ComposerField.GetValue(screenManager) as GuiComposer)?.Dispose();
            ComposerField.SetValue(screenManager, composer);
            SidebarField.SetValue(menu, ElementBounds.Fixed(0, 0, 0, 0));
        }
        catch (Exception e)
        {
            _logger?.Warning($"[SombreterreGate] Could not centre the main menu buttons, keeping the bar: {e}");
        }
    }

    /// <summary>
    /// The centred buttons belong to the main screen: on the others (settings...) they would cover the page,
    /// so those get a Back button in the top left corner instead.
    /// </summary>
    private static void MainMenuOnMainScreenOnly(ScreenManager __instance, float dt, ref bool withMainMenu)
    {
        if (!withMainMenu || OnMainScreen(__instance)) return;
        withMainMenu = false;
        _backComposer?.Render(dt);
        _backComposer?.PostRender(dt);
    }

    private static bool MenuInputOnMainScreenOnly(GuiCompositeMainMenuLeft __instance, MouseEvent e, MethodBase __originalMethod)
    {
        if (ScreenManagerField.GetValue(__instance) is not ScreenManager screenManager || OnMainScreen(screenManager)) return true;
        switch (__originalMethod.Name)
        {
            case "OnMouseDown": _backComposer?.OnMouseDown(e); break;
            case "OnMouseUp": _backComposer?.OnMouseUp(e); break;
            default: _backComposer?.OnMouseMove(e); break;
        }
        return false;
    }

    private static bool ServerTip(ref string __result)
    {
        if (!MenuTexts.LoadTips()) return true;
        __result = MenuTexts.NextTip()!;
        return false;
    }

    /// <summary>Makes the tip larger over a darker band, and puts the server's panel (or none) in place of the welcome one.</summary>
    private static void AfterMainScreenCompose(GuiScreenMainRight __instance)
    {
        try
        {
            var api = __instance.ScreenManager.api;
            var band = GrayBgField!.GetValue(__instance) as LoadedTexture ?? new LoadedTexture(api);
            MenuTexts.TipBand(api, ref band);
            GrayBgField.SetValue(__instance, band);

            var quote = AccessTools.Field(typeof(GuiScreenMainRight), "quote").GetValue(__instance) as string;
            if (!string.IsNullOrEmpty(quote)) SetTip(__instance, api, quote);

            ReplacePanel(__instance);
        }
        catch (Exception e)
        {
            _logger?.Warning($"[SombreterreGate] Could not set the main menu texts: {e}");
        }
    }

    private static void BeforeMainScreenRender(GuiScreenMainRight __instance)
    {
        if (!MenuTexts.TipDue) return;
        var tip = MenuTexts.NextTip();
        if (tip != null) SetTip(__instance, __instance.ScreenManager.api, tip);
    }

    private static void SetTip(GuiScreenMainRight screen, ICoreClientAPI api, string tip)
    {
        var previous = QuoteTextureField!.GetValue(screen) as LoadedTexture;
        QuoteTextureField.SetValue(screen, MenuTexts.TipTexture(api, tip));
        previous?.Dispose();
    }

    private static void ReplacePanel(GuiScreenMainRight screen)
    {
        var text = MenuTexts.Read("panel");
        GuiComposer composer;
        if (!string.IsNullOrWhiteSpace(text))
        {
            // Built the way the game builds its welcome panel, with the server's text in it.
            var bounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.RightBottom).WithFixedAlignmentOffset(-50, -50);
            var textBounds = ElementBounds.Fixed(0, 0, 450, 500);
            var font = CairoFont.WhiteDetailText().WithFontSize(16).WithLineHeightMultiplier(1.15);
            composer = ScreenManager.GuiComposers.Create("welcomedialog", bounds)
                .AddShadedDialogBG(ElementStdBounds.DialogBackground().WithFixedPadding(GuiStyle.ElementToDialogPadding, GuiStyle.ElementToDialogPadding), false, 5, 0.8f)
                .BeginChildElements()
                .AddRichtext(text, font, textBounds, MenuTexts.OpenLink, "panel")
                .EndChildElements()
                .Compose();
            var height = composer.GetRichtext("panel").TotalHeight / GuiElement.scaled(1);
            if (height > 0)
            {
                textBounds.fixedHeight = height + 5;
                composer.ReCompose();
            }
        }
        else
        {
            // No panel: an empty composer, as the screen expects one.
            composer = ScreenManager.GuiComposers.Create("welcomedialog", ElementBounds.Fixed(0, 0, 1, 1)).Compose();
        }

        screen.ElementComposer?.Dispose();
        screen.ElementComposer = composer;
    }

    /// <summary>The second premultiplied draw in the main screen's Render is the quote; the first, its band, stays.</summary>
    private static IEnumerable<CodeInstruction> RerouteQuoteDraw(IEnumerable<CodeInstruction> instructions)
    {
        var replacement = AccessTools.Method(typeof(MenuTexts), nameof(MenuTexts.DrawTip));
        var seen = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(Render2DPremultipliedMethod) && ++seen == 2)
            {
                yield return new CodeInstruction(OpCodes.Call, replacement).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                continue;
            }
            yield return instruction;
        }
    }
}
