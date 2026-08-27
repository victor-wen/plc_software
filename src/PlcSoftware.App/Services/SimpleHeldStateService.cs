using PlcSoftware.Core.Models;

namespace PlcSoftware.App.Services;

/// <summary>
/// App-layer tracker of the HMI's own held (保持写入) mask command state — the source of truth for the
/// 屏蔽 (bypass) status flags shown by the UI. M110/M111 are holding <em>commands</em>, not PLC feedback
/// points: the design's fast-block register map (D100=M0-M15, D102=M200-M215, D103=M30-M45, D104=M300-M315,
/// D105.bit0=M316) has no slot for them, so they can never appear in a decoded <c>DeviceSnapshot</c>. The
/// mask state therefore cannot come from a snapshot read; it is derived from the HMI's own last
/// successfully-written M110/M111 value (design §4.4: 保持写入，持续确认并审计), which is what this service
/// holds and re-publishes.
///
/// <para>Only the two mask targets are owned here. Recording is idempotent and the change event is raised
/// only when a flag actually flips, so a redundant write does not spam the UI. The tracker is guarded by a
/// tiny lock: writes come from the command path and reads from the UI wiring, so they are never allowed to
/// race.</para>
/// </summary>
internal sealed class SimpleHeldStateService
{
    private readonly object _sync = new();
    private bool _lightCurtainBypass;
    private bool _doorBypass;

    /// <summary>True when the HMI last held a successful M110 光栅屏蔽 write.</summary>
    public bool LightCurtainBypass
    {
        get { lock (_sync) { return _lightCurtainBypass; } }
    }

    /// <summary>True when the HMI last held a successful M111 门磁屏蔽 write.</summary>
    public bool DoorBypass
    {
        get { lock (_sync) { return _doorBypass; } }
    }

    /// <summary>Raised after <see cref="LightCurtainBypass"/> or <see cref="DoorBypass"/> changes.</summary>
    public event Action? MaskStateChanged;

    /// <summary>
    /// Records the outcome of a mask command (<see cref="CommandTarget.LightCurtainBypass"/> /
    /// <see cref="CommandTarget.DoorBypass"/>). Non-mask targets are ignored. Raises
    /// <see cref="MaskStateChanged"/> once if either flag changed.
    /// </summary>
    public void Record(CommandTarget target, bool value)
    {
        var changed = false;
        lock (_sync)
        {
            switch (target)
            {
                case CommandTarget.LightCurtainBypass:
                    if (_lightCurtainBypass != value)
                    {
                        _lightCurtainBypass = value;
                        changed = true;
                    }
                    break;

                case CommandTarget.DoorBypass:
                    if (_doorBypass != value)
                    {
                        _doorBypass = value;
                        changed = true;
                    }
                    break;

                default:
                    // Not a mask target; the tracker only owns M110/M111.
                    return;
            }
        }

        if (changed)
        {
            MaskStateChanged?.Invoke();
        }
    }
}
