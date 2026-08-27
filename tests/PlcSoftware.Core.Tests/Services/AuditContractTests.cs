using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Models;
using PlcSoftware.Core.Services;

namespace PlcSoftware.Core.Tests.Services;

/// <summary>
/// Contract tests for the audit surface: 屏蔽 (bypass) writes through <see cref="CommandService"/>'s
/// M110/M111 holding commands and 参数 (parameter) writes through <see cref="ParameterService"/> must
/// each produce an audit event on an injected <see cref="IAuditLog"/> (design §4.4/§4.3 审计).
///
/// The 调试 (debug/diagnostic-terminal) category is not yet wired to a service (comes in a later task),
/// so the contract here pins that the <see cref="AuditCategory.Debug"/> path is <em>representable</em> by
/// the interface — i.e. the interface is designed so that task can plug in without changing the contract.
/// These are "any implementation of <see cref="IAuditLog"/> is exercised by these services" style pins.
/// </summary>
public class AuditContractTests
{
    [Theory]
    [InlineData(CommandTarget.LightCurtainBypass, "M110")]   // 光栅屏蔽.
    [InlineData(CommandTarget.DoorBypass, "M111")]           // 门磁屏蔽.
    public async Task BypassHoldingWrite_ProducesAuditEvent(CommandTarget target, string expectedTarget)
    {
        var client = new RecordingClient();
        var audit = new RecordingAuditLog();
        var service = new CommandService(client, new FakeGate(), new FakeDelay(), auditLog: audit);

        var result = await service.ExecuteAsync(new CommandRequest(target, Value: true), CancellationToken.None);

        Assert.Equal(CommandStatus.Success, result.Status);
        var evt = Assert.Single(audit.Events);
        Assert.Equal(AuditCategory.Mask, evt.Category);
        Assert.Equal(expectedTarget, evt.Target);
        Assert.Equal(true, evt.Value);
    }

    [Fact]
    public async Task NonBypassHoldingWrite_DoesNotProduceAuditEvent()
    {
        var client = new RecordingClient();
        var audit = new RecordingAuditLog();
        var service = new CommandService(client, new FakeGate(), new FakeDelay(), auditLog: audit);

        // M104 automatic-mode is a holding write but not a 屏蔽 target — no audit event is produced.
        var result = await service.ExecuteAsync(new CommandRequest(CommandTarget.AutoMode, Value: true), CancellationToken.None);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Empty(audit.Events);
    }

    [Fact]
    public async Task ParameterWrite_ProducesAuditEvent()
    {
        var client = new RecordingClient();
        var audit = new RecordingAuditLog();
        var service = new ParameterService(client, new FakeGate(), Writable(), auditLog: audit);

        var result = await service.WriteAsync("D201", 250, CancellationToken.None);

        Assert.Equal(ParameterWriteStatus.Success, result.Status);
        var evt = Assert.Single(audit.Events);
        Assert.Equal(AuditCategory.Parameter, evt.Category);
        Assert.Equal("D201", evt.Target);
        Assert.Equal(250, evt.Value);
    }

    [Fact]
    public async Task RejectedParameterWrite_DoesNotProduceAuditEvent()
    {
        var client = new RecordingClient();
        var audit = new RecordingAuditLog();
        var service = new ParameterService(client, new FakeGate(), Writable(), auditLog: audit);

        // D200 is read-only: rejected before any write, so nothing is audited.
        var result = await service.WriteAsync("D200", 100, CancellationToken.None);

        Assert.Equal(ParameterWriteStatus.Rejected, result.Status);
        Assert.Empty(audit.Events);
    }

    [Fact]
    public async Task BypassHoldingWrite_ThrowingAuditLog_StillSucceeds_AttemptSwallowed()
    {
        var client = new RecordingClient();
        var audit = new CountingThrowingAuditLog();
        var service = new CommandService(client, new FakeGate(), new FakeDelay(), auditLog: audit);

        // A throwing audit implementation must not turn an already-committed 屏蔽 write into a failure
        // (design audit contract: the audit is an observer). The event is attempted exactly once then
        // swallowed, and the mask write itself still lands.
        var result = await service.ExecuteAsync(new CommandRequest(CommandTarget.LightCurtainBypass, Value: true), CancellationToken.None);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal(1, audit.Attempts);
        Assert.Contains(((ushort)110, true), client.CoilWrites);
    }

