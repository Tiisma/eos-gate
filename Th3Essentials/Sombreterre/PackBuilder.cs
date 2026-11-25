using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Th3Essentials.Sombreterre;

/// <summary>
/// Builds the pack manifest the Sombreterre launcher reads (format 1, see the launcher's
/// src/domain/sombreterre/pack.ts) out of the .zip mods in the server's folders. Mods from the optional
/// folders are listed apart, with their name and description, for players to choose in the launcher.
///
/// Server-side mods are never listed: players do not need them, and they are often the very code
/// a server keeps to itself. Anything that is not a .zip with a readable modinfo.json is skipped
/// and reported, since the launcher cannot place it.
/// </summary>
public class PackBuilder
{
    public class PrivateFile
    {
        public string FileName = "";
        public string FullPath = "";
    }

    public class Result
    {
        public string ManifestJson = "{}";
        public Dictionary<string, PrivateFile> PrivateFiles = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Skipped = new();
        public List<string> OptionalModIds = new();

        /// <summary>Main menu texts (tips, panel) served by name.</summary>
        public Dictionary<string, PrivateFile> MenuFiles = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The main menu logo, when the server has one.</summary>
        public PrivateFile? Logo;

        /// <summary>Main menu backgrounds served by name, like <see cref="PrivateFiles"/>.</summary>
        public Dictionary<string, PrivateFile> Backgrounds = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly Regex ModIdPattern = new("^[a-z0-9_-]{1,64}$", RegexOptions.IgnoreCase);
    private static readonly Regex VersionPattern = new("^[0-9A-Za-z.+-]{1,64}$");
    private static readonly Regex FileNamePattern = new("^[^\\\\/:*?\"<>|]{1,195}\\.zip$", RegexOptions.IgnoreCase);

    private static readonly Regex OptimumVersionPattern = new("^(latest|[0-9]+\\.[0-9]+\\.[0-9]+)$");

    /// <summary>True for a value <see cref="GateConfig.OptimumVersion"/> may hold: empty, "latest", or a plain three-part version.</summary>
    public static bool IsOptimumVersion(string value) => value.Length == 0 || OptimumVersionPattern.IsMatch(value);

    /// <summary>Most backgrounds one pack lists, and the largest one it accepts, so a folder dropped there by mistake cannot slow every launch.</summary>
    public const int MaxBackgrounds = 100;
    public const long MaxBackgroundBytes = 8 * 1024 * 1024;

    public const long MaxMenuFileBytes = 64 * 1024;
    private static readonly Regex MenuFileNamePattern = new("^(tips|panel)(\\.[a-z]{2})?\\.txt$");

    private static readonly Regex BackgroundNamePattern = new("^[A-Za-z0-9][A-Za-z0-9 _.()-]{0,99}\\.(png|jpe?g)$", RegexOptions.IgnoreCase);

    public static Result Build(string packName, string gameVersion, IEnumerable<string> folders, ICollection<string> modDbModIds, IEnumerable<string>? optionalFolders = null, string optimumVersion = "", string? backgroundsFolder = null, string? logoFile = null, string? menuFolder = null)
    {
        var result = new Result();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var moddb = new HashSet<string>(modDbModIds, StringComparer.OrdinalIgnoreCase);

        var entries = Collect(result, folders, seen, moddb, false);
        var optional = Collect(result, optionalFolders ?? Array.Empty<string>(), seen, moddb, true);

        var manifest = new JObject
        {
            ["format"] = 1,
            ["name"] = packName,
            ["gameVersion"] = gameVersion,
            ["mods"] = JArray.FromObject(entries),
            ["optional"] = JArray.FromObject(optional)
        };
        if (optimumVersion.Length > 0 && IsOptimumVersion(optimumVersion)) manifest["optimum"] = optimumVersion;
        var backgrounds = CollectBackgrounds(result, backgroundsFolder);
        if (backgrounds.Count > 0) manifest["backgrounds"] = backgrounds;
        var menu = CollectMenuFiles(result, menuFolder);
        if (menu.Count > 0) manifest["menu"] = menu;
        if (logoFile != null && File.Exists(logoFile))
        {
            var size = new FileInfo(logoFile).Length;
            if (!logoFile.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || size is 0 or > MaxBackgroundBytes)
                result.Skipped.Add($"logo {Path.GetFileName(logoFile)}: not a .png of at most {MaxBackgroundBytes / 1024 / 1024} MB");
            else
            {
                manifest["logo"] = new JObject { ["sha256"] = Sha256(logoFile) };
                result.Logo = new PrivateFile { FileName = Path.GetFileName(logoFile), FullPath = logoFile };
            }
        }
        result.ManifestJson = manifest.ToString(Formatting.Indented);
        return result;
    }

