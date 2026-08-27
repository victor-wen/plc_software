using CommunityToolkit.Mvvm.ComponentModel;
using PlcSoftware.Core.Models;

namespace PlcSoftware.App.ViewModels;

/// <summary>
/// One row of the read-only I/O table (design §6.6): a physical point (X / Y / M), its Chinese name and its
/// live state. The point is presentation-only — <see cref="Address"/>, <see cref="Name"/>, <see cref="Group"/>
/// and the state (<see cref="State"/>, <see cref="StateText"/>) are the only surface, so a row cannot carry a
/// force-write (Gate 7: the I/O table provides no arbitrary write; manual actions run through the manual page,
/// design §6.4).
/// </summary>
public sealed partial class IoRow : ObservableObject
{
    public IoRow(string address, string name, string group, string? snapshotKey)
    {
        Address = address ?? throw new ArgumentNullException(nameof(address));
        Name = name ?? string.Empty;
        Group = group ?? throw new ArgumentNullException(nameof(group));
        SnapshotKey = snapshotKey;
    }

    /// <summary>Logical address in original notation, e.g. "X20".</summary>
    public string Address { get; }

    /// <summary>Chinese display name, e.g. "阻挡原位".</summary>
    public string Name { get; }

    /// <summary>The point family: "X", "Y" or "M".</summary>
    public string Group { get; }

    /// <summary>The snapshot key that resolves this point's live state, or null when no mirror exists (a Y
    /// output, or an X input like X0-X3 that is not echoed onto M300+).</summary>
    public string? SnapshotKey { get; }

    /// <summary>The live state (true = 接通, false = 断开), or null when the point has not been reported yet.</summary>
    [ObservableProperty]
    private bool? _state;

    /// <summary>Human-readable state: 接通 / 断开 / 未上报 (unavailable offline).</summary>
    public string StateText => State switch
    {
        true => "接通",
        false => "断开",
        _ => "未上报",
    };

    /// <summary>True once a live state has been resolved from a snapshot.</summary>
    public bool HasValue => State.HasValue;

    partial void OnStateChanged(bool? value)
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(HasValue));
    }
}

