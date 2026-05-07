using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace JMTech.Shared.NetCord;

public delegate ValueTask ButtonHandler(GenericMenu menu);
public delegate ValueTask DropdownHandler(GenericMenu menu, IReadOnlyList<string> selected);

// ---- Runtime menu ---------------------------------------------------------

public class GenericMenu
{
    readonly Guid id;
    readonly Action<MessageProperties> decorator;
    readonly Row[] rows;
    readonly GatewayClient gateway;
    readonly int? timeout;

    // Latest selection per dropdown row (keyed by row index — only dropdown rows ever appear here).
    readonly Dictionary<int, IReadOnlyList<string>> dropdownSelections = new();

    RestMessage? message;

    internal GenericMenu(Guid id, Action<MessageProperties> decorator, Row[] rows, GatewayClient gateway, int? timeout)
    {
        this.id = id;
        this.decorator = decorator;
        this.rows = rows;
        this.gateway = gateway;
        this.timeout = timeout;
    }

    /// <summary>Latest values selected on the Nth dropdown (0-based across dropdowns, in row order). Empty if no selection yet.</summary>
    public IReadOnlyList<string> GetSelected(int dropdownIndex)
    {
        int found = -1;
        for (int r = 0; r < rows.Length; r++)
        {
            if (rows[r] is not DropdownRow) continue;
            if (++found == dropdownIndex)
                return dropdownSelections.TryGetValue(r, out IReadOnlyList<string>? v) ? v : Array.Empty<string>();
        }
        return Array.Empty<string>();
    }

    /// <summary>First selected value for the Nth dropdown, or null.</summary>
    public string? GetSelectedSingle(int dropdownIndex)
    {
        IReadOnlyList<string> sel = GetSelected(dropdownIndex);
        return sel.Count > 0 ? sel[0] : null;
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

    internal async ValueTask Send(Func<MessageProperties, ValueTask<RestMessage>> send)
    {
        MessageProperties msg = new();
        decorator.Invoke(msg);
        InternalDecorate(msg);
        msg.WithComponents(GetComponents());
        message = await send.Invoke(msg);

        if (timeout is not { } timeoutVal)
            return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(timeoutVal * 1000);
            await Close();
        });
    }

    internal void RegisterInteractionHandler() => gateway.InteractionCreate += OnInteractionCreate;
    void DeregisterInteractionHandler() => gateway.InteractionCreate -= OnInteractionCreate;

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

    // Custom-id scheme:
    //   button:   "{guid}/{rowIdx}.{positionInRow}"
    //   dropdown: "{guid}/{rowIdx}"   (no dot)
    async ValueTask OnInteractionCreate(Interaction interaction)
    {
        switch (interaction)
        {
            case ButtonInteraction button: await HandleButton(button); break;
            case StringMenuInteraction stringMenu: await HandleDropdown(stringMenu); break;
        }
    }

    async ValueTask HandleButton(ButtonInteraction button)
    {
        if (!TryParseId(button.Data.CustomId, out string ownerGuid, out string idPart) || ownerGuid != id.ToString())
            return;

        int dot = idPart.IndexOf('.');
        if (dot < 0) return;
        if (!int.TryParse(idPart.AsSpan(0, dot), out int rowIdx)) return;
        if (!int.TryParse(idPart.AsSpan(dot + 1), out int pos)) return;
        if (rowIdx < 0 || rowIdx >= rows.Length) return;
        if (rows[rowIdx] is not ButtonRow row) return;
        if (pos < 0 || pos >= row.Buttons.Length) return;

        await button.SendResponseAsync(InteractionCallback.DeferredModifyMessage);
        await row.Buttons[pos].onClick.Invoke(this);
    }

    async ValueTask HandleDropdown(StringMenuInteraction stringMenu)
    {
        if (!TryParseId(stringMenu.Data.CustomId, out string ownerGuid, out string idPart) || ownerGuid != id.ToString())
            return;
        if (idPart.Contains('.')) return; // button shape, ignore
        if (!int.TryParse(idPart, out int rowIdx)) return;
        if (rowIdx < 0 || rowIdx >= rows.Length) return;
        if (rows[rowIdx] is not DropdownRow row) return;

        IReadOnlyList<string> selected = stringMenu.Data.SelectedValues;
        dropdownSelections[rowIdx] = selected;

        await stringMenu.SendResponseAsync(InteractionCallback.DeferredModifyMessage);

        if (row.Dropdown.onSelect != null)
            await row.Dropdown.onSelect.Invoke(this, selected);
    }

    static bool TryParseId(string customId, out string guid, out string idPart)
    {
        guid = ""; idPart = "";
        int slash = customId.IndexOf('/');
        if (slash < 0) return false;
        guid = customId.Substring(0, slash);
        idPart = customId.Substring(slash + 1);
        return true;
    }

    List<IMessageComponentProperties> GetComponents()
    {
        List<IMessageComponentProperties> components = [];

        for (int r = 0; r < rows.Length; r++)
        {
            switch (rows[r])
            {
                case ButtonRow buttonRow:
                    ActionRowProperties row = [];
                    for (int p = 0; p < buttonRow.Buttons.Length; p++)
                    {
                        Button b = buttonRow.Buttons[p];
                        row.Add(new ButtonProperties($"{id}/{r}.{p}", b.label, b.style).WithDisabled(b.disabled));
                    }
                    components.Add(row);
                    break;

                case DropdownRow dropdownRow:
                    Dropdown d = dropdownRow.Dropdown;
                    StringMenuSelectOptionProperties[] opts = new StringMenuSelectOptionProperties[d.options.Length];
                    for (int o = 0; o < d.options.Length; o++)
                    {
                        DropdownOption opt = d.options[o];
                        opts[o] = new StringMenuSelectOptionProperties(opt.label, opt.value);
                        if (opt.description != null) opts[o].Description = opt.description;
                    }
                    StringMenuProperties menu = new StringMenuProperties($"{id}/{r}", opts)
                        .WithPlaceholder(d.placeholder)
                        .WithMinValues(d.minValues)
                        .WithMaxValues(d.maxValues);
                    components.Add(menu);
                    break;
            }
        }

        return components;
    }

    // ---- Internal row spec types --------------------------------------
    internal abstract class Row { }
    internal class ButtonRow : Row { public required Button[] Buttons; }
    internal class DropdownRow : Row { public required Dropdown Dropdown; }
}

