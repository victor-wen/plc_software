using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Models;
using PlcSoftware.Core.Services;

namespace PlcSoftware.Core.Tests.Services;

/// <summary>
/// Behavioural tests for <see cref="ParameterService"/>, the D126/D128/D204/D122 range-validation,
/// write and read-back surface of design §4.3/§6.5.
///
/// The service is exercised over an injectable <see cref="IModbusClient"/> (a recording fake with a
/// configurable register map, read-back override and failure injection) and an injectable
/// <see cref="ICommandGate"/> (whose <see cref="ICommandGate.IsOnline"/> is the 断线 writes-forbidden
/// flag, design §5.3).
///
/// Verified rules:
///   - pre-write rejection with <em>no</em> register write when limits are missing (Min/Max null),
///     the value is outside the configured range, the address is read-only (not in the injected writable
///     set), or the link is offline (design §5.2/§5.3: 参数上下限未配置/越界/只读地址/断线禁止写入);
///   - a valid value is written then read back; a consistent read-back reports success;
///   - a read-back that does not match the written value reports a mismatch (reason recorded), and a
///     communication interruption (client throws) reports a failure without crashing and without
///     reporting success — §5.3 "参数写入后必须读回一致才报告成功" / §6.5 "写回失败时保留原值并记录原因".
/// </summary>
public class ParameterServiceTests
{
    [Fact]
    public async Task Write_MissingLimits_Rejected_NoWrite()
    {
        var client = new RecordingClient();
        var wr = new ParameterService(client, new FakeGate(), Writable().With("D126", min: null, max: null));

        var result = await wr.WriteAsync("D126", 100, CancellationToken.None);

        Assert.Equal(ParameterWriteStatus.Rejected, result.Status);
        Assert.NotNull(result.Message);
        Assert.Empty(client.Writes); // rejected before any write is attempted.
    }

    [Fact]
    public async Task Write_InvalidLimits_MinAboveMax_Rejected_NoWrite()
    {
        var client = new RecordingClient();
        var wr = new ParameterService(client, new FakeGate(), Writable().With("D126", min: 500, max: 10));

        // D126 has an invalid configuration (Min 500 > Max 10): the binding constraint forbids writing.
        var result = await wr.WriteAsync("D126", 100, CancellationToken.None);

        Assert.Equal(ParameterWriteStatus.Rejected, result.Status);
        Assert.NotNull(result.Message);
        Assert.Empty(client.Writes); // rejected before any write is attempted.
    }

    [Theory]
    [InlineData(0, "below min")]
    [InlineData(1000, "above max")]
    public async Task Write_OutOfRange_Rejected_NoWrite(int value, string _)
    {
        var client = new RecordingClient();
        var wr = new ParameterService(client, new FakeGate(), Writable());

        // D126 range is [10..500].
        var result = await wr.WriteAsync("D126", value, CancellationToken.None);

        Assert.Equal(ParameterWriteStatus.Rejected, result.Status);
        Assert.NotNull(result.Message);
        Assert.Empty(client.Writes);
    }

    [Theory]
    [InlineData("D120")]   // record step number — read-only.
    [InlineData("D130")]   // current width — read-only.
    [InlineData("D210")]   // tuning delta — read-only.
    [InlineData("D999")]   // not in the point map at all.
    public async Task Write_ReadOnlyAddress_Rejected_NoWrite(string name)
    {
        var client = new RecordingClient();
        var wr = new ParameterService(client, new FakeGate(), Writable());

        var result = await wr.WriteAsync(name, 100, CancellationToken.None);

        Assert.Equal(ParameterWriteStatus.Rejected, result.Status);
        Assert.NotNull(result.Message);
        Assert.Empty(client.Writes);
    }

    [Fact]
    public async Task Write_LinkOffline_Rejected_NoWrite()
    {
        var client = new RecordingClient();
        var wr = new ParameterService(client, new FakeGate { IsOnline = false }, Writable());

        var result = await wr.WriteAsync("D126", 100, CancellationToken.None);

        Assert.Equal(ParameterWriteStatus.Rejected, result.Status);
        Assert.NotNull(result.Message);
        Assert.Empty(client.Writes); // §5.3 forbids every write while 断线.
    }

