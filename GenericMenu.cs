using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace JMTech.Shared.NetCord;

public delegate ValueTask ButtonHandler(GenericMenu menu);
public delegate ValueTask DropdownHandler(GenericMenu menu, IReadOnlyList<string> selected);

public class GenericMenu
{
    readonly Guid id;
    readonly Action<MessageProperties> decorator;
    readonly Dropdown[] dropdowns;
    readonly Button[] buttons;
    readonly GatewayClient gateway;
    readonly int? timeout;

    // Latest selected values per dropdown (by index). Updated on each
    // StringMenuInteraction so button handlers can read the current selection
    // server-side without round-tripping through the message.
    readonly Dictionary<int, IReadOnlyList<string>> dropdownSelections = new();

    RestMessage? message;

    protected GenericMenu(Guid id, Action<MessageProperties> decorator, Dropdown[] dropdowns, Button[] buttons, GatewayClient gateway, int? timeout)
    {
        this.id = id;
        this.decorator = decorator;
        this.dropdowns = dropdowns;
        this.buttons = buttons;
        this.gateway = gateway;
        this.timeout = timeout;
    }

    /// <summary>Latest selection values for the dropdown at <paramref name="dropdownIndex"/>; empty if no selection yet.</summary>
    public IReadOnlyList<string> GetSelected(int dropdownIndex) =>
        dropdownSelections.TryGetValue(dropdownIndex, out IReadOnlyList<string>? v) ? v : Array.Empty<string>();

