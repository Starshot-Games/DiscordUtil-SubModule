using Microsoft.Extensions.Hosting;
using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Hosting.Services.ApplicationCommands;
using NetCord.Rest;

namespace JMTech.Shared.NetCord;

public static class DiscordUtil
{
    const GatewayIntents DefaultIntents = GatewayIntents.MessageContent | GatewayIntents.GuildMessages | GatewayIntents.Guilds;
    
    public static HostApplicationBuilder CreateBuilder(string discordToken, GatewayIntents intents = DefaultIntents)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Services.AddDiscordGateway().AddApplicationCommands();
        builder.Services.ConfigureDiscordGateway(client =>
        {
            client.Token = discordToken;
            client.Intents = intents;
        });
        return builder;
    }
    public static async ValueTask<TextChannel> GetTextChannelAsync(this RestClient client, ulong channelId)
    {
        Channel channel = await client.GetChannelAsync(channelId);

        if (channel is not TextChannel textChannel)
            throw new Exception("This is not a text channel!");

        return textChannel;
    }
}