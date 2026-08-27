namespace PlcSoftware.Core.Configuration;

using PlcSoftware.Core.Models;

/// <summary>
/// The configurable HMI screen model (design §7: 模块化可配置界面). A <see cref="UiLayoutDefinition"/> is
/// loaded from <c>config/ui-layout.json</c> and drives the whole operator interface: the global shell
/// (title bar, sign-in rule), the ordered page list and, per page, a list of <em>modules</em> that are
/// placed into fixed regions (header / left menu / right nav / content / bottom command bar) by the
/// renderer:
///
/// <list type="bullet">
///   <item><c>header</c> — the page title bar (title + logo).</item>
///   <item><c>menu</c> — the left vertical button group (mode switches, page shortcuts).</item>
///   <item><c>nav</c> — the right vertical navigation group (next/prev/back + page shortcuts).</item>
///   <item><c>commandBar</c> — the bottom horizontal command row (启动/停止/复位/急停…).</item>
///   <item><c>loginForm</c> — the sign-in form (username / password / confirm).</item>
///   <item><c>parameterGroup</c> — one or more 位置参数 tables (axis rows × position/speed fields).</item>
///   <item><c>pageHost</c> — hosts the existing (legacy XAML) page view by name.</item>
/// </list>
///
/// <para><b>Actions.</b> Every button carries a <see cref="UiActionDefinition"/>: <c>navigate</c> switches
/// to another page, <c>command</c> sends one or more host-command writes
/// (<see cref="CommandTarget"/> + value), <c>login</c> jumps to the sign-in page, <c>up</c>/<c>down</c>/
/// <c>back</c> walk the page list and <c>none</c> is inert.</para>
///
/// <para><b>WPF-free.</b> The model and its validation live in Core so the JSON contract is unit-tested on
/// the Linux CI runner; only the renderer (App layer) touches WPF.</para>
/// </summary>
public sealed class UiLayoutDefinition
{
    /// <summary>Global shell settings (title, logo, default page, sign-in rule).</summary>
    public UiAppDefinition App { get; set; } = new();

    /// <summary>The ordered page list. The first page is the default when <see cref="UiAppDefinition.DefaultPage"/>
    /// is not set.</summary>
    public List<UiPageDefinition> Pages { get; set; } = new();

    /// <summary>Returns the page with the given id, or null.</summary>
    public UiPageDefinition? FindPage(string? pageId)
        => Pages.FirstOrDefault(p => string.Equals(p.Id, pageId, StringComparison.Ordinal));

    /// <summary>The default page (explicit <see cref="UiAppDefinition.DefaultPage"/>, falling back to the first page).</summary>
    public UiPageDefinition DefaultPage => FindPage(App.DefaultPage) ?? Pages.FirstOrDefault()!;

    /// <summary>Validates the layout and returns a list of human-readable errors (empty = valid).</summary>
    public List<string> Validate()
    {
        var errors = new List<string>();

        if (Pages.Count == 0)
        {
            errors.Add("ui-layout: at least one page is required.");
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in Pages)
        {
            if (string.IsNullOrWhiteSpace(page.Id))
            {
                errors.Add("ui-layout: a page has an empty id.");
                continue;
            }

            if (!seenIds.Add(page.Id))
            {
                errors.Add($"ui-layout: duplicate page id '{page.Id}'.");
            }

            errors.AddRange(page.Validate());
        }

        if (App.DefaultPage is not null && FindPage(App.DefaultPage) is null)
        {
            errors.Add($"ui-layout: default page '{App.DefaultPage}' does not exist.");
        }

        if (App.LoginSuccess is not null)
        {
            errors.AddRange(ValidateActionReference(App.LoginSuccess, "app.loginSuccess"));
        }

        // Navigate targets are checked against the full page list (page-level validation cannot see it).
        foreach (var page in Pages)
        {
            foreach (var module in page.Modules)
            {
                foreach (var item in module.Buttons)
                {
                    if (item.Action is not null)
                    {
                        errors.AddRange(ValidateActionReference(
                            item.Action, $"page '{page.Id}' module '{module.Type}' button '{item.Text}'"));
                    }
                }
            }
        }

        return errors;
    }

