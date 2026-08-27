namespace PlcSoftware.Core.Models;

/// <summary>
/// The write semantics of a host command (design §4.4).
/// </summary>
public enum CommandKind
{
    /// <summary>~200 ms set-then-clear pulse (M100-M103 脉冲).</summary>
    Pulse,

    /// <summary>Maintained write (M104/M105 模式, M110/M111 屏蔽 保持写入).</summary>
    Holding,

    /// <summary>
    /// Press-and-hold (M106-M109 点动). <see cref="ICommandService.ExecuteAsync"/> writes the coil true
    /// and returns; the caller must release it later via <see cref="ICommandService.ReleaseJogCommandsAsync"/>
    /// (on mouse-up, page switch, window blur or app exit — design §6.4), with the PLC watchdog
    /// (D106, design §5.2) as the offline fallback.
    /// </summary>
    Jog,
}

/// <summary>
/// The named host command surface (design §4.4). Each target maps to a fixed M address and a fixed
/// <see cref="CommandKind"/>, resolved by <c>CommandService</c>; the numeric M address is kept in the
/// protocol-address byte/coil space (M100 → protocol address 100 … M111 → 111).
/// </summary>
public enum CommandTarget
{
    /// <summary>M100 上位机急停请求 (software e-stop request).</summary>
    EStopRequest,

    /// <summary>M101 上位机启动.</summary>
    Start,

    /// <summary>M102 上位机停止.</summary>
    Stop,

    /// <summary>M103 上位机复位.</summary>
    Reset,

    /// <summary>M104 自动模式 (holding; mutually exclusive with <see cref="BypassMode"/>).</summary>
    AutoMode,

    /// <summary>M105 直通模式 (holding; mutually exclusive with <see cref="AutoMode"/>).</summary>
    BypassMode,

    /// <summary>M106 手动调宽+ (jog).</summary>
    ManualWidthPlus,

    /// <summary>M107 手动调宽- (jog).</summary>
    ManualWidthMinus,

    /// <summary>M108 手动皮带点动 (jog).</summary>
    ManualBeltJog,

    /// <summary>M109 手动挡停 (jog).</summary>
    ManualStopper,

    /// <summary>M110 光栅屏蔽 (holding).</summary>
    LightCurtainBypass,

    /// <summary>M111 门磁屏蔽 (holding).</summary>
    DoorBypass,
}

/// <summary>
/// An immutable host command for execution. For <see cref="CommandKind.Pulse"/> the <see cref="Value"/>
/// is ignored (the pulse always sets true then clears); for <see cref="CommandKind.Holding"/> and
/// <see cref="CommandKind.Jog"/> it is the value written to the coil.
/// </summary>
public sealed record CommandRequest(CommandTarget Target, bool Value = true);
