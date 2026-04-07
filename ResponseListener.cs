using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

namespace JMTech.Shared.NetCord;

public delegate Task<ListenerAction> OnResponse(RestMessage msg);
public class ResponseListener
{
    readonly GatewayClient client;
    readonly OnResponse onResponse;
    readonly MessageFilter? filter;
    readonly int? timeout;
    
    public Action? onStop;
    
    bool started;

    public ResponseListener(GatewayClient client, OnResponse onResponse, MessageFilter? filter = null, int? timeout = null)
    {
        this.client = client;
        this.onResponse = onResponse;
        this.filter = filter;
        this.timeout = timeout;
    }

    public void Start()
    {
        if (started)
            return;

        started = true;
        client.MessageCreate += OnMessageCreate;

        if (timeout is not { } timeoutVal)
            return;

        _ = Task.Run(Timeout);
        async ValueTask Timeout()
        {
            await Task.Delay(timeoutVal * 1000);

            if (!started)
                return;
            
            Stop();
        }
    }
    public void Stop()
    {
        if (!started)
            return;

        started = false;
        client.MessageCreate -= OnMessageCreate;
        onStop?.Invoke();
    }

    async ValueTask OnMessageCreate(Message message)
    {
        if (filter != null && !filter.Value.Accepts(message))
            return;

        if (await onResponse.Invoke(message) == ListenerAction.StopListening)
            Stop();
    }

    public static async Task<ResponseListener> Auto(NetCordClients clients, ulong channelId, string message, OnResponse onResponse, int timeout, MessageFilter? filter = null)
    {
        RestMessage restMsg = await clients.rest.SendMessageAsync(channelId, message + $"\n\n***(Expires in {timeout}s)***");
        ResponseListener listener = new(clients.gateway, onResponse, filter, timeout);
        listener.onStop += () =>
        {
            _ = restMsg.DeleteAsync();
        };
        listener.Start();
        return listener;
    }
    public static async Task<ResponseListener> Manual(NetCordClients clients, ulong channelId, string message, OnResponse onResponse, MessageFilter? filter = null)
    {
        ResponseListener listener = new(clients.gateway, onResponse, filter);
        GenericMenu menu = await GenericMenu.Open(clients.rest, clients.gateway, channelId, msg => msg.WithContent(message), null, new Button(ButtonStyle.Danger, "Cancel", async menu =>
        {
            listener.Stop();
        }));
        listener.onStop += () =>
        {
            _ = menu.Remove();
        };
        listener.Start();
        return listener;
    }
}

public struct MessageFilter
{
    /// <summary>Requires at least one attachment</summary>
    public bool attachments;
    public ulong? channelId;
    public ulong? userId;

    public bool Accepts(RestMessage message)
    {
        if (attachments && message.Attachments.Count == 0)
            return false;

        if (channelId != null && message.ChannelId != channelId)
            return false;

        if (userId != null && message.Author.Id != userId)
            return false;

        return true;
    }
}


public enum ListenerAction
{
    KeepListening,
    StopListening
}