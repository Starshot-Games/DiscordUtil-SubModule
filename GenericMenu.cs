using System.Diagnostics;
using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace JMTech.Shared.NetCord;

public delegate ValueTask ButtonHandler(GenericMenu menu);
public class GenericMenu
{
    readonly Guid id;
    readonly Action<MessageProperties> decorator;
    readonly Button[] buttons;
    readonly GatewayClient gateway;
    readonly int? timeout;
    
    RestMessage? message;

    protected GenericMenu(Guid id, Action<MessageProperties> decorator, Button[] buttons, GatewayClient gateway, int? timeout)
    {
        this.id = id;
        this.decorator = decorator;
        this.buttons = buttons;
        this.gateway = gateway;
        this.timeout = timeout;
    }

    public async ValueTask UpdateMessage(Action<MessageOptions> decorator)
    {
        await message!.ModifyAsync(edit =>
        {
            edit.WithComponents(GetComponents());
            decorator.Invoke(edit);
            InternalDecorate(edit);
        });
    }
    public async ValueTask Close()
    {
        DeregisterInteractionHandler();
        await message!.ModifyAsync(edit => edit.WithComponents([]));
    }
    public async ValueTask Remove()
    {
        DeregisterInteractionHandler();
        await message!.DeleteAsync();
    }

    async ValueTask SendInitialMessage(ulong channelId, RestClient client)
    {
        await SendInitialMessage(async msg => await client.SendMessageAsync(channelId, msg));
    }
    async ValueTask SendInitialMessage(TextChannel channel)
    {
        await SendInitialMessage(async msg => await channel.SendMessageAsync(msg));
    }
    async ValueTask SendInitialMessage(Func<MessageProperties, ValueTask<RestMessage>> send)
    {
        MessageProperties msg = new();
        decorator.Invoke(msg);
        InternalDecorate(msg);
        msg.WithComponents(GetComponents());
        message = await send.Invoke(msg);

        if (timeout is not { } timeoutVal)
            return;

        _ = Task.Run(TimeoutRunner);
        async Task TimeoutRunner()
        {
            Console.WriteLine($"Running timeout timer for {id.ToString()}");
            await Task.Delay(timeoutVal * 1000);
            Console.WriteLine($"Timeout on {id.ToString()}");
            await Close();
        }
    }

    void InternalDecorate(MessageProperties msg)
    {
        if (timeout != null)
            msg.Content += $"\n\n*(Menu expires in {timeout}s)*";
    }
    void InternalDecorate(MessageOptions msg)
    {
        if (timeout != null)
            msg.Content += $"\n\n*(Menu expires in {timeout}s)*";
    }
    
    void RegisterInteractionHandler() => gateway.InteractionCreate += OnInteractionCreate;
    void DeregisterInteractionHandler() => gateway.InteractionCreate -= OnInteractionCreate;
    async ValueTask OnInteractionCreate(Interaction interaction)
    {
        if (interaction is not ButtonInteraction button)
            return;
        
        Console.WriteLine($"Menu interaction!\n" +
                          $"cId = {button.Data.CustomId}\n" +
                          $"menuId = {this.id.ToString()}");

        if (!button.Data.CustomId.Contains('/'))
            return;

        string[] parts = button.Data.CustomId.Split('/');
        string guid = parts[0];
        int id = int.Parse(parts[1]);

        if (guid != this.id.ToString())
            return;

        await interaction.SendResponseAsync(InteractionCallback.DeferredModifyMessage);
        await buttons[id].onClick.Invoke(this);
    }
    
    List<IMessageComponentProperties> GetComponents()
    {
        List<IMessageComponentProperties> components = [];

        for (int row = 0; row < buttons.Length; row += 5)
        {
            ActionRowProperties actionRow = [];
            components.Add(actionRow);
            
            for (int button = row; button < row + 5 && button < buttons.Length; button++)
                actionRow.Add(new ButtonProperties($"{id}/{button}", buttons[button].label, buttons[button].style).WithDisabled(buttons[button].disabled));
        }

        return components;
    }

    public static async Task<GenericMenu> Open(ApplicationCommandContext context, Action<MessageProperties> decorator, params Button[] buttons)
    {
        Guid id = Guid.NewGuid();
        GenericMenu menu = new(id, decorator, buttons, context.Client, null);
        menu.RegisterInteractionHandler();
        await menu.SendInitialMessage(context.Channel);
        return menu;
    }
    public static async Task<GenericMenu> Open(RestClient rest, GatewayClient gateway, ulong channelId, Action<MessageProperties> decorator, params Button[] buttons)
    {
        Guid id = Guid.NewGuid();
        GenericMenu menu = new(id, decorator, buttons, gateway, null);
        menu.RegisterInteractionHandler();
        await menu.SendInitialMessage(channelId, rest);
        return menu;
    }
    public static async Task<GenericMenu> Open(ApplicationCommandContext context, Action<MessageProperties> decorator, int? timeout, params Button[] buttons)
    {
        Guid id = Guid.NewGuid();
        GenericMenu menu = new(id, decorator, buttons, context.Client, timeout);
        menu.RegisterInteractionHandler();
        await menu.SendInitialMessage(context.Channel);
        return menu;
    }
    public static async Task<GenericMenu> Open(RestClient rest, GatewayClient gateway, ulong channelId, Action<MessageProperties> decorator, int? timeout, params Button[] buttons)
    {
        Guid id = Guid.NewGuid();
        GenericMenu menu = new(id, decorator, buttons, gateway, timeout);
        menu.RegisterInteractionHandler();
        await menu.SendInitialMessage(channelId, rest);
        return menu;
    }
    public static async Task<GenericMenu> OpenConfirm(ApplicationCommandContext context, 
                                                      Action<MessageProperties> decorator, 
                                                      ButtonHandler onYes, 
                                                      ButtonHandler onNo,
                                                      string yesText = "Yes",
                                                      string noText = "No",
                                                      int? timeout = null)
    {
        return await Open(context, decorator, timeout, new Button(ButtonStyle.Success, yesText, onYes), new Button(ButtonStyle.Danger, noText, onNo));
    }
    public static async Task<GenericMenu> OpenConfirm(RestClient rest, 
                                                      GatewayClient gateway, 
                                                      ulong channelId, 
                                                      Action<MessageProperties> decorator, 
                                                      ButtonHandler onYes, 
                                                      ButtonHandler onNo,
                                                      string yesText = "Yes",
                                                      string noText = "No",
                                                      int? timeout = null)
    {
        return await Open(rest, gateway, channelId, decorator, timeout, new Button(ButtonStyle.Success, yesText, onYes), new Button(ButtonStyle.Danger, noText, onNo));
    }
}

public struct Button(ButtonStyle style, string label, ButtonHandler onClick, bool disabled = false)
{
    public readonly ButtonStyle style = style;
    public readonly string label = label;
    public readonly ButtonHandler onClick = onClick;
    public readonly bool disabled = disabled;
}