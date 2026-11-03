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
/// src/domain/sombreterre/pack.ts) out of the .zip mods in the server's folders.
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
    }

    private static readonly Regex ModIdPattern = new("^[a-z0-9_-]{1,64}$", RegexOptions.IgnoreCase);
    private static readonly Regex VersionPattern = new("^[0-9A-Za-z.+-]{1,64}$");
    private static readonly Regex FileNamePattern = new("^[^\\\\/:*?\"<>|]{1,195}\\.zip$", RegexOptions.IgnoreCase);

    public static Result Build(string packName, string gameVersion, IEnumerable<string> folders, ICollection<string> modDbModIds)
    {
        var result = new Result();
        var entries = new List<object>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var moddb = new HashSet<string>(modDbModIds, StringComparer.OrdinalIgnoreCase);

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
                    entries.Add(new { source = "moddb", modid, version });
                    continue;
                }

                if (!FileNamePattern.IsMatch(fileName) || fileName.StartsWith('.') || fileName.Contains(".."))
                {
                    result.Skipped.Add($"{fileName}: file name the launcher refuses");
                    continue;
                }

                entries.Add(new { source = "private", modid, version, file = fileName, sha256 = Sha256(path) });
                result.PrivateFiles[fileName] = new PrivateFile { FileName = fileName, FullPath = path };
            }
        }

        result.ManifestJson = JsonConvert.SerializeObject(new { format = 1, name = packName, gameVersion, mods = entries }, Formatting.Indented);
        return result;
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