    [Fact]
    public async Task Write_ValidValue_WrittenAndReadBack_Consistent_Success()
    {
        var client = new RecordingClient();
        var wr = new ParameterService(client, new FakeGate(), Writable());

        var result = await wr.WriteAsync("D126", 250, CancellationToken.None);

        Assert.Equal(ParameterWriteStatus.Success, result.Status);
        // One write at the D126 protocol address (26), then a read-back that matched.
        Assert.Equal(((ushort)26, (ushort)250), client.Writes.Single());
        Assert.Equal(250, result.ReadBack);
        Assert.Null(result.Message);
    }

    [Theory]
    [InlineData(10, "at min")]
    [InlineData(500, "at max")]
    public async Task Write_BoundaryValue_OnRangeEdges_Allowed_Success(int value, string _)
    {
        var client = new RecordingClient();
        var wr = new ParameterService(client, new FakeGate(), Writable());

        // D126 range is [10..500]; both edges are inclusive and must be writable (and read back identically).
        var result = await wr.WriteAsync("D126", value, CancellationToken.None);

        Assert.Equal(ParameterWriteStatus.Success, result.Status);
        Assert.Equal(((ushort)26, (ushort)value), client.Writes.Single());
        Assert.Equal(value, result.ReadBack);
        Assert.Null(result.Message);
    }

    [Fact]
    public async Task Write_MinEqualsMax_SinglePointRange_Allowed_Success()
    {
        var client = new RecordingClient();
        var wr = new ParameterService(client, new FakeGate(), Writable().With("D126", min: 250, max: 250));

        // Min == Max is a valid configuration: exactly one value is in range, and it is writable.
        var result = await wr.WriteAsync("D126", 250, CancellationToken.None);

        Assert.Equal(ParameterWriteStatus.Success, result.Status);
        Assert.Equal(((ushort)26, (ushort)250), client.Writes.Single());
        Assert.Equal(250, result.ReadBack);
        Assert.Null(result.Message);
    }

    [Fact]
    public async Task Write_ReadBackMismatch_ReportsFailureWithReason()
    {
        var client = new RecordingClient(overrideReadBack: 251);
        var wr = new ParameterService(client, new FakeGate(), Writable());

        var result = await wr.WriteAsync("D126", 250, CancellationToken.None);

        // The write "landed" (the PLC is believed to have attempted it) but the verified read-back
        // differs, so §5.3 forbids reporting success and §6.5 requires the reason to be recorded.
        Assert.Equal(ParameterWriteStatus.Mismatch, result.Status);
        Assert.NotNull(result.Message);
        Assert.Equal(251, result.ReadBack);
        Assert.Equal(((ushort)26, (ushort)250), client.Writes.Single());
    }

    [Fact]
    public async Task Write_ReadBackCommunicationFailure_ReportsFailure_NoCrash()
    {
        var client = new RecordingClient(throwOnReadBack: true);
        var wr = new ParameterService(client, new FakeGate(), Writable());

        var result = await wr.WriteAsync("D126", 250, CancellationToken.None);

        // The write may have reached the PLC but the read-back could not be verified (通信中断), so the
        // outcome is unknown — never success, and no exception escapes to crash the caller.
        Assert.Equal(ParameterWriteStatus.Unknown, result.Status);
        Assert.NotNull(result.Message);
        Assert.Equal((ushort)26, client.Writes.Single().Address);
    }