    [Fact]
    public async Task ParameterWrite_ThrowingAuditLog_StillSucceeds_WithCorrectReadBack()
    {
        var client = new RecordingClient();
        var audit = new CountingThrowingAuditLog();
        var service = new ParameterService(client, new FakeGate(), Writable(), auditLog: audit);

        // A throwing audit implementation must not turn an already-committed 参数 write into a failure;
        // the write is committed and the read-back still confirms it (design audit contract).
        var result = await service.WriteAsync("D201", 250, CancellationToken.None);

        Assert.Equal(ParameterWriteStatus.Success, result.Status);
        Assert.Equal(250, result.ReadBack);
        Assert.Equal(1, audit.Attempts);
        Assert.Equal(new[] { ((ushort)101, (ushort)250) }, client.Writes);
    }

    [Fact]
    public void DebugCategory_IsPartOfTheAuditContract()
    {
        // The diagnostic terminal (a later task) must be able to plug into this interface without a
        // contract change. Pin that the Debug category exists and can be recorded end-to-end.
        Assert.True(Enum.IsDefined(typeof(AuditCategory), AuditCategory.Debug));

        var audit = new RecordingAuditLog();
        var evt = new AuditEvent(AuditCategory.Debug, "D210", 1234, "diagnostic terminal write");
        audit.Record(evt);

        Assert.Equal(evt, Assert.Single(audit.Events));
    }

    private sealed class RecordingAuditLog : IAuditLog
    {
        private readonly List<AuditEvent> _events = new();

        public IReadOnlyList<AuditEvent> Events => _events.ToArray();

        public void Record(AuditEvent auditEvent) => _events.Add(auditEvent);
    }

    /// <summary>Counts each audit attempt and always throws — a misbehaving audit backend that the
    /// producers must isolate (they must never let the recording failure change the write outcome).</summary>
    private sealed class CountingThrowingAuditLog : IAuditLog
    {
        public int Attempts { get; private set; }

        public void Record(AuditEvent auditEvent)
        {
            Attempts++;
            throw new InvalidOperationException("audit backend unavailable");
        }
    }

    private sealed class FakeGate : ICommandGate
    {
        public bool IsOnline { get; set; } = true;
        public bool IsManualIdle { get; set; } = true;
    }

    private sealed class FakeDelay : IAsyncDelay
    {
        public Task Delay(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Records the FC06 register writes and keeps a register map so FC03 read-back matches.</summary>
    private sealed class RecordingClient : IModbusClient
    {
        private readonly Dictionary<ushort, ushort> _registers = new();
        private readonly List<(ushort Address, ushort Value)> _writes = new();
        private readonly List<(ushort Address, bool Value)> _coilWrites = new();

        public IReadOnlyList<(ushort Address, ushort Value)> Writes => _writes.ToArray();

        public IReadOnlyList<(ushort Address, bool Value)> CoilWrites => _coilWrites.ToArray();

        public Task WriteSingleRegisterAsync(byte slaveId, ushort address, ushort value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _writes.Add((address, value));
            _registers[address] = value;
            return Task.CompletedTask;
        }

        public Task<ushort[]> ReadHoldingRegistersAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new[] { _registers.TryGetValue(address, out var value) ? value : (ushort)0 });
        }

        public Task WriteSingleCoilAsync(byte slaveId, ushort address, bool value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _coilWrites.Add((address, value));
            return Task.CompletedTask;
        }

        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool[]> ReadCoilsAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
            => Task.FromResult(new bool[count]);

        public Task<bool[]> ReadDiscreteInputsAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
            => Task.FromResult(new bool[count]);

        public Task<ushort[]> ReadInputRegistersAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
            => Task.FromResult(new ushort[count]);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Default writable set binding D201/D202/D204/D205 to their protocol addresses (design §4.3).</summary>
    private static ParameterDefinition[] Writable()
        => new[]
        {
            Def("D201", 101, 10, 500),
            Def("D202", 102, 100, 1500),
            Def("D204", 104, 1, 1000),
            Def("D205", 105, 10, 1000),
        };

    private static ParameterDefinition Def(string name, ushort address, int min, int max)
        => new() { Name = name, Address = address, Unit = "u", Min = min, Max = max };
}
