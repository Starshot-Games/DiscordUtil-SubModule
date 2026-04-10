using NetCord.Rest;

namespace CaptainBuild.Notifications;

public class BufferedMessage
{
    bool isEnded;

    public string content;
    public Action<MessageOptions>? modifier;
    RestMessage msg = null!;

    readonly RestClient client;
    readonly ulong channelId;
    
    BufferedMessage(RestClient client, ulong channelId, string initialContent)
    {
        this.client = client;
        this.channelId = channelId;
        content = initialContent;
    }

    async Task Run()
    {
        msg = await client.SendMessageAsync(channelId, content);
        _ = Task.Run(Body);

        async Task Body()
        {
            while (!isEnded)
            {
                await msg.ModifyAsync(edit =>
                {
                    edit.WithContent(content);
                    modifier?.Invoke(edit);
                });
                await Task.Delay(2000);
            }
            
            // Do one final update
            await msg.ModifyAsync(edit =>
            {
                edit.WithContent(content);
                modifier?.Invoke(edit);
            });
        }
    }

    public async Task Stop(Action<MessageOptions>? modifier = null)
    {
        this.modifier += modifier;
        isEnded = true;
    }
    
    ~BufferedMessage()
    {
        isEnded = false;
    }

    public static async Task<BufferedMessage> Create(RestClient client, ulong channelId, string initialContent)
    {
        BufferedMessage msg = new(client, channelId, initialContent);
        await msg.Run();
        return msg;
    }
}