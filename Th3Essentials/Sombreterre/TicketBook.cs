using System;
using System.Collections.Generic;
using System.Linq;

namespace Th3Essentials.Sombreterre;

/// <summary>
/// Launch tickets: short-lived, single-use proofs that a launch went through the launcher of a
/// whitelisted Discord account. The launcher drops one in the game folder, the client mod sends it
/// on join. Game traffic is not encrypted, so a ticket is worthless once used or expired.
/// Kept in memory only: a restart simply makes launchers ask for a new one.
/// </summary>
public class TicketBook
{
    private readonly Dictionary<string, (ulong DiscordId, DateTime ExpiresUtc)> _tickets = new();
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
            _tickets[ticket] = (discordId, nowUtc + lifetime);
        }
        return ticket;
    }

    /// <summary>The Discord account a ticket was issued to, removing it. Null when unknown, used or expired.</summary>
    public ulong? Consume(string ticket, DateTime nowUtc)
    {
        lock (_lock)
        {
            if (!_tickets.Remove(ticket, out var entry)) return null;
            return entry.ExpiresUtc > nowUtc ? entry.DiscordId : null;
        }
    }

    private void Prune(DateTime nowUtc)
    {
        foreach (var key in _tickets.Where(t => t.Value.ExpiresUtc <= nowUtc).Select(t => t.Key).ToList()) _tickets.Remove(key);
    }
}
