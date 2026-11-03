using System;

namespace Th3Essentials.Sombreterre;

/// <summary>Where a Discord account stands with the server's guild, as the bot last saw it.</summary>
public enum MembershipStatus
{
    /// <summary>The bot is not connected yet, so nothing can be said.</summary>
    Unknown,
    NotMember,
    MemberWithoutRole,
    Whitelisted
}

public enum GateOutcome
{
    Admitted,
    InvalidTicket,
    PlayerLinkedElsewhere,
    DiscordLinkedElsewhere,
    NotMember,
    NotWhitelisted,
    DiscordUnavailable
}

/// <summary>
/// Decides whether one player who presented one ticket may stay. Pure: the game identity comes from
/// the server's own authentication, the Discord identity from the ticket, the role from the bot.
/// A first valid ticket also creates the link, which is what makes linking need no command.
/// </summary>
public static class GateDecision
{
    public static GateOutcome Evaluate(string playerUid, string ticket, GateStore store, TicketBook tickets, Func<ulong, MembershipStatus> membership, DateTime nowUtc, TimeSpan session)
    {
        var discordId = tickets.Redeem(ticket, playerUid, nowUtc, session);
        if (discordId == null) return GateOutcome.InvalidTicket;

        // Checked before linking, so an account that is not whitelisted yet is not tied to anything.
        var status = membership(discordId.Value);
        switch (status)
        {
            case MembershipStatus.Unknown: return GateOutcome.DiscordUnavailable;
            case MembershipStatus.NotMember: return GateOutcome.NotMember;
            case MembershipStatus.MemberWithoutRole: return GateOutcome.NotWhitelisted;
        }

        return store.Link(playerUid, discordId.Value) switch
        {
            GateStore.LinkOutcome.PlayerLinkedElsewhere => GateOutcome.PlayerLinkedElsewhere,
            GateStore.LinkOutcome.DiscordLinkedElsewhere => GateOutcome.DiscordLinkedElsewhere,
            _ => GateOutcome.Admitted
        };
    }
}
