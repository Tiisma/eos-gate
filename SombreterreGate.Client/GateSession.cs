using System;
using System.Collections.Generic;

namespace SombreterreGate.Client;

/// <summary>
/// What this game process knows about its launch: the ticket and the server. Static on purpose, so it
/// outlives the world it was read in. The server binds the ticket to this player's game account on
/// first use, so resending it after a disconnect (the main menu's Play button) lets this player, and
/// only this player, back in for the rest of the session.
///
/// Shared with SombreterreGate.Startup, which compiles this file into its own assembly: each assembly
/// then has its own copy of these fields.
/// </summary>
// Each of the two assemblies assigns only some of these fields.
#pragma warning disable CS0649
internal static class GateSession
{
    public static string? Ticket;
    public static string? Server;

    /// <summary>Main menu backgrounds, as PNG bytes.</summary>
    public static readonly List<byte[]> Backgrounds = new();

    /// <summary>
    /// Run each time the main menu composes, before its buttons are added. The startup hook fills
    /// <see cref="Server"/> and <see cref="Backgrounds"/> here, since nothing else does before a world loads.
    /// </summary>
    public static Action? BeforeMenu;
}
#pragma warning restore CS0649