    /// <summary>First selected value for the dropdown, or null if no selection yet.</summary>
    public string? GetSelectedSingle(int dropdownIndex) =>
        GetSelected(dropdownIndex).Count > 0 ? GetSelected(dropdownIndex)[0] : null;

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
        // Custom-id scheme:
        //   buttons:   "{guid}/{index}"
        //   dropdowns: "{guid}/d{index}"   (the 'd' prefix differentiates them)
        switch (interaction)
        {
            case ButtonInteraction button:
                await HandleButton(button);
                break;
            case StringMenuInteraction stringMenu:
                await HandleDropdown(stringMenu);
                break;
        }
    }

    async ValueTask HandleButton(ButtonInteraction button)
    {
        Console.WriteLine($"Menu button interaction!\n" +
                          $"cId = {button.Data.CustomId}\n" +
                          $"menuId = {this.id.ToString()}");

        if (!TryParseCustomId(button.Data.CustomId, out string ownerGuid, out string idPart))
            return;
        if (ownerGuid != this.id.ToString())
            return;
        if (!int.TryParse(idPart, out int btnIdx) || btnIdx < 0 || btnIdx >= buttons.Length)
            return;

        await button.SendResponseAsync(InteractionCallback.DeferredModifyMessage);
        await buttons[btnIdx].onClick.Invoke(this);
    }

    async ValueTask HandleDropdown(StringMenuInteraction stringMenu)
    {
        Console.WriteLine($"Menu dropdown interaction!\n" +
                          $"cId = {stringMenu.Data.CustomId}\n" +
                          $"menuId = {this.id.ToString()}");

        if (!TryParseCustomId(stringMenu.Data.CustomId, out string ownerGuid, out string idPart))
            return;
        if (ownerGuid != this.id.ToString())
            return;
        if (idPart.Length < 2 || idPart[0] != 'd')
            return;
        if (!int.TryParse(idPart.AsSpan(1), out int ddIdx) || ddIdx < 0 || ddIdx >= dropdowns.Length)
            return;

        IReadOnlyList<string> selected = stringMenu.Data.SelectedValues;
        dropdownSelections[ddIdx] = selected;

        await stringMenu.SendResponseAsync(InteractionCallback.DeferredModifyMessage);

        DropdownHandler? handler = dropdowns[ddIdx].onSelect;
        if (handler != null)
            await handler.Invoke(this, selected);
    }

    static bool TryParseCustomId(string customId, out string guid, out string idPart)
    {
        guid = "";
        idPart = "";
        int slash = customId.IndexOf('/');
        if (slash < 0) return false;
        guid = customId.Substring(0, slash);
        idPart = customId.Substring(slash + 1);
        return true;
    }

    List<IMessageComponentProperties> GetComponents()
    {
        List<IMessageComponentProperties> components = [];

        // String-select menus are top-level components (NetCord serializes them into
        // their own ActionRow as Discord requires). Don't wrap manually.
        for (int i = 0; i < dropdowns.Length; i++)
        {
            Dropdown d = dropdowns[i];
            StringMenuSelectOptionProperties[] opts = new StringMenuSelectOptionProperties[d.options.Length];
            for (int o = 0; o < d.options.Length; o++)
            {
                DropdownOption opt = d.options[o];
                opts[o] = new StringMenuSelectOptionProperties(opt.label, opt.value);
                if (opt.description != null)
                    opts[o].Description = opt.description;
            }

            StringMenuProperties menu = new StringMenuProperties($"{id}/d{i}", opts)
                .WithPlaceholder(d.placeholder)
                .WithMinValues(d.minValues)
                .WithMaxValues(d.maxValues);

            components.Add(menu);
        }

        // Buttons need explicit ActionRow wrapping, packed into rows of 5.
        for (int row = 0; row < buttons.Length; row += 5)
        {
            ActionRowProperties actionRow = [];
            components.Add(actionRow);

            for (int b = row; b < row + 5 && b < buttons.Length; b++)
                actionRow.Add(new ButtonProperties($"{id}/{b}", buttons[b].label, buttons[b].style).WithDisabled(buttons[b].disabled));
        }

        return components;
    }

    // ---- Open factories ---------------------------------------------------
    // Existing button-only signatures (unchanged callsites).
    public static Task<GenericMenu> Open(ApplicationCommandContext context, Action<MessageProperties> decorator, params Button[] buttons)
        => OpenInternal(decorator, [], buttons, context.Client, null, m => m.SendInitialMessage(context.Channel));
    public static Task<GenericMenu> Open(RestClient rest, GatewayClient gateway, ulong channelId, Action<MessageProperties> decorator, params Button[] buttons)
        => OpenInternal(decorator, [], buttons, gateway, null, m => m.SendInitialMessage(channelId, rest));
    public static Task<GenericMenu> Open(ApplicationCommandContext context, Action<MessageProperties> decorator, int? timeout, params Button[] buttons)
        => OpenInternal(decorator, [], buttons, context.Client, timeout, m => m.SendInitialMessage(context.Channel));
    public static Task<GenericMenu> Open(RestClient rest, GatewayClient gateway, ulong channelId, Action<MessageProperties> decorator, int? timeout, params Button[] buttons)
        => OpenInternal(decorator, [], buttons, gateway, timeout, m => m.SendInitialMessage(channelId, rest));

    // New: dropdowns + buttons. The `int? timeout` slot is required (pass null for
    // no timeout) so this overload is unambiguous against the button-only ones.
    public static Task<GenericMenu> Open(ApplicationCommandContext context, Action<MessageProperties> decorator, int? timeout, Dropdown[] dropdowns, params Button[] buttons)
        => OpenInternal(decorator, dropdowns, buttons, context.Client, timeout, m => m.SendInitialMessage(context.Channel));
    public static Task<GenericMenu> Open(RestClient rest, GatewayClient gateway, ulong channelId, Action<MessageProperties> decorator, int? timeout, Dropdown[] dropdowns, params Button[] buttons)
        => OpenInternal(decorator, dropdowns, buttons, gateway, timeout, m => m.SendInitialMessage(channelId, rest));

    static async Task<GenericMenu> OpenInternal(
        Action<MessageProperties> decorator,
        Dropdown[] dropdowns,
        Button[] buttons,
        GatewayClient gateway,
        int? timeout,
        Func<GenericMenu, ValueTask> send)
    {
        Guid id = Guid.NewGuid();
        GenericMenu menu = new(id, decorator, dropdowns, buttons, gateway, timeout);
        menu.RegisterInteractionHandler();
        await send.Invoke(menu);
        return menu;
    }

    public static Task<GenericMenu> OpenConfirm(ApplicationCommandContext context,
                                                Action<MessageProperties> decorator,
                                                ButtonHandler onYes,
                                                ButtonHandler onNo,
                                                string yesText = "Yes",
                                                string noText = "No",
                                                int? timeout = null)
    {
        return Open(context, decorator, timeout, new Button(ButtonStyle.Success, yesText, onYes), new Button(ButtonStyle.Danger, noText, onNo));
    }
    public static Task<GenericMenu> OpenConfirm(RestClient rest,
                                                GatewayClient gateway,
                                                ulong channelId,
                                                Action<MessageProperties> decorator,
                                                ButtonHandler onYes,
                                                ButtonHandler onNo,
                                                string yesText = "Yes",
                                                string noText = "No",
                                                int? timeout = null)
    {
        return Open(rest, gateway, channelId, decorator, timeout, new Button(ButtonStyle.Success, yesText, onYes), new Button(ButtonStyle.Danger, noText, onNo));
    }
}

public struct Button(ButtonStyle style, string label, ButtonHandler onClick, bool disabled = false)
{
    public readonly ButtonStyle style = style;
    public readonly string label = label;
    public readonly ButtonHandler onClick = onClick;
    public readonly bool disabled = disabled;
}

public struct Dropdown(string placeholder, DropdownOption[] options, DropdownHandler? onSelect = null, int minValues = 1, int maxValues = 1)
{
    public readonly string placeholder = placeholder;
    public readonly DropdownOption[] options = options;
    public readonly DropdownHandler? onSelect = onSelect;
    public readonly int minValues = minValues;
    public readonly int maxValues = maxValues;
}

public struct DropdownOption(string label, string value, string? description = null)
{
    public readonly string label = label;
    public readonly string value = value;
    public readonly string? description = description;
}
