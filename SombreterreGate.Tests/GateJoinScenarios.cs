using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Th3Essentials.Sombreterre;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Xunit;

namespace SombreterreGate.Tests;

/// <summary>
/// What a player without a launch ticket can do on a live headless server with the gate on: be
/// frozen as a spectator, be disconnected once the grace period is over, and be turned away on an
/// immediate retry. The Discord bot is not configured here, so no ticket can be accepted; admission
/// itself is covered by the unit tests, and the restore of the player's own mode is driven directly.
/// </summary>
[AtlasDataFiles("fixtures/ModConfig", TargetPath = "ModConfig")]
public class GateJoinScenarios : AtlasScenarioBase
{
    private const string SavedModeKey = "sombreterregate-mode";

    private IServerPlayer ServerPlayer(ITestPlayer player) => (IServerPlayer)World.Api.World.PlayerByUid(player.Player.PlayerUID);

    [AtlasScenario]
    public async Task A_PlayerWithoutTicket_IsFrozenThenRefusedThenCooledDown()
    {
        ITestPlayer player = await World.JoinPlayer("Stranger");

        await World.Until(() => player.Player.WorldData.CurrentGameMode == EnumGameMode.Spectator, 200);
        Assert.Equal(EnumGameMode.Spectator, player.Player.WorldData.CurrentGameMode);
        Assert.True(ServerPlayer(player).ServerData.CustomPlayerData.ContainsKey(SavedModeKey));
        await World.Until(() => player.Client.PlayerData().Any(d => d.IsSelf && d.GameMode == EnumGameMode.Spectator), 200);

        // Chat from a player who has not been admitted reaches nobody.
        ITestPlayer witness = await World.JoinPlayer("Witness");
        witness.Client.Clear();
        player.Say("can anyone hear me");
        await World.Ticks(20);
        Assert.DoesNotContain(witness.Client.ChatLines(), line => line.Contains("can anyone hear me"));

        // The grace period (3 s in the fixture) ends with a refusal.
        await World.Until(() => !player.IsConnected, 600);
        Assert.False(player.IsConnected);

        // An immediate retry is turned away straight on join.
        ITestPlayer retry = await World.JoinPlayer("Stranger");
        await World.Until(() => !retry.IsConnected, 200);
        Assert.False(retry.IsConnected);
    }

    [AtlasScenario]
    public async Task B_Admission_GivesThePlayerTheirOwnModeBack()
    {
        ITestPlayer player = await World.JoinPlayer("Returning");
        await World.Until(() => player.Player.WorldData.CurrentGameMode == EnumGameMode.Spectator, 200);
        var saved = ServerPlayer(player).ServerData.CustomPlayerData[SavedModeKey];
        Assert.NotEqual(nameof(EnumGameMode.Spectator), saved);

        // What OnTicket does after an accepted ticket, driven directly: no Discord here.
        var gate = World.Api.ModLoader.GetModSystem<SombreterreGateSystem>();
        var unfreeze = typeof(SombreterreGateSystem).GetMethod("Unfreeze", BindingFlags.Instance | BindingFlags.NonPublic)!;
        unfreeze.Invoke(gate, new object[] { ServerPlayer(player) });

        await World.Until(() => player.Player.WorldData.CurrentGameMode.ToString() == saved, 200);
        Assert.Equal(saved, player.Player.WorldData.CurrentGameMode.ToString());
        Assert.False(ServerPlayer(player).ServerData.CustomPlayerData.ContainsKey(SavedModeKey));
    }
}