// ---- Builders -------------------------------------------------------------

public class GenericMenuBuilder
{
    readonly ApplicationCommandContext? context;
    readonly RestClient? rest;
    readonly GatewayClient gateway;
    readonly ulong? channelId;

    Action<MessageProperties> decorator = _ => { };
    int? timeout;
    readonly List<GenericMenu.Row> rows = new();

    public GenericMenuBuilder(ApplicationCommandContext context)
    {
        this.context = context;
        this.gateway = context.Client;
    }

    public GenericMenuBuilder(RestClient rest, GatewayClient gateway, ulong channelId)
    {
        this.rest = rest;
        this.gateway = gateway;
        this.channelId = channelId;
    }

    public GenericMenuBuilder Message(string text)
    {
        decorator = msg => msg.WithContent(text);
        return this;
    }

    public GenericMenuBuilder Message(Action<MessageProperties> apply)
    {
        decorator = apply;
        return this;
    }

    public GenericMenuBuilder Timeout(int seconds) { timeout = seconds; return this; }

    public ActionRowBuilder ActionRow() => new(this);

    public async Task<GenericMenu> Build()
    {
        if (rows.Count == 0)
            throw new InvalidOperationException("GenericMenuBuilder.Build called with no action rows.");

        Guid id = Guid.NewGuid();
        GenericMenu menu = new(id, decorator, rows.ToArray(), gateway, timeout);
        menu.RegisterInteractionHandler();

        if (context != null)
            await menu.Send(async msg => await context.Channel.SendMessageAsync(msg));
        else
            await menu.Send(async msg => await rest!.SendMessageAsync(channelId!.Value, msg));

        return menu;
    }

    internal void AddRow(GenericMenu.Row row) => rows.Add(row);
}

public class ActionRowBuilder
{
    readonly GenericMenuBuilder parent;
    readonly List<Button> buttons = new();
    Dropdown? dropdown;

    internal ActionRowBuilder(GenericMenuBuilder parent) { this.parent = parent; }

    // ---- Buttons --------------------------------------------------------

    public ActionRowBuilder Button(string label, ButtonHandler onClick, ButtonStyle style = ButtonStyle.Primary, bool disabled = false)
        => Button(new Button(style, label, onClick, disabled));

    public ActionRowBuilder Button(Button button)
    {
        if (dropdown.HasValue) throw new InvalidOperationException("ActionRow already has a dropdown — buttons can't be mixed in.");
        if (buttons.Count >= 5) throw new InvalidOperationException("ActionRow can hold at most 5 buttons.");
        buttons.Add(button);
        return this;
    }

    public ActionRowBuilder Buttons(params Button[] btns) => Buttons((IEnumerable<Button>)btns);

    public ActionRowBuilder Buttons(IEnumerable<Button> btns)
    {
        foreach (Button b in btns) Button(b);
        return this;
    }

    // ---- Dropdown -------------------------------------------------------

    public ActionRowBuilder Dropdown(string placeholder, params DropdownOption[] options)
        => Dropdown(new Dropdown(placeholder, options));

    public ActionRowBuilder Dropdown(string placeholder, IEnumerable<DropdownOption> options)
        => Dropdown(new Dropdown(placeholder, options.ToArray()));

    public ActionRowBuilder Dropdown(Dropdown d)
    {
        if (buttons.Count > 0) throw new InvalidOperationException("ActionRow already has buttons — a dropdown can't be added.");
        if (dropdown.HasValue) throw new InvalidOperationException("ActionRow already has a dropdown.");
        dropdown = d;
        return this;
    }

    /// <summary>Attach a select-changed handler to the dropdown that was just added in this row.</summary>
    public ActionRowBuilder OnSelect(DropdownHandler handler)
    {
        if (!dropdown.HasValue) throw new InvalidOperationException("Add a Dropdown before OnSelect.");
        Dropdown d = dropdown.Value;
        dropdown = new Dropdown(d.placeholder, d.options, handler, d.minValues, d.maxValues);
        return this;
    }

    /// <summary>Finishes this row and returns to the menu builder.</summary>
    public GenericMenuBuilder Close()
    {
        if (dropdown.HasValue)
            parent.AddRow(new GenericMenu.DropdownRow { Dropdown = dropdown.Value });
        else if (buttons.Count > 0)
            parent.AddRow(new GenericMenu.ButtonRow { Buttons = buttons.ToArray() });
        else
            throw new InvalidOperationException("ActionRow is empty — add at least one button or a dropdown before closing.");
        return parent;
    }

    /// <summary>Closes this row and starts a new one (shortcut for Close().ActionRow()).</summary>
    public ActionRowBuilder ActionRow() => Close().ActionRow();

    /// <summary>Closes this row and builds the menu (shortcut for Close().Build()).</summary>
    public Task<GenericMenu> Build() => Close().Build();
}

// ---- Component value types ------------------------------------------------

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
