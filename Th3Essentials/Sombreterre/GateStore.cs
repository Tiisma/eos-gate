using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace Th3Essentials.Sombreterre;

/// <summary>
/// What the gate remembers between restarts: which game account belongs to which Discord account,
/// and the launcher tokens it has handed out. Tokens are stored as SHA-256 digests only, so the file
/// leaking does not leak a usable token.
/// </summary>
public class GateStore
{
    public class Data
    {
        /// <summary>Game player UID to Discord user id.</summary>
        public Dictionary<string, ulong> Links = new();

        /// <summary>Hex SHA-256 of a launcher token to the Discord user id it was issued to.</summary>
        public Dictionary<string, TokenRecord> Tokens = new();
    }

    public class TokenRecord
    {
        public ulong DiscordId;
        public string DiscordName = "";
        public DateTime IssuedUtc;
    }

    private readonly string? _path;
    private readonly object _lock = new();
    private Data _data;

    /// <param name="path">File to persist to, or null to keep everything in memory (tests).</param>
    public GateStore(string? path)
    {
        _path = path;
        _data = Load(path);
    }

    private static Data Load(string? path)
    {
        if (path == null || !File.Exists(path)) return new Data();
        try
        {
            return JsonConvert.DeserializeObject<Data>(File.ReadAllText(path)) ?? new Data();
        }
        catch (JsonException)
        {
            // An unreadable store is moved aside rather than overwritten, so links are recoverable by hand.
            File.Move(path, path + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
            return new Data();
        }
    }

    private void Save()
    {
        if (_path == null) return;
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonConvert.SerializeObject(_data, Formatting.Indented));
        File.Move(temp, _path, true);
    }

    public static string Hash(string token)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    }

    /// <summary>Creates a launcher token for this Discord account. Returned once, never stored in clear.</summary>
    public string IssueToken(ulong discordId, string discordName, DateTime nowUtc)
    {
        var token = RandomToken();
        lock (_lock)
        {
            _data.Tokens[Hash(token)] = new TokenRecord { DiscordId = discordId, DiscordName = discordName, IssuedUtc = nowUtc };
            Save();
        }
        return token;
    }

    public TokenRecord? FindToken(string token)
    {
        lock (_lock)
        {
            return _data.Tokens.TryGetValue(Hash(token), out var record) ? record : null;
        }
    }

    public bool RevokeToken(string token)
    {
        lock (_lock)
        {
            if (!_data.Tokens.Remove(Hash(token))) return false;
            Save();
            return true;
        }
    }

    public ulong? LinkOf(string playerUid)
    {
        lock (_lock)
        {
            return _data.Links.TryGetValue(playerUid, out var id) ? id : null;
        }
    }

    public string? PlayerOf(ulong discordId)
    {
        lock (_lock)
        {
            return _data.Links.FirstOrDefault(link => link.Value == discordId).Key;
        }
    }

    public enum LinkOutcome
    {
        Linked,
        AlreadyLinked,
        PlayerLinkedElsewhere,
        DiscordLinkedElsewhere
    }

    /// <summary>
    /// Ties a game account to a Discord account, one to one. An existing link is never replaced:
    /// moving an account to another Discord is a staff decision, made with <see cref="Unlink"/>.
    /// </summary>
    public LinkOutcome Link(string playerUid, ulong discordId)
    {
        lock (_lock)
        {
            if (_data.Links.TryGetValue(playerUid, out var existing))
                return existing == discordId ? LinkOutcome.AlreadyLinked : LinkOutcome.PlayerLinkedElsewhere;
            if (_data.Links.Any(link => link.Value == discordId)) return LinkOutcome.DiscordLinkedElsewhere;

            _data.Links[playerUid] = discordId;
            Save();
            return LinkOutcome.Linked;
        }
    }

    public bool Unlink(string playerUid)
    {
        lock (_lock)
        {
            if (!_data.Links.Remove(playerUid)) return false;
            Save();
            return true;
        }
    }

    /// <summary>32 random bytes, base64url. Used for tokens, tickets and link sessions.</summary>
    public static string RandomToken()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
