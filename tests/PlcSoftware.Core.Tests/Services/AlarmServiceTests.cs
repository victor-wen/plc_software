using PlcSoftware.Core.Models;
using PlcSoftware.Core.Services;

namespace PlcSoftware.Core.Tests.Services;

/// <summary>
/// Behavioural tests for <see cref="AlarmService"/> (the K1-K7 alarm start/recovery layer fed by the
/// D110 fault code).
///
/// Verified rules:
///   - the same D110 value observed repeatedly must NOT raise a duplicate <see cref="AlarmService.AlarmStarted"/>;
///   - D110 returning to 0 closes the active alarm (raises <see cref="AlarmService.AlarmRecovered"/>);
///   - a change from one fault code to another recovers the old alarm and starts the new one;
///   - an undefined non-zero code (outside K1-K7) is a garbage sample: it raises no alarm, does NOT
///     clear the active alarm, and leaves the current state untouched; only a return to 0 closes it.
/// </summary>
public class AlarmServiceTests
{
    private readonly AlarmService _service = new(Faults());
    private readonly List<FaultDefinition> _started = new();
    private readonly List<FaultDefinition> _recovered = new();

    public AlarmServiceTests()
    {
        _service.AlarmStarted += _started.Add;
        _service.AlarmRecovered += _recovered.Add;
    }

    [Fact]
    public void Observe_SameCode_DoesNotRaiseDuplicateAlarm()
    {
        _service.Observe(3);
        _service.Observe(3);
        _service.Observe(3);

        var started = Assert.Single(_started);
        Assert.Equal(3, started.Code);
        Assert.Empty(_recovered);
    }

    [Fact]
    public void Observe_Zero_ClosesActiveAlarm()
    {
        _service.Observe(3);
        _service.Observe(0);

        var recovered = Assert.Single(_recovered);
        Assert.Equal(3, recovered.Code);
        Assert.Equal(0, _service.ActiveCode);
        Assert.Single(_started);
    }

    [Fact]
    public void Observe_CodeChangeToAnotherFault_RaisesRecoveredForOldAndStartedForNew()
    {
        _service.Observe(1);
        _service.Observe(2);

        // Changing code recovers the old alarm (code 1) and starts the new one (code 2).
        Assert.Equal(1, Assert.Single(_recovered).Code);
        Assert.Equal(new[] { 1, 2 }, _started.Select(f => f.Code));
        Assert.Equal(2, _service.ActiveCode);
    }

    [Fact]
    public void Observe_ZeroWithNoActiveFault_RaisesNoEvents()
    {
        _service.Observe(0);
        _service.Observe(0);

        Assert.Empty(_started);
        Assert.Empty(_recovered);
    }

    [Fact]
    public void Observe_UndefinedCode_RaisesNoAlarm()
    {
        _service.Observe(99); // not a K1-K7 code.

        Assert.Empty(_started);
        Assert.Empty(_recovered);
        Assert.Equal(0, _service.ActiveCode);
    }

    [Fact]
    public void Observe_FaultThenUndefinedCode_KeepsActiveAlarm_RaisesNoRecovery()
    {
        _service.Observe(3);  // started (3) — real safety alarm.
        _service.Observe(99); // undefined non-zero → a garbage sample: KEEP the active alarm.

        Assert.Empty(_recovered);          // no recovery event.
        Assert.Single(_started);           // still just the original start.
        Assert.Equal(3, _service.ActiveCode); // state unchanged.
    }

    private static IReadOnlyList<FaultDefinition> Faults() =>
        new[]
        {
            new FaultDefinition { Code = 1, Message = "急停" },
            new FaultDefinition { Code = 2, Message = "安全门打开" },
            new FaultDefinition { Code = 3, Message = "安全光栅" },
            new FaultDefinition { Code = 4, Message = "气压低" },
            new FaultDefinition { Code = 5, Message = "气缸挡停伸出超时" },
            new FaultDefinition { Code = 6, Message = "挡停未缩回" },
            new FaultDefinition { Code = 7, Message = "扫码超时" },
        };
}
