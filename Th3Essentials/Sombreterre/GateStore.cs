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

        /// <summary>
        /// Discord user id to the game account its launcher plays with. Unproven until that account
        /// joins with a ticket and becomes a link: it only decides who goes on the server's whitelist.
        /// </summary>
        public Dictionary<ulong, PlayerClaim> Claims = new();
    }

    public class PlayerClaim
    {
        public string PlayerUid = "";
        public string PlayerName = "";
    }

    public class TokenRecord
    {
        public ulong DiscordId;
        public string DiscordName = "";
        public DateTime IssuedUtc;
        /// <summary>Last time the token was presented; refreshed at most once a day to keep writes rare.</summary>
        public DateTime LastUsedUtc;
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

    /// <summary>
    /// The record behind a token, or null when it is unknown or has gone unused for longer than
    /// <paramref name="idle"/>, in which case it is dropped.
    /// </summary>
    public TokenRecord? FindToken(string token, DateTime nowUtc, TimeSpan idle)
    {
        lock (_lock)
        {
            var key = Hash(token);
            if (!_data.Tokens.TryGetValue(key, out var record)) return null;

            var lastUsed = record.LastUsedUtc > record.IssuedUtc ? record.LastUsedUtc : record.IssuedUtc;
            if (nowUtc - lastUsed > idle)
            {
                _data.Tokens.Remove(key);
                Save();
                return null;
            }

            if (nowUtc - lastUsed > TimeSpan.FromDays(1))
            {
                record.LastUsedUtc = nowUtc;
                Save();
            }
            return record;
        }
    }

    /// <summary>Revokes every launcher token of one Discord account. Returns how many there were.</summary>
    public int RevokeAllFor(ulong discordId)
    {
        lock (_lock)
        {
            var keys = _data.Tokens.Where(t => t.Value.DiscordId == discordId).Select(t => t.Key).ToList();
            foreach (var key in keys) _data.Tokens.Remove(key);
            if (keys.Count > 0) Save();
            return keys.Count;
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

    public enum ClaimOutcome
    {
        Claimed,
        PlayerLinkedElsewhere,
        DiscordLinkedElsewhere
    }

    /// <summary>
    /// Records the game account a launcher says it plays with. A link always wins over a claim. Between
    /// claims the latest wins, since nothing has proven either yet: a false one only puts an account on
    /// the whitelist, and the server still turns that account away without a ticket.
    /// </summary>
    /// <param name="discordId">The Discord account of the launcher.</param>
    /// <param name="playerUid">The game account it says it plays with.</param>
    /// <param name="playerName">That account's name.</param>
    /// <param name="droppedUid">An account this claim replaced and nobody else is tied to, for the whitelist to drop.</param>
    public ClaimOutcome Claim(ulong discordId, string playerUid, string playerName, out string? droppedUid)
    {
        droppedUid = null;
        lock (_lock)
        {
            if (_data.Links.TryGetValue(playerUid, out var linkedTo) && linkedTo != discordId) return ClaimOutcome.PlayerLinkedElsewhere;
            if (_data.Links.Any(link => link.Value == discordId && link.Key != playerUid)) return ClaimOutcome.DiscordLinkedElsewhere;

            _data.Claims.TryGetValue(discordId, out var previous);
            if (previous != null && previous.PlayerUid == playerUid && previous.PlayerName == playerName) return ClaimOutcome.Claimed;

            foreach (var other in _data.Claims.Where(claim => claim.Key != discordId && claim.Value.PlayerUid == playerUid).Select(claim => claim.Key).ToList())
                _data.Claims.Remove(other);
            if (previous != null && previous.PlayerUid != playerUid && !_data.Links.ContainsKey(previous.PlayerUid)) droppedUid = previous.PlayerUid;

            _data.Claims[discordId] = new PlayerClaim { PlayerUid = playerUid, PlayerName = playerName };
            Save();
            return ClaimOutcome.Claimed;
        }
    }

    /// <summary>The game account tied to a Discord account: its link, otherwise its claim. The name is empty when only a link is known.</summary>
    public PlayerClaim? AccountOf(ulong discordId)
    {
        lock (_lock)
        {
            _data.Claims.TryGetValue(discordId, out var claim);
            var linked = _data.Links.FirstOrDefault(link => link.Value == discordId).Key;
            if (linked == null) return claim;
            return new PlayerClaim { PlayerUid = linked, PlayerName = claim?.PlayerUid == linked ? claim.PlayerName : "" };
        }
    }

    /// <summary>Every Discord account tied to a game account, by link or by claim.</summary>
    public List<ulong> AccountHolders()
    {
        lock (_lock)
        {
            return _data.Links.Values.Concat(_data.Claims.Keys).Distinct().ToList();
        }
    }

    /// <summary>Removes the link of a game account. Returns the Discord account it was linked to.</summary>
    public ulong? Unlink(string playerUid)
    {
        lock (_lock)
        {
            if (!_data.Links.Remove(playerUid, out var discordId)) return null;
            if (_data.Claims.TryGetValue(discordId, out var claim) && claim.PlayerUid == playerUid) _data.Claims.Remove(discordId);
            Save();
            return discordId;
        }
    }

    /// <summary>32 random bytes, base64url. Used for tokens, tickets and link sessions.</summary>
    public static string RandomToken()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