/// <summary>
/// The I/O diagnostics page (design §6.6): the point map's X/Y/M relays are grouped and displayed read-only.
///
/// <para><b>Source.</b> The raw X/Y coil names come from the injected point map (the
/// <c>config/point-map.simulation.json</c> <c>PointDefinition</c> list loaded by
/// <c>JsonConfigurationLoader</c>); the value is matched to a decoded snapshot where a mirror exists. The
/// X inputs are matched through the M300+ echo registers of design §4.6 (X4→M300, X20→M303, X22→M316 etc.);
/// the M relays are read straight from their own snapshot key. A Y output — or an X input with no echo (X0-X3) —
/// shows 未上报 (unavailable offline) because its live value is not echoed into the polled register block
/// (the I/O diagnostic polling group of §5.1 is not wired in this milestone).</para>
///
/// <para><b>Read-only (design §6.6 + Gate 7).</b> The page exposes no write command and ignores the point map's
/// <see cref="PointDefinition.IsWritable"/> flags, so there is no force-write entry anywhere. Manual actions can
/// only be driven from the manual page (design §6.4).</para>
///
/// <para><b>No UI-thread dependency.</b> The view model consumes Core snapshots and the supervised link state
/// through <see cref="ApplySnapshot"/> / <see cref="ApplyConnectionState"/>. It never touches a
/// <c>Dispatcher</c> or any WPF type, so it stays testable under a pure unit test host (the App tests are
/// CI-only on Windows because the WindowsDesktop runtime cannot run on the WSL cross-build, not because this
/// class needs WPF).</para>
/// </summary>
public sealed partial class IoDiagnosticsViewModel : ObservableObject
{
    /// <summary>Copyright of the design §4.6 X→M echo map: each physical X input is mirrored onto an M300+
    /// relay that IS carried by the polled register block (D104 → M300-M315, D105.bit0 → M316).</summary>
    private static readonly IReadOnlyDictionary<string, string> XToMEcho = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["X4"] = "M300",   // 进板感应
        ["X5"] = "M301",   // 到位感应
        ["X6"] = "M302",   // 出板感应
        ["X7"] = "M313",   // 安全光栅
        ["X10"] = "M310",  // 限位+
        ["X11"] = "M311",  // 原位
        ["X12"] = "M312",  // 限位-
        ["X13"] = "M305",  // 拍照结束
        ["X14"] = "M306",  // 上站给板
        ["X15"] = "M307",  // 允许出站
        ["X16"] = "M314",  // 前门
        ["X17"] = "M315",  // 后门
        ["X20"] = "M303",  // 阻挡原位
        ["X21"] = "M304",  // 阻挡工作位
        ["X22"] = "M316",  // 气压检测
    };

    private readonly List<IoRow> _inputs = new();
    private readonly List<IoRow> _outputs = new();
    private readonly List<IoRow> _relays = new();

    /// <summary>The supervised link state, used only for the connection-status text (design §6.1).</summary>
    [ObservableProperty]
    private ConnectionState _connectionState;

    /// <summary>Builds the read-only I/O table over the injected point map, grouped into X / Y / M.</summary>
    public IoDiagnosticsViewModel(IEnumerable<PointDefinition> pointMap)
    {
        if (pointMap is null)
        {
            throw new ArgumentNullException(nameof(pointMap));
        }

        foreach (var point in pointMap)
        {
            if (point is null || string.IsNullOrWhiteSpace(point.Address))
            {
                continue;
            }

            var group = ResolveGroup(point.Address);
            if (group is null)
            {
                // D registers and any other non-X/Y/M address are not part of the I/O table (§6.6).
                continue;
            }

            var row = new IoRow(point.Address, point.Name, group, ResolveSnapshotKey(group, point.Address));
            switch (group)
            {
                case "X": _inputs.Add(row); break;
                case "Y": _outputs.Add(row); break;
                case "M": _relays.Add(row); break;
            }
        }

        _connectionState = ConnectionState.Disconnected;
    }

    /// <summary>The X input rows.</summary>
    public IReadOnlyList<IoRow> Inputs => _inputs;

    /// <summary>The Y output rows.</summary>
    public IReadOnlyList<IoRow> Outputs => _outputs;

    /// <summary>The M relay rows.</summary>
    public IReadOnlyList<IoRow> Relays => _relays;

    /// <summary>True only when the supervised link is <see cref="ConnectionState.Online"/>.</summary>
    public bool IsOnline => ConnectionState == ConnectionState.Online;

    /// <summary>Human-readable link text (在线 / 离线 / …) for the page header.</summary>
    public string ConnectionStatusText => ConnectionState switch
    {
        ConnectionState.Online => "在线",
        ConnectionState.Connecting => "连接中",
        ConnectionState.Reconnecting => "重连中",
        ConnectionState.HeartbeatLost => "心跳丢失",
        _ => "离线",
    };

    /// <summary>Applies an observed supervised-link state, refreshing the header and the online flag.</summary>
    public void ApplyConnectionState(ConnectionState state)
    {
        ConnectionState = state;
        OnPropertyChanged(nameof(IsOnline));
    }

    /// <summary>
    /// Applies one decoded snapshot: every row re-resolves its live state from the snapshot key. A row whose
    /// key is absent (or that has no echo key) keeps <see cref="IoRow.State"/> null → 未上报.
    /// </summary>
    public void ApplySnapshot(DeviceSnapshot snapshot)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        var values = snapshot.Values;
        foreach (var row in _inputs)
        {
            row.State = ReadBoolNullable(values, row.SnapshotKey);
        }

        foreach (var row in _outputs)
        {
            row.State = ReadBoolNullable(values, row.SnapshotKey);
        }

        foreach (var row in _relays)
        {
            row.State = ReadBoolNullable(values, row.SnapshotKey);
        }
    }

    partial void OnConnectionStateChanged(ConnectionState value) => OnPropertyChanged(nameof(ConnectionStatusText));

    /// <summary>The X / Y / M family of a logical address, or null for a D register (not part of the I/O table).</summary>
    private static string? ResolveGroup(string address)
        => address[0] switch
        {
            'X' => "X",
            'Y' => "Y",
            'M' => "M",
            _ => null,
        };

    /// <summary>The snapshot key that resolves a point's live state: an M relay reads its own address, an X
    /// input reads its M300+ echo, and a Y output has no echo (null).</summary>
    private static string? ResolveSnapshotKey(string group, string address)
        => group switch
        {
            "M" => address,
            "X" => XToMEcho.TryGetValue(address, out var echo) ? echo : null,
            _ => null,
        };

    private static bool? ReadBoolNullable(IReadOnlyDictionary<string, object?> values, string? key)
    {
        if (key is null || !values.TryGetValue(key, out var value))
        {
            return null;
        }

        return value switch
        {
            bool b => b,
            ushort u => u != 0,
            int i => i != 0,
            uint ui => ui != 0,
            short s => s != 0,
            byte b => b != 0,
            _ => null,
        };
    }
}