    /// <summary>Validates an action's page references against the page list (shared by page and app level).</summary>
    internal List<string> ValidateActionReference(UiActionDefinition action, string path)
    {
        var errors = new List<string>();

        if (action.Kind == UiActionKind.Navigate && FindPage(action.Page) is null)
        {
            errors.Add($"ui-layout: {path} navigates to unknown page '{action.Page}'.");
        }

        if (action.Kind == UiActionKind.Command)
        {
            if (action.Writes.Count == 0)
            {
                errors.Add($"ui-layout: {path} is a command action with no writes.");
            }

            foreach (var write in action.Writes)
            {
                if (write.ResolveTarget() is null)
                {
                    errors.Add($"ui-layout: {path} references unknown command target '{write.Target}'.");
                }
            }
        }

        return errors;
    }
}

/// <summary>Global shell settings.</summary>
public sealed class UiAppDefinition
{
    /// <summary>The window/page title (e.g. 自动化设备). Defaults to the app title.</summary>
    public string Title { get; set; } = "PLC 上位机监控系统";

    /// <summary>The logo text shown in the corner (e.g. VISA).</summary>
    public string Logo { get; set; } = string.Empty;

    /// <summary>The id of the page shown at startup; the first page is used when unset.</summary>
    public string? DefaultPage { get; set; }

    /// <summary>True when every page except the sign-in page requires a successful sign-in
    /// (navigation to another page is redirected to the sign-in page while signed out).
    /// Defaults to true when <see cref="Users"/> is non-empty, false otherwise.</summary>
    public bool? RequireLogin { get; set; }

    /// <summary>The optional sign-in credentials. Empty = the login form accepts anything (demo/simulation).</summary>
    public List<UiUserDefinition> Users { get; set; } = new();

    /// <summary>The action run after a successful sign-in (typically <c>navigate</c>).</summary>
    public UiActionDefinition? LoginSuccess { get; set; }

    /// <summary>Effective sign-in gate: <see cref="RequireLogin"/> or, when unset, "any users configured".</summary>
    public bool LoginRequired => RequireLogin ?? Users.Count > 0;
}

/// <summary>One allowed sign-in credential.</summary>
public sealed class UiUserDefinition
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

/// <summary>One configurable screen. Modules are rendered into the five fixed regions of the shell.</summary>
public sealed class UiPageDefinition
{
    /// <summary>Unique page id (used by navigate actions and the nav list).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The human-readable page title (defaults to <see cref="Id"/>).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The modules of this page, rendered in declaration order.</summary>
    public List<UiModuleDefinition> Modules { get; set; } = new();

    internal List<string> Validate()
    {
        var errors = new List<string>();
        var typeCounts = new Dictionary<UiModuleType, int>();

        foreach (var module in Modules)
        {
            errors.AddRange(module.Validate());
            typeCounts[module.Type] = typeCounts.GetValueOrDefault(module.Type) + 1;
        }

        // Exactly one of each singleton shell region per page (header/menu/nav/commandBar/loginForm).
        foreach (var singleton in new[] { UiModuleType.Header, UiModuleType.LoginForm })
        {
            if (typeCounts.GetValueOrDefault(singleton) > 1)
            {
                errors.Add($"ui-layout: page '{Id}' declares more than one {singleton} module.");
            }
        }

        return errors;
    }
}

/// <summary>One UI module instance (a discriminated union by <see cref="Type"/>).</summary>
public sealed class UiModuleDefinition
{
    /// <summary>The module kind; fields meaningful for other kinds are ignored by the renderer.</summary>
    public UiModuleType Type { get; set; }

    /// <summary>header: the title text (falls back to <c>app.title</c>).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>header: the logo text (falls back to <c>app.logo</c>).</summary>
    public string Logo { get; set; } = string.Empty;