    [Fact]
    public async Task Write_ReadBackEmptyArray_ReportsUnknownWithDedicatedMessage()
    {
        var client = new RecordingClient(emptyReadBack: true);
        var wr = new ParameterService(client, new FakeGate(), Writable());

        var result = await wr.WriteAsync("D126", 250, CancellationToken.None);

        // The write landed but the read-back returned no register(s) — the outcome is unverifiable, so
        // success is never reported. The reason must be the dedicated empty-read-back message (§5.3).
        Assert.Equal(ParameterWriteStatus.Unknown, result.Status);
        Assert.NotNull(result.Message);
        Assert.Contains("empty", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal((ushort)26, client.Writes.Single().Address);
    }

    [Fact]
    public async Task Write_WriteCommunicationFailure_ReportsFailure_NoRetry()
    {
        var client = new RecordingClient(throwOnWrite: true);
        var wr = new ParameterService(client, new FakeGate(), Writable());

        var result = await wr.WriteAsync("D126", 250, CancellationToken.None);

        Assert.Equal(ParameterWriteStatus.Unknown, result.Status);
        Assert.NotNull(result.Message);
        Assert.Empty(client.Writes); // the write was abandoned; §5.3: no blind retry.
    }

    private sealed class FakeGate : ICommandGate
    {
        public bool IsOnline { get; set; } = true;
        public bool IsManualIdle { get; set; } = true;
    }

    /// <summary>
    /// Recording fake that emulates FC06/FC03 over a per-address register map. It records every register
    /// write and can be seeded to (a) override the read-back value, or (b) throw on the write or the
    /// read-back to simulate a communication interruption.
    /// </summary>
    private sealed class RecordingClient : IModbusClient
    {
        private readonly Dictionary<ushort, ushort> _registers = new();
        private readonly bool _throwOnWrite;
        private readonly bool _throwOnReadBack;
        private readonly ushort? _overrideReadBack;
        private readonly bool _emptyReadBack;
        private readonly List<(ushort Address, ushort Value)> _writes = new();

        public RecordingClient(bool throwOnWrite = false, bool throwOnReadBack = false, ushort? overrideReadBack = null, bool emptyReadBack = false)
        {
            _throwOnWrite = throwOnWrite;
            _throwOnReadBack = throwOnReadBack;
            _overrideReadBack = overrideReadBack;
            _emptyReadBack = emptyReadBack;
        }

        public IReadOnlyList<(ushort Address, ushort Value)> Writes => _writes.ToArray();

        public Task WriteSingleRegisterAsync(byte slaveId, ushort address, ushort value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_throwOnWrite)
            {
                throw new TimeoutException("simulated write response timeout");
            }

            _writes.Add((address, value));
            _registers[address] = value;
            return Task.CompletedTask;
        }

        public Task<ushort[]> ReadHoldingRegistersAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_throwOnReadBack)
            {
                throw new TimeoutException("simulated read-back timeout");
            }

            if (_overrideReadBack is not null)
            {
                return Task.FromResult(new[] { _overrideReadBack.Value });
            }

            if (_emptyReadBack)
            {
                return Task.FromResult(Array.Empty<ushort>());
            }

            return Task.FromResult(new[] { _registers.TryGetValue(address, out var value) ? value : (ushort)0 });
        }

        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool[]> ReadCoilsAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
            => Task.FromResult(new bool[count]);

        public Task<bool[]> ReadDiscreteInputsAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
            => Task.FromResult(new bool[count]);

        public Task<ushort[]> ReadInputRegistersAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
            => Task.FromResult(new ushort[count]);

        public Task WriteSingleCoilAsync(byte slaveId, ushort address, bool value, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Default writable set binding D126/D128/D204/D122 to their protocol addresses (design §4.3).</summary>
    private static ParameterDefinition[] Writable()
        => new[]
        {
            Def("D126", 26, "Hz", 10, 500),
            Def("D128", 28, "mm", 100, 1500),
            Def("D204", 104, "脉冲/mm", 1, 1000),
            Def("D122", 22, "Hz", 10, 1000),
        };

    private static ParameterDefinition Def(string name, ushort address, string unit, int min, int max)
        => new() { Name = name, Address = address, Unit = unit, Min = min, Max = max };
}

internal static class ParameterTestExtensions
{
    /// <summary>Rebuilds the writable set, overriding one parameter's limits (used to model missing/unset bounds).</summary>
    public static ParameterDefinition[] With(this IEnumerable<ParameterDefinition> source, string name, int? min, int? max)
    {
        var defs = source.ToList();
        var index = defs.FindIndex(d => d.Name == name);
        if (index >= 0)
        {
            defs[index] = new ParameterDefinition { Name = name, Address = defs[index].Address, Unit = defs[index].Unit, Min = min, Max = max };
        }

        return defs.ToArray();
    }
}
