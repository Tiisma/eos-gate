using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SombreterreGate.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

/// <summary>
/// A .NET startup hook: the launcher starts the game with DOTNET_STARTUP_HOOKS pointing at this
/// assembly, and the runtime calls <see cref="Initialize"/> before the game's own Main. That is the
/// only way to have the server's main menu on the game's first screen, since the game runs no mod
/// code until a world loads.
///
/// It installs the same patches as the client mod (MainMenuPatches.cs is compiled into both), and
/// reads at each menu what the mod would otherwise have provided: the server address from the
/// launch ticket file, and the backgrounds from the client mod's zip in the Mods folder. The player
/// then picks Play themselves, so they can change settings (and restart for a new language) first.
///
/// Nothing here may stop the game from starting: every failure is logged next to this file and the
/// game keeps its own menu.
/// </summary>
// Must be named StartupHook, in no namespace, with a static Initialize(): that is what the runtime calls.
// ReSharper disable once UnusedType.Global CheckNamespace
internal class StartupHook
{
    private const string GameProcessName = "Vintagestory";
    private const string TicketFileName = "sombreterre-ticket.json";
    private const string ModZipPattern = "sombreterregate*.zip";
    private const string BackgroundFolder = "assets/sombreterregate/textures/gui/backgrounds/";
    private const long MaxBackgroundBytes = 32L * 1024 * 1024;

    private static readonly string LogPath = Path.Combine(Path.GetDirectoryName(typeof(StartupHook).Assembly.Location) ?? AppContext.BaseDirectory, "startup.log");
    private static bool _backgroundsRead;

    // ReSharper disable once UnusedMember.Global
    public static void Initialize()
    {
        try
        {
            // The variable reaches every .NET program the game starts too, such as its crash reporter.
            if (!string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), GameProcessName, StringComparison.OrdinalIgnoreCase)) return;
            File.WriteAllText(LogPath, "");
            // The game resolves its Lib folder itself, but only once its Main runs.
            AppDomain.CurrentDomain.AssemblyResolve += ResolveFromGame;
            Install();
        }
        catch (Exception e)
        {
            Log($"Startup hook failed, the game keeps its own menu: {e}");
        }
    }

    /// <summary>Kept apart from <see cref="Initialize"/>, so no game type is needed before the resolver is in place.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Install()
    {
        GateSession.BeforeMenu = ReadLaunchDetails;
        MainMenuPatches.Apply(new HookLogger());
    }

    /// <summary>The same search the game's own resolver does, minus the Mods folders.</summary>
    private static Assembly? ResolveFromGame(object? sender, ResolveEventArgs args)
    {
        var fileName = new AssemblyName(args.Name).Name + ".dll";
        foreach (var folder in new[] { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "Lib") })
        {
            var path = Path.Combine(folder, fileName);
            if (File.Exists(path)) return Assembly.LoadFrom(path);
        }
        return null;
    }

    private static void ReadLaunchDetails()
    {
        GateSession.Server = ReadServer(Path.Combine(GamePaths.ModConfig, TicketFileName));
        if (_backgroundsRead) return;
        _backgroundsRead = true;
        ReadBackgrounds();
    }

    private static string? ReadServer(string path)
    {
        if (!File.Exists(path))
        {
            Log("No launch ticket file: the menu has no Play button. Start the game from the launcher.");
            return null;
        }
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
        if (!document.RootElement.TryGetProperty("Server", out var server) || server.ValueKind != JsonValueKind.String) return null;
        var value = server.GetString()?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static void ReadBackgrounds()
    {
        var mods = GamePaths.DataPathMods;
        if (!Directory.Exists(mods)) return;
        var zip = new DirectoryInfo(mods).GetFiles(ModZipPattern).OrderByDescending(file => file.LastWriteTimeUtc).FirstOrDefault();
        if (zip == null)
        {
            Log("Client mod not found in the Mods folder: keeping the game's backgrounds.");
            return;
        }

        using var archive = ZipFile.OpenRead(zip.FullName);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (!name.StartsWith(BackgroundFolder, StringComparison.Ordinal) || !name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.Length is <= 0 or > MaxBackgroundBytes) continue;
            using var stream = entry.Open();
            using var bytes = new MemoryStream((int)entry.Length);
            stream.CopyTo(bytes);
            GateSession.Backgrounds.Add(bytes.ToArray());
        }
        Log($"{GateSession.Backgrounds.Count} background(s) read from {zip.Name}.");
    }

    internal static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Logging is a convenience; a locked or read-only folder must not stop the game.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>What the menu patches log, before the game's own loggers exist.</summary>
    private sealed class HookLogger : LoggerBase
    {
        protected override void LogImpl(EnumLogType logType, string format, params object[] args)
        {
            StartupHook.Log($"[{logType}] {(args.Length == 0 ? format : string.Format(format, args))}");
        }
    }
}