    /// <summary>menu/nav/commandBar: the buttons of the group.</summary>
    public List<UiActionButtonDefinition> Buttons { get; set; } = new();

    /// <summary>parameterGroup: the tables shown in the content region.</summary>
    public List<UiParameterGroupDefinition> Groups { get; set; } = new();

    /// <summary>dashboard: the tiles of the board (home page 磁贴看板).</summary>
    public List<UiTileDefinition> Tiles { get; set; } = new();

    /// <summary>pageHost: the name of the legacy page view to host (e.g. OverviewView).</summary>
    public string HostedView { get; set; } = string.Empty;

    internal List<string> Validate()
    {
        var errors = new List<string>();

        switch (Type)
        {
            case UiModuleType.Header:
                break;
            case UiModuleType.Menu:
            case UiModuleType.Nav:
            case UiModuleType.CommandBar:
                if (Buttons.Count == 0)
                {
                    errors.Add($"ui-layout: a {Type} module must declare at least one button.");
                }

                foreach (var button in Buttons)
                {
                    if (string.IsNullOrWhiteSpace(button.Text))
                    {
                        errors.Add($"ui-layout: a {Type} module has a button with an empty text.");
                    }
                }

                break;
            case UiModuleType.LoginForm:
                break;
            case UiModuleType.ParameterGroup:
                if (Groups.Count == 0)
                {
                    errors.Add("ui-layout: a parameterGroup module must declare at least one group.");
                }

                foreach (var group in Groups)
                {
                    errors.AddRange(group.Validate());
                }

                break;
            case UiModuleType.Dashboard:
                if (Tiles.Count == 0)
                {
                    errors.Add("ui-layout: a dashboard module must declare at least one tile.");
                }

                var tileIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var tile in Tiles)
                {
                    errors.AddRange(tile.Validate());
                    if (!tileIds.Add(tile.Id))
                    {
                        errors.Add($"ui-layout: duplicate tile id '{tile.Id}'.");
                    }
                }

                break;
            case UiModuleType.PageHost:
                if (string.IsNullOrWhiteSpace(HostedView))
                {
                    errors.Add("ui-layout: a pageHost module must declare the hosted view name.");
                }

                break;
            default:
                errors.Add($"ui-layout: unknown module type '{Type}'.");
                break;
        }

        return errors;
    }
}

/// <summary>The module kinds understood by the renderer.</summary>
public enum UiModuleType
{
    /// <summary>Title bar (title + logo).</summary>
    Header,

    /// <summary>Left vertical button group.</summary>
    Menu,

    /// <summary>Right vertical navigation group.</summary>
    Nav,

    /// <summary>Bottom horizontal command row.</summary>
    CommandBar,

    /// <summary>Sign-in form (username / password / confirm).</summary>
    LoginForm,

    /// <summary>位置参数 tables (axis rows × position/speed fields).</summary>
    ParameterGroup,

    /// <summary>Home dashboard (磁贴看板): a tile grid of buttons / live status / clock / text tiles
    /// that the operator can edit (size, order, content) at runtime.</summary>
    Dashboard,

    /// <summary>Hosts a legacy (hand-written XAML) page view by name.</summary>
    PageHost,
}

/// <summary>One button in a menu/nav/commandBar module.</summary>
public sealed class UiActionButtonDefinition
{
    /// <summary>The button caption (e.g. 手动模式).</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>The action performed on click; null = inert.</summary>
    public UiActionDefinition? Action { get; set; }
}

/// <summary>The performed action of a button / login success.</summary>
public sealed class UiActionDefinition
{
    /// <summary>The action kind.</summary>
    public UiActionKind Kind { get; set; } = UiActionKind.None;

    /// <summary>navigate: the target page id.</summary>
    public string? Page { get; set; }

    /// <summary>command: the host-command writes to send (each target + value), in order. Multiple writes
    /// compose e.g. the mutually exclusive mode pairs (自动 M104=1, M105=0).</summary>
    public List<UiWriteDefinition> Writes { get; set; } = new();
}