    private static List<object> Collect(Result result, IEnumerable<string> folders, HashSet<string> seen, HashSet<string> moddb, bool optional)
    {
        var entries = new List<object>();
        foreach (var folder in folders.Where(Directory.Exists))
        {
            foreach (var path in Directory.EnumerateFiles(folder).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var fileName = Path.GetFileName(path);
                if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    result.Skipped.Add($"{fileName}: not a .zip");
                    continue;
                }

                var info = ReadModInfo(path);
                if (info == null)
                {
                    result.Skipped.Add($"{fileName}: no readable modinfo.json");
                    continue;
                }

                var (modid, version, side) = info.Value;
                if (side.Equals("server", StringComparison.OrdinalIgnoreCase)) continue;
                if (!ModIdPattern.IsMatch(modid) || !VersionPattern.IsMatch(version))
                {
                    result.Skipped.Add($"{fileName}: modid or version the launcher cannot read");
                    continue;
                }
                if (!seen.Add(modid))
                {
                    result.Skipped.Add($"{fileName}: second copy of {modid}");
                    continue;
                }

                if (moddb.Contains(modid))
                {
                    // Pinned to the archive the server runs, so a ModDB file that changed under the same version is refused.
                    entries.Add(Described(new JObject { ["source"] = "moddb", ["modid"] = modid, ["version"] = version, ["sha256"] = Sha256(path) }, path, optional));
                    if (optional) result.OptionalModIds.Add(modid);
                    continue;
                }

                if (!FileNamePattern.IsMatch(fileName) || fileName.StartsWith('.') || fileName.Contains(".."))
                {
                    result.Skipped.Add($"{fileName}: file name the launcher refuses");
                    continue;
                }

                entries.Add(Described(new JObject { ["source"] = "private", ["modid"] = modid, ["version"] = version, ["file"] = fileName, ["sha256"] = Sha256(path) }, path, optional));
                if (optional) result.OptionalModIds.Add(modid);
                result.PrivateFiles[fileName] = new PrivateFile { FileName = fileName, FullPath = path };
            }
        }
        return entries;
    }

    private static JArray CollectMenuFiles(Result result, string? folder)
    {
        var list = new JArray();
        if (folder == null || !Directory.Exists(folder)) return list;
        foreach (var path in Directory.EnumerateFiles(folder).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(path);
            if (!MenuFileNamePattern.IsMatch(name))
            {
                result.Skipped.Add($"menu {name}: only tips.txt and panel.txt, optionally per language (tips.fr.txt), are read");
                continue;
            }
            if (new FileInfo(path).Length > MaxMenuFileBytes)
            {
                result.Skipped.Add($"menu {name}: over {MaxMenuFileBytes / 1024} KB");
                continue;
            }
            list.Add(new JObject { ["file"] = name, ["sha256"] = Sha256(path) });
            result.MenuFiles[name] = new PrivateFile { FileName = name, FullPath = path };
        }
        return list;
    }

    private static JArray CollectBackgrounds(Result result, string? folder)
    {
        var list = new JArray();
        if (folder == null || !Directory.Exists(folder)) return list;
        foreach (var path in Directory.EnumerateFiles(folder).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(path);
            if (!BackgroundNamePattern.IsMatch(name) || name.Contains(".."))
            {
                result.Skipped.Add($"background {name}: not a .png or .jpg with a plain name");
                continue;
            }
            if (new FileInfo(path).Length is 0 or > MaxBackgroundBytes)
            {
                result.Skipped.Add($"background {name}: empty or over {MaxBackgroundBytes / 1024 / 1024} MB");
                continue;
            }
            if (result.Backgrounds.Count >= MaxBackgrounds)
            {
                result.Skipped.Add($"background {name}: more than {MaxBackgrounds} backgrounds");
                continue;
            }
            list.Add(new JObject { ["file"] = name, ["sha256"] = Sha256(path) });
            result.Backgrounds[name] = new PrivateFile { FileName = name, FullPath = path };
        }
        return list;
    }

    /// <summary>What a player reads about an optional mod before choosing it.</summary>
    private static JObject Described(JObject entry, string zipPath, bool optional)
    {
        if (!optional) return entry;
        entry["name"] = ReadModInfoText(zipPath, "name") ?? entry["modid"];
        entry["description"] = ReadModInfoText(zipPath, "description") ?? "";
        return entry;
    }

    private static string? ReadModInfoText(string zipPath, string key)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.Entries.First(e => e.FullName.Equals("modinfo.json", StringComparison.OrdinalIgnoreCase));
        using var reader = new StreamReader(entry.Open());
        return JObject.Parse(reader.ReadToEnd()).Properties().FirstOrDefault(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value.ToString();
    }

    /// <summary>modid, version and side from a mod archive's modinfo.json. Keys are matched case-insensitively, as the game does.</summary>
    public static (string ModId, string Version, string Side)? ReadModInfo(string zipPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var entry = archive.Entries.FirstOrDefault(e => e.FullName.Equals("modinfo.json", StringComparison.OrdinalIgnoreCase));
            if (entry == null) return null;

            using var reader = new StreamReader(entry.Open());
            var json = JObject.Parse(reader.ReadToEnd());
            string? Get(string key) => json.Properties().FirstOrDefault(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value.ToString();

            var modid = Get("modid");
            var version = Get("version");
            if (string.IsNullOrWhiteSpace(modid) || string.IsNullOrWhiteSpace(version)) return null;
            return (modid, version, Get("side") ?? "universal");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
