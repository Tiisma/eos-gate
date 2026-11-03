using System;
using System.Collections.Generic;
using System.Linq;

namespace Th3Essentials.Sombreterre;

/// <summary>
/// Launch tickets: short-lived proofs that a launch went through the launcher of a whitelisted
/// Discord account. The launcher drops one in the game folder, the client mod sends it on join.
///
/// The first join binds a ticket to the game account that presented it. From then on it opens the
/// server for that account only, for the rest of the game session, which is what lets the game's own
/// Play button reconnect without going back through the launcher. Game traffic is not encrypted, but
/// a ticket read off the wire is worthless to anyone else: the server authenticates the game account
/// itself, and a bound ticket refuses every other one.
/// Kept in memory only: a restart simply makes launchers ask for a new one.
/// </summary>
public class TicketBook
{
    private readonly Dictionary<string, (ulong DiscordId, DateTime ExpiresUtc, string? PlayerUid)> _tickets = new();
    private readonly object _lock = new();

    /// <summary>
    /// Issues a ticket. One Discord account holds at most one: a new one cancels the previous, so a
    /// whitelisted account cannot stockpile tickets to hand to other players.
    /// </summary>
    public string Issue(ulong discordId, DateTime nowUtc, TimeSpan lifetime)
    {
        var ticket = GateStore.RandomToken();
        lock (_lock)
        {
            Prune(nowUtc);
            foreach (var key in _tickets.Where(t => t.Value.DiscordId == discordId).Select(t => t.Key).ToList()) _tickets.Remove(key);
            _tickets[ticket] = (discordId, nowUtc + lifetime, null);
        }
        return ticket;
    }

    /// <summary>
    /// The Discord account a ticket was issued to, when <paramref name="playerUid"/> may use it: an
    /// unused ticket is bound to that account for <paramref name="session"/>, a bound one only answers
    /// its own account. Null when unknown, expired or bound to another account.
    /// </summary>
    public ulong? Redeem(string ticket, string playerUid, DateTime nowUtc, TimeSpan session)
    {
        lock (_lock)
        {
            if (!_tickets.TryGetValue(ticket, out var entry)) return null;
            if (entry.ExpiresUtc <= nowUtc)
            {
                _tickets.Remove(ticket);
                return null;
            }
            if (entry.PlayerUid == null)
            {
                _tickets[ticket] = (entry.DiscordId, nowUtc + session, playerUid);
                return entry.DiscordId;
            }
            return entry.PlayerUid == playerUid ? entry.DiscordId : null;
        }
    }

    private void Prune(DateTime nowUtc)
    {
        foreach (var key in _tickets.Where(t => t.Value.ExpiresUtc <= nowUtc).Select(t => t.Key).ToList()) _tickets.Remove(key);
    }
}