/// <summary>The action kinds.</summary>
public enum UiActionKind
{
    /// <summary>Inert (no-op).</summary>
    None,

    /// <summary>Switch to <see cref="UiActionDefinition.Page"/>.</summary>
    Navigate,

    /// <summary>Send <see cref="UiActionDefinition.Writes"/> through <c>ICommandService</c>.</summary>
    Command,

    /// <summary>Switch to the sign-in page (the page whose module list contains loginForm).</summary>
    Login,

    /// <summary>Sign out (clears the sign-in state of the shell).</summary>
    Logout,

    /// <summary>Move to the previous page of the page list.</summary>
    Up,

    /// <summary>Move to the next page of the page list.</summary>
    Down,

    /// <summary>Return to the previous visited page.</summary>
    Back,
}

/// <summary>One host-command write of a command action.</summary>
public sealed class UiWriteDefinition
{
    /// <summary>The command target name (CommandTarget enum member, e.g. <c>AutoMode</c>, <c>Start</c>, <c>EStopRequest</c>).</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>The value written (holding writes; pulses ignore it).</summary>
    public bool Value { get; set; } = true;

    /// <summary>Resolves <see cref="Target"/> to a <see cref="CommandTarget"/>, or null when the name is invalid.</summary>
    public CommandTarget? ResolveTarget()
        => Enum.TryParse<CommandTarget>(Target, ignoreCase: false, out var parsed) ? parsed : null;
}

/// <summary>One 位置参数 table (e.g. 上料道一): axis rows × position/speed fields.</summary>
public sealed class UiParameterGroupDefinition
{
    /// <summary>The table caption (e.g. 上料道一).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The rows of the table (one per axis).</summary>
    public List<UiParameterRowDefinition> Rows { get; set; } = new();

    internal List<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Title))
        {
            errors.Add("ui-layout: a parameterGroup has an empty title.");
        }

        if (Rows.Count == 0)
        {
            errors.Add($"ui-layout: parameterGroup '{Title}' declares no rows.");
        }

        foreach (var row in Rows)
        {
            errors.AddRange(row.Validate());
        }

        return errors;
    }
}

/// <summary>One axis row of a parameter table.</summary>
public sealed class UiParameterRowDefinition
{
    /// <summary>The axis caption (e.g. X轴).</summary>
    public string Axis { get; set; } = string.Empty;

    /// <summary>The position (设定) field.</summary>
    public UiParameterFieldDefinition? Position { get; set; }

    /// <summary>The speed (速度设定) field.</summary>
    public UiParameterFieldDefinition? Speed { get; set; }

    internal List<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Axis))
        {
            errors.Add("ui-layout: a parameter row has an empty axis.");
        }

        if (Position is null && Speed is null)
        {
            errors.Add($"ui-layout: parameter row '{Axis}' declares no field.");
        }

        if (Position is not null)
        {
            errors.AddRange(Position.Validate($"row '{Axis}' position"));
        }

        if (Speed is not null)
        {
            errors.AddRange(Speed.Validate($"row '{Axis}' speed"));
        }

        return errors;
    }
}

/// <summary>One writable field of an axis row (位置设定 / 速度设定).</summary>
public sealed class UiParameterFieldDefinition
{
    /// <summary>The writable parameter name (matches a <see cref="ParameterDefinition"/> name, e.g. D201).</summary>
    public string Register { get; set; } = string.Empty;

    /// <summary>The column caption (e.g. 位置设定(M)).</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>The engineering unit (e.g. mm, M/S).</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>The allowed lower bound (null = unconfigured, write refused by ParameterService).</summary>
    public int? Min { get; set; }

    /// <summary>The allowed upper bound (null = unconfigured, write refused by ParameterService).</summary>
    public int? Max { get; set; }

    internal List<string> Validate(string path)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Register))
        {
            errors.Add($"ui-layout: parameter {path} has an empty register name.");
        }

        if (Min.HasValue && Max.HasValue && Min.Value > Max.Value)
        {
            errors.Add($"ui-layout: parameter {path} ('{Register}') has Min > Max.");
        }

        return errors;
    }
}

