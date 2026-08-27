namespace PlcSoftware.Core.Services;

using System.Diagnostics;
using PlcSoftware.Core.Abstractions;

/// <summary>
/// Outcome of one diagnostic-terminal operation. <see cref="Value"/> is the value read or written
/// (bool for coils/discrete inputs, ushort for registers); <see cref="Hex"/> is an ASCII/hex rendering
/// of the value for the debug terminal; <see cref="Elapsed"/> is the internal time spent running the
/// command; <see cref="Error"/> is set (and <see cref="Success"/> false) on a validation, guard or
/// transport failure.
/// </summary>
public sealed record TerminalOpResult(
    bool Success,
    object? Value,
    string Hex,
    TimeSpan Elapsed,
    string? Error);

/// <summary>
/// The structured Modbus debug terminal (design §6.5): FC01/02/03/04 reads and FC05/06 single-point
/// writes, exposed as a safe, read-biased surface.
///
/// <para><b>Read-only by default.</b> Reads (coils / discrete inputs / holding registers / input registers)
/// are always permitted. Writes (coil / register) are permitted only when the terminal is <em>unlocked</em>
/// (<see cref="SetUnlocked"/>) — and auto-lock 5 minutes after the unlock was granted — and when the
/// machine is <em>not</em> running (the injected <c>isRunningProvider</c>, true = reject write; reads are
/// unaffected).</para>
///
/// <para><b>Bounds.</b> <c>slaveId</c> must be in 1..247; <c>count</c> must be in
/// <c>(0, MaxReadBits]</c> for bits or <c>(0, MaxReadRegisters]</c> for registers, and <c>address + count</c>
/// must stay inside the 16-bit Modbus address space (<see cref="ModbusLimits"/>). An invalid range returns a
/// failed <see cref="TerminalOpResult"/> — it never throws to the caller.</para>
///
/// <para><b>Audit.</b> Every command (success, validation failure, guard rejection and transport failure) is
/// recorded on the injected <see cref="IAuditLog"/> under <see cref="AuditCategory.Debug"/>. A recording
/// failure is swallowed so it never changes the command outcome.</para>
///
/// <para><b>Never throws.</b> A client/transport exception is caught and surfaces as
/// <c>Success = false</c> with the exception message in <see cref="TerminalOpResult.Error"/>.</para>
/// </summary>
public sealed class DiagnosticTerminalService
{
    private const int MaxSlaveId = 247;
    private const int MinSlaveId = 1;
    private static readonly TimeSpan UnlockDuration = TimeSpan.FromMinutes(5);

    private readonly IModbusClient _client;
    private readonly IAuditLog? _auditLog;
    private readonly Func<bool> _isRunningProvider;
    private readonly Func<DateTime> _clock;
    private readonly object _gate = new();
    private DateTime? _unlockedAt;

