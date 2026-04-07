using NetCord;
using NetCord.Rest;

namespace JMTech.Shared.NetCord;

public static class Extensions
{
    public static async ValueTask<TextChannel> GetTextChannelAsync(this RestClient client, ulong channelId)
    {
        Channel channel = await client.GetChannelAsync(channelId);

        if (channel is not TextChannel textChannel)
            throw new Exception("This is not a text channel!");

        return textChannel;
    }
}