/// <summary>
/// One tile of a dashboard module (磁贴看板, design §7): a rectangular cell of the tile grid that is
/// either a live <see cref="UiTileKind.Button"/> (runs its <see cref="Action"/>), a
/// <see cref="UiTileKind.Status"/> cell (live shell state), a <see cref="UiTileKind.Clock"/>, a
/// <see cref="UiTileKind.Text"/> annotation or a <see cref="UiTileKind.Navigate"/> shortcut.
/// <see cref="Cols"/>x<see cref="Rows"/> is measured in grid columns/rows (default 2x2, 1..4 each);
/// the operator can edit tiles at runtime (size, order and content) and the edits are persisted
/// separately from ui-layout.json (see the App-layer dashboard tile store).
/// </summary>
public sealed class UiTileDefinition
{
    /// <summary>Unique tile id (edit persistence key).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The tile kind.</summary>
    public UiTileKind Kind { get; set; } = UiTileKind.Button;

    /// <summary>button/navigate: the tile caption. text: the annotation text.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>button/navigate: the action executed on click (command writes or page navigation).</summary>
    public UiActionDefinition? Action { get; set; }

    /// <summary>status: which shell state the tile mirrors.</summary>
    public UiTileStatus? Status { get; set; }

    /// <summary>The tile width in grid columns (1..4, default 2).</summary>
    public int Cols { get; set; } = 2;

    /// <summary>The tile height in grid rows (1..4, default 2).</summary>
    public int Rows { get; set; } = 2;

    /// <summary>An optional ARGB hex color (e.g. #1E6FB8) overriding the default tile color; null = default.</summary>
    public string? Color { get; set; }

    internal List<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Id))
        {
            errors.Add("ui-layout: a dashboard tile has an empty id.");
        }

        if (Kind is UiTileKind.Button or UiTileKind.Navigate)
        {
            if (string.IsNullOrWhiteSpace(Text))
            {
                errors.Add($"ui-layout: tile '{Id}' ({Kind}) has an empty text.");
            }

            if (Action is null)
            {
                errors.Add($"ui-layout: tile '{Id}' ({Kind}) has no action.");
            }
            else if (Kind == UiTileKind.Navigate && Action.Kind != UiActionKind.Navigate)
            {
                errors.Add($"ui-layout: tile '{Id}' is a navigate tile whose action kind is '{Action.Kind}'.");
            }
        }
        else if (Kind == UiTileKind.Status && Status is null)
        {
            errors.Add($"ui-layout: tile '{Id}' is a status tile without a status kind.");
        }

        if (Cols is < 1 or > 4 || Rows is < 1 or > 4)
        {
            errors.Add($"ui-layout: tile '{Id}' size must be within 1..4 (got {Cols}x{Rows}).");
        }

        return errors;
    }
}

/// <summary>The tile kinds of a dashboard.</summary>
public enum UiTileKind
{
    /// <summary>A button tile running a command action (host-command writes).</summary>
    Button,

    /// <summary>A live shell-state cell (connection/heartbeat/mode/run/fault/mask).</summary>
    Status,

    /// <summary>The current date/time.</summary>
    Clock,

    /// <summary>A static annotation text.</summary>
    Text,

    /// <summary>A shortcut tile navigating to another page.</summary>
    Navigate,
}

/// <summary>The live shell states a status tile can mirror.</summary>
public enum UiTileStatus
{
    /// <summary>串口 link state (在线/连接中/重连中/心跳丢失/离线).</summary>
    Connection,

    /// <summary>Heartbeat state (正常/丢失).</summary>
    Heartbeat,

    /// <summary>Machine mode (手动/自动/直通/未知).</summary>
    Mode,

    /// <summary>Run state (运行/停止).</summary>
    Run,

    /// <summary>Fault text (无故障 when clear).</summary>
    Fault,

    /// <summary>Bypass mask state (已屏蔽/正常).</summary>
    Mask,
}