    /// <summary>
    /// Builds the service over the shared single-queue client. <c>clock</c> defaults to
    /// <see cref="DateTime.UtcNow"/> and drives the 5-minute auto-lock; <c>isRunningProvider</c> (default:
    /// never running) rejects writes while the machine is running; the optional <see cref="IAuditLog"/>
    /// records every debug command (design audit, category <see cref="AuditCategory.Debug"/>).
    /// </summary>
    public DiagnosticTerminalService(
        IModbusClient client,
        IAuditLog? auditLog = null,
        Func<bool>? isRunningProvider = null,
        Func<DateTime>? clock = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _auditLog = auditLog;
        _isRunningProvider = isRunningProvider ?? (() => false);
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>True while the terminal is unlocked. Auto-locks 5 minutes after the last unlock.</summary>
    public bool IsUnlocked
    {
        get
        {
            lock (_gate)
            {
                return IsUnlockedLocked();
            }
        }
    }

    /// <summary>Grants or revokes the write unlock. Auto-locks 5 minutes after being granted.</summary>
    public void SetUnlocked(bool unlocked)
    {
        lock (_gate)
        {
            _unlockedAt = unlocked ? _clock() : (DateTime?)null;
        }
    }

    /// <summary>Reads coils (FC01). Read-only: always permitted.</summary>
    public Task<TerminalOpResult> ReadCoils(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
        => RunAsync("coils", slaveId, address, count, null,
            async op => (object?)(await op.ReadCoilsAsync(slaveId, address, count, cancellationToken)));

    /// <summary>Reads discrete inputs (FC02). Read-only: always permitted.</summary>
    public Task<TerminalOpResult> ReadDiscrete(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
        => RunAsync("discrete", slaveId, address, count, null,
            async op => (object?)(await op.ReadDiscreteInputsAsync(slaveId, address, count, cancellationToken)));

    /// <summary>Reads holding registers (FC03). Read-only: always permitted.</summary>
    public Task<TerminalOpResult> ReadHolding(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
        => RunAsync("holding", slaveId, address, count, null,
            async op => (object?)(await op.ReadHoldingRegistersAsync(slaveId, address, count, cancellationToken)));

    /// <summary>Reads input registers (FC04). Read-only: always permitted.</summary>
    public Task<TerminalOpResult> ReadInput(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
        => RunAsync("input", slaveId, address, count, null,
            async op => (object?)(await op.ReadInputRegistersAsync(slaveId, address, count, cancellationToken)));

    /// <summary>Writes a single coil (FC05). Allowed only while unlocked and the machine is not running.</summary>
    public Task<TerminalOpResult> WriteCoil(byte slaveId, ushort address, bool value, CancellationToken cancellationToken)
        => RunAsync("coil-write", slaveId, address, count: null, value,
            async op => { await op.WriteSingleCoilAsync(slaveId, address, value, cancellationToken); return value; });

    /// <summary>Writes a single register (FC06). Allowed only while unlocked and the machine is not running.</summary>
    public Task<TerminalOpResult> WriteRegister(byte slaveId, ushort address, ushort value, CancellationToken cancellationToken)
        => RunAsync("register-write", slaveId, address, count: null, value,
            async op => { await op.WriteSingleRegisterAsync(slaveId, address, value, cancellationToken); return value; });

    private async Task<TerminalOpResult> RunAsync(
        string operation,
        byte slaveId,
        ushort address,
        ushort? count,
        object? value,
        Func<IModbusClient, Task<object?>> invoke)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (Validate(slaveId, address, count, operation) is string validationError)
            {
                return Complete(operation, slaveId, address, count, value, Fail(validationError), stopwatch);
            }

            if (IsWrite(operation) && !CanWrite())
            {
                return Complete(operation, slaveId, address, count, value,
                    Fail(Unlocked() ? "write denied: the machine is running." : "write denied: the terminal is locked."),
                    stopwatch);
            }

            // The delegate returns the value the operation produced (the read registers/coils, or the value
            // just written); it becomes the result's Value so the terminal can render the response hex
            // (design §6.9: 显示请求摘要、响应、耗时、异常码和十六进制数据).
            var resultValue = await invoke(_client);

            return Complete(operation, slaveId, address, count, resultValue, Ok(resultValue, string.Empty), stopwatch);
        }
        catch (OperationCanceledException)
        {
            // A caller cancellation is surfaced as a failed result (never thrown); the attempt is audited.
            return Complete(operation, slaveId, address, count, value, Fail("operation cancelled."), stopwatch);
        }
        catch (Exception ex)
        {
            // Never throw to the caller: a transport failure surfaces as a failed result with the reason.
            return Complete(operation, slaveId, address, count, value, Fail(ex.Message), stopwatch);
        }
    }

    private string? Validate(byte slaveId, ushort address, ushort? count, string operation)
    {
        if (slaveId < MinSlaveId || slaveId > MaxSlaveId)
        {
            return $"slaveId {slaveId} is outside the valid range {MinSlaveId}..{MaxSlaveId}.";
        }

        if (IsRead(operation))
        {
            var maxCount = IsBits(operation) ? ModbusLimits.MaxBitsPerRead : ModbusLimits.MaxRegistersPerRead;
            var n = count!.Value;
            if (n == 0)
            {
                return "count must be greater than 0.";
            }

            if (n > maxCount)
            {
                return $"count {n} is greater than the per-read maximum of {maxCount}.";
            }

            if (address + n > ModbusLimits.AddressSpaceSize)
            {
                return $"address {address} + count {n} exceeds the {ModbusLimits.AddressSpaceSize}-wide address space.";
            }
        }

        return null;
    }

    private bool CanWrite()
    {
        lock (_gate)
        {
            if (!IsUnlockedLocked())
            {
                return false;
            }

            // Machine running forbids a write (reads are unaffected and handled by the caller path).
            return !_isRunningProvider();
        }
    }

    private bool Unlocked()
    {
        lock (_gate)
        {
            return IsUnlockedLocked();
        }
    }

    private bool IsUnlockedLocked()
    {
        if (_unlockedAt is not DateTime unlockedAt)
        {
            return false;
        }

        var elapsed = _clock() - unlockedAt;
        if (elapsed >= UnlockDuration)
        {
            _unlockedAt = null;
            return false;
        }

        return true;
    }

    private static bool IsRead(string operation)
        => operation.StartsWith("coils", StringComparison.Ordinal)
            || operation.StartsWith("discrete", StringComparison.Ordinal)
            || operation.StartsWith("holding", StringComparison.Ordinal)
            || operation.StartsWith("input", StringComparison.Ordinal);

    private static bool IsBits(string operation)
        => operation.StartsWith("coils", StringComparison.Ordinal)
            || operation.StartsWith("discrete", StringComparison.Ordinal);

    private static bool IsWrite(string operation) => !IsRead(operation);

    private TerminalOpResult Complete(string operation, byte slaveId, ushort address, ushort? count, object? value,
        TerminalOpResult result, Stopwatch stopwatch)
    {
        var hex = string.IsNullOrWhiteSpace(result.Hex) ? ToHex(value) : result.Hex;
        RecordAudit(new AuditEvent(AuditCategory.Debug, BuildTarget(operation, slaveId, address, count), value,
            result.Success ? null : result.Error));
        return result with { Hex = hex, Elapsed = stopwatch.Elapsed };
    }

    private static string BuildTarget(string operation, byte slaveId, ushort address, ushort? count)
        => count.HasValue
            ? $"{operation} s{slaveId} a{address} n{count.Value}"
            : $"{operation} s{slaveId} a{address}";

    private static TerminalOpResult Ok(object? value, string hex)
        => new(true, value, hex, TimeSpan.Zero, null);

    private static TerminalOpResult Fail(string error)
        => new(false, null, string.Empty, TimeSpan.Zero, error);

    private static string ToHex(object? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        return value switch
        {
            bool b => b ? "0x01" : "0x00",
            ushort u => $"0x{u:X4}",
            ushort[] regs => "0x" + string.Concat(regs.Select(r => r.ToString("X4"))),
            bool[] bits => "0x" + string.Concat(bits.Select(BitToHex)),
            _ => Convert.ToString(value) ?? string.Empty,
        };
    }

    private static string BitToHex(bool bit) => bit ? "1" : "0";

    private void RecordAudit(AuditEvent auditEvent)
    {
        try
        {
            _auditLog?.Record(auditEvent);
        }
        catch
        {
            // Audit is an observer; swallow — the command outcome is already decided.
        }
    }
}
