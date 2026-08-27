namespace PlcSoftware.Core.Services;

using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Models;

/// <summary>
/// Outcome of a parameter <see cref="ParameterService.WriteAsync"/> operation.
/// </summary>
public enum ParameterWriteStatus
{
    /// <summary>The value was written and the read-back confirmed it (design §5.3: 读回一致才报告成功).</summary>
    Success,

    /// <summary>
    /// The write was denied before any register write: limits not configured or invalid
    /// (<see cref="ParameterDefinition.Validate"/>), value outside the configured range, read-only /
    /// unknown parameter, or offline link (§5.2/§5.3/§6.5: 参数上下限未配置、越界、只读地址、断线均禁止写入).
    /// </summary>
    Rejected,

    /// <summary>
    /// The write reached the register but the read-back value differs from the written value, so the
    /// confirmed result does <em>not</em> match — the outcome is a failure and the original value is
    /// retained (Design §6.5: 写回失败时保留原值并记录原因).
    /// </summary>
    Mismatch,

    /// <summary>
    /// A communication interruption (client/transport exception) left the result unverifiable — the
    /// write may or may not have landed, so success is never reported (§5.3). The caller keeps the
    /// previously displayed value and reconciles from state.
    /// </summary>
    Unknown,
}

/// <summary>
/// Immutable result of a parameter write. <see cref="Value"/> is the requested value; <see cref="ReadBack"/>
/// is the register value observed during read-back (null when it could not be read or the write was
/// rejected); <see cref="Message"/> carries the reason for non-success outcomes.
/// </summary>
public sealed record ParameterWriteResult(
    ParameterWriteStatus Status,
    string Parameter,
    int Value,
    int? ReadBack = null,
    string? Message = null);

/// <summary>
/// Validates and performs a single-parameter write against the engineering parameter surface
/// (D201/D202/D204/D205, design §4.3/§6.5) over the shared single-queue <see cref="IModbusClient"/>.
///
/// <para><b>Range injection.</b> The writable parameters and their allowed ranges are injected by the
/// caller as <see cref="ParameterDefinition"/>s — they are the sole source of the configured limits. A
/// parameter whose limits are not configured (<c>Min</c>/<c>Max</c> null) or are invalid
/// (<c>Min &gt; Max</c>) is rejected before any write (binding constraint: 参数上下限未配置或配置非法时禁止写入).</para>
///
/// <para><b>Read-only rejection.</b> Only the injected definitions form the writable set. Any address
/// outside it (e.g. D200, D203, D210 — read-only in the point map) is rejected as read-only / unknown.</para>
///
/// <para><b>Offline.</b> The injectable <see cref="ICommandGate"/>'s <see cref="ICommandGate.IsOnline"/>
/// is false when 断线; §5.3 forbids every write then, so <see cref="WriteAsync"/> rejects without a write.</para>
///
/// <para><b>Write-then-verify.</b> A valid value is written with FC06 and then read back with FC03; only a
/// matching read-back is reported as <see cref="ParameterWriteStatus.Success"/> (§5.3: 参数写入后必须读回一致才报告成功).
/// A mismatch yields <see cref="ParameterWriteStatus.Mismatch"/> (original value retained + reason
/// recorded, §6.5) and a client exception (communication interruption) yields
/// <see cref="ParameterWriteStatus.Unknown"/> without re-throwing, so the caller never crashes.</para>
/// </summary>
public sealed class ParameterService
{
    private readonly IModbusClient _client;
    private readonly ICommandGate _gate;
    private readonly IReadOnlyDictionary<string, ParameterDefinition> _writable;
    private readonly byte _slaveId;

    /// <summary>Builds the service over the shared single-queue client. <paramref name="writableParameters"/>
    /// is the injected set of writable engineering parameters (with their configured ranges).</summary>
    public ParameterService(
        IModbusClient client,
        ICommandGate gate,
        IEnumerable<ParameterDefinition> writableParameters,
        byte slaveId = 1)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        if (writableParameters is null)
        {
            throw new ArgumentNullException(nameof(writableParameters));
        }

        _writable = writableParameters.ToDictionary(p => p.Name, StringComparer.Ordinal);
        _slaveId = slaveId;
    }

    /// <summary>
    /// Writes <paramref name="value"/> to the parameter identified by <paramref name="parameterName"/> and
    /// verifies it by reading the register back. Rejections are decided before any write; a write is
    /// reported as success only when the read-back matches.
    /// </summary>
    public async Task<ParameterWriteResult> WriteAsync(string parameterName, int value, CancellationToken cancellationToken)
    {
        if (parameterName is null)
        {
            throw new ArgumentNullException(nameof(parameterName));
        }

        // Read-only / unknown address: only the injected writable set is writable.
        if (!_writable.TryGetValue(parameterName, out var definition))
        {
            return new ParameterWriteResult(ParameterWriteStatus.Rejected, parameterName, value,
                Message: "read-only or unknown parameter address.");
        }

        // Limits not configured or invalid (Min/Max null, Min > Max): the binding constraint forbids writing.
        var errors = definition.Validate();
        if (errors.Count > 0)
        {
            return new ParameterWriteResult(ParameterWriteStatus.Rejected, parameterName, value,
                Message: string.Join("; ", errors));
        }

        // Range check (design §6.5: 写入前显示允许范围). A value must also fit a 16-bit register.
        if (value < definition.Min || value > definition.Max)
        {
            return new ParameterWriteResult(ParameterWriteStatus.Rejected, parameterName, value,
                Message: $"value {value} is outside the configured range [{definition.Min}..{definition.Max}] for {definition.Name}.");
        }

        if (value < ushort.MinValue || value > ushort.MaxValue)
        {
            return new ParameterWriteResult(ParameterWriteStatus.Rejected, parameterName, value,
                Message: $"value {value} cannot be written to a 16-bit register.");
        }

        // Offline (断线): §5.3 disables every write.
        if (!_gate.IsOnline)
        {
            return new ParameterWriteResult(ParameterWriteStatus.Rejected, parameterName, value,
                Message: "link offline.");
        }

        try
        {
            await _client.WriteSingleRegisterAsync(_slaveId, definition.Address, (ushort)value, cancellationToken);

            var readBack = await ReadBackAsync(definition.Address, cancellationToken);

            if (readBack == value)
            {
                return new ParameterWriteResult(ParameterWriteStatus.Success, parameterName, value, readBack);
            }

            // Read-back confirmed a different value: the write did not stick as requested — report a
            // mismatch, keep the original value, record the reason (design §6.5).
            return new ParameterWriteResult(ParameterWriteStatus.Mismatch, parameterName, value, readBack,
                Message: $"read-back {readBack} does not match written value {value}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Communication interruption: the outcome is unknown — never report success, never crash.
            return new ParameterWriteResult(ParameterWriteStatus.Unknown, parameterName, value,
                Message: ex.Message);
        }
    }

    private async Task<ushort> ReadBackAsync(ushort address, CancellationToken cancellationToken)
    {
        var registers = await _client.ReadHoldingRegistersAsync(_slaveId, address, count: 1, cancellationToken);
        if (registers.Length == 0)
        {
            // A read-back that yields no register cannot confirm the write — report a dedicated
            // unverifiable-outcome reason (design §5.3: 读回不一致/不完整不报告成功).
            throw new InvalidOperationException("read-back returned an empty register array.");
        }

        return registers[0];
    }
}
