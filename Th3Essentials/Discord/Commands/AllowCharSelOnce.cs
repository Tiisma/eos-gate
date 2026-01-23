using System.Collections.Generic;
using System.Linq;
using Discord;
using Discord.WebSocket;
using Vintagestory.API.Config;
using Vintagestory.API.Util;

namespace Th3Essentials.Discord.Commands;

public abstract class AllowCharSelOnce
{
    public static SlashCommandProperties CreateCommand()
    {
        List<SlashCommandOptionBuilder> charSelectOptions = new List<SlashCommandOptionBuilder>()
        {
            new()
            {
                Name = "playername",
                Description = Lang.Get("th3essentials:slc-allowcharselonce-playername"),
                Type = ApplicationCommandOptionType.String,
                IsRequired = true
            }
        };
        var allowcharselonce = new SlashCommandBuilder
        {
            Name = SlashCommands.AllowCharSelOnce.ToString().ToLower(),
            Description = Lang.Get("th3essentials:slc-allowcharselonce"),
            Options = charSelectOptions
        };
        return allowcharselonce.Build();
    }

    public static string HandleSlashCommand(Th3Discord discord, SocketSlashCommand commandInteraction)
    {
        if (commandInteraction.User is SocketGuildUser guildUser)
        {
            if (Th3SlashCommands.HasPermission(guildUser, discord.Config.ModerationRoles))
            {
                var option = commandInteraction.Data.Options.First();
                if (option.Value is string playername)
                {
                    var playerData = discord.Sapi.PlayerData.GetPlayerDataByLastKnownName(playername);
                    if (playerData != null)
                    {
                        var player = discord.Sapi.World.AllOnlinePlayers.FirstOrDefault(p => p.PlayerUID.Equals(playerData.PlayerUID));
                        if (player != null)
                        {
                            var allowcharselonce = player.Entity.WatchedAttributes.GetBool("allowcharselonce");
                            if (!allowcharselonce)
                            {
                                player.Entity.WatchedAttributes.SetBool("allowcharselonce", true);
                                discord.Sapi.Logger.Audit($"{guildUser.DisplayName}({guildUser.Id}) granted charsel to {playername}.");
                                return Lang.Get("Ok, player can now run .charsel to change skin and character class once");
                            }
                            return "Player already has charsel permission";
                        }

                        return "Player is not online";
                    }

                    return "Could not find that player";
                }

                return "Error: playername needs to be set";
            }

            return "You do not have permissions to do that";
        }

        return "Something went wrong: User was not a GuildUser";
    }
}