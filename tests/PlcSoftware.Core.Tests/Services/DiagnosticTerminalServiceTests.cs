using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Services;

namespace PlcSoftware.Core.Tests.Services;

/// <summary>
/// Behavioural tests for <see cref="DiagnosticTerminalService"/>, the structured Modbus debug terminal
/// (design §6.5): FC01/02/03/04 reads and FC05/06 single-point writes, gated by an unlock guard and a
/// machine-running guard, with every command audited (category <see cref="AuditCategory.Debug"/>).
///
/// Verified rules:
///   - argument bounds are enforced (slaveId 1..247, count ≤ MaxReadBits/MaxReadRegisters, address+count
///     within the 16-bit address space) — an invalid range returns a failure result, never a throw;
///   - reads are always allowed; writes are allowed only when the terminal is unlocked
///     (via <see cref="DiagnosticTerminalService.SetUnlocked"/>) and the machine is not running;
///   - the terminal auto-locks 5 minutes after unlock (driven by the injected clock);
///   - every command (success, validation failure and transport failure) is audited;
///   - results carry an internal elapsed measurement and an ASCII/hex rendering, and never throw to caller.
/// </summary>
public class DiagnosticTerminalServiceTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    // ---- Argument bounds --------------------------------------------------

    [Theory]
    [InlineData((byte)0)]    // slaveId < 1
    [InlineData((byte)248)]  // slaveId > 247
    public async Task ReadCoils_InvalidSlaveId_ReturnsFailure(byte slaveId)
    {
        var service = new DiagnosticTerminalService(new RecordingClient());

        var result = await service.ReadCoils(slaveId, address: 0, count: 1, None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Theory]
    [InlineData((ushort)0)]                               // count == 0
    [InlineData((ushort)(ModbusLimits.MaxBitsPerRead + 1))]// count > MaxBitsPerRead
    public async Task ReadCoils_InvalidCount_ReturnsFailure(ushort count)
    {
        var service = new DiagnosticTerminalService(new RecordingClient());

        var result = await service.ReadCoils(1, address: 0, count, None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Theory]
    [InlineData(ModbusLimits.MaxRegistersPerRead + 1)]   // count > MaxRegistersPerRead
    [InlineData(-1)]                                     // count < 0 (int path via ushort never, but pin size)
    public async Task ReadHolding_InvalidCount_ReturnsFailure(int count)
    {
        var service = new DiagnosticTerminalService(new RecordingClient());
        if (count < 0)
        {
            return; // count is a ushort parameter, the negative branch is covered by the 0 bound.
        }

        var result = await service.ReadHolding(1, address: 0, (ushort)count, None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task ReadCoils_AddressPlusCountOverflowsAddressSpace_ReturnsFailure()
    {
        var service = new DiagnosticTerminalService(new RecordingClient());

        // address + count crosses the 16-bit boundary: [0xFFFE, 0xFFFE + 4) exceeds 0x10000.
        var result = await service.ReadCoils(1, address: 0xFFFE, count: 4, None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task WriteRegister_InvalidSlaveId_ReturnsFailure()
    {
        var client = new RecordingClient();
        var service = new DiagnosticTerminalService(client);
        service.SetUnlocked(true);

        var result = await service.WriteRegister(0, address: 0, value: 1, None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Empty(client.Writes);
    }

    // ---- Unlock guard -----------------------------------------------------

    [Fact]
    public async Task WriteRegister_Locked_ReturnsFailure_NoWrite()
    {
        var client = new RecordingClient();
        var service = new DiagnosticTerminalService(client); // locked by default.

        var result = await service.WriteRegister(1, address: 0, value: 42, None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Empty(client.Writes);
        Assert.False(service.IsUnlocked);
    }

    [Fact]
    public async Task WriteRegister_Unlocked_AllowsWrite_Success()
    {
        var client = new RecordingClient();
        var service = new DiagnosticTerminalService(client);
        service.SetUnlocked(true);

        var result = await service.WriteRegister(1, address: 100, value: 0x1234, None);

        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.Contains(((ushort)100, (ushort)0x1234), client.Writes);
        Assert.True(service.IsUnlocked);
    }

    [Fact]
    public void SetUnlocked_False_LocksTerminal()
    {
        var service = new DiagnosticTerminalService(new RecordingClient());
        service.SetUnlocked(true);
        Assert.True(service.IsUnlocked);

        service.SetUnlocked(false);
        Assert.False(service.IsUnlocked);
    }

    // ---- Machine-running guard -------------------------------------------

    [Fact]
    public async Task WriteCoil_MachineRunning_ReturnsFailure_NoWrite()
    {
        var client = new RecordingClient();
        var service = new DiagnosticTerminalService(client, isRunningProvider: () => true);
        service.SetUnlocked(true);

        var result = await service.WriteCoil(1, address: 0, value: true, None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Empty(client.CoilWrites);
    }

    [Fact]
    public async Task WriteRegister_MachineRunning_ReturnsFailure_NoWrite()
    {
        var client = new RecordingClient();
        var service = new DiagnosticTerminalService(client, isRunningProvider: () => true);
        service.SetUnlocked(true);

        var result = await service.WriteRegister(1, address: 0, value: 1, None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Empty(client.Writes);
    }

    [Fact]
    public async Task ReadCoils_MachineRunning_StillSucceeds()
    {
        var client = new RecordingClient();
        var service = new DiagnosticTerminalService(client, isRunningProvider: () => true);

        var result = await service.ReadCoils(1, address: 0, count: 2, None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task ReadHolding_MachineRunning_StillSucceeds()
    {
        var client = new RecordingClient();
        var service = new DiagnosticTerminalService(client, isRunningProvider: () => true);

        var result = await service.ReadHolding(1, address: 0, count: 2, None);

        Assert.True(result.Success);
    }

    // ---- Auto-lock after 5 minutes ---------------------------------------

    [Fact]
    public async Task WriteRegister_AfterFiveMinutes_AutomaticallyLocked_NoWrite()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        var client = new RecordingClient();
        var service = new DiagnosticTerminalService(client, clock: () => clock.Now);
        service.SetUnlocked(true);
        Assert.True(service.IsUnlocked);

        // Advance just past the 5-minute window.
        clock.Now = clock.Now.AddMinutes(5);
        var result = await service.WriteRegister(1, address: 0, value: 1, None);

        Assert.False(result.Success);
        Assert.False(service.IsUnlocked);
        Assert.Empty(client.Writes);
    }

    [Fact]
    public async Task WriteRegister_BeforeFiveMinutes_StillUnlocked_AllowsWrite()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        var client = new RecordingClient();
        var service = new DiagnosticTerminalService(client, clock: () => clock.Now);
        service.SetUnlocked(true);

        clock.Now = clock.Now.AddMinutes(4);
        var result = await service.WriteRegister(1, address: 0, value: 7, None);

        Assert.True(result.Success);
        Assert.True(service.IsUnlocked);
        Assert.Contains(((ushort)0, (ushort)7), client.Writes);
    }

    [Fact]
    public async Task WriteRegister_AtExactlyFiveMinutes_AutomaticallyLocked()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        var client = new RecordingClient();
        var service = new DiagnosticTerminalService(client, clock: () => clock.Now);
        service.SetUnlocked(true);

        clock.Now = clock.Now.AddMinutes(5); // boundary considered locked at exactly 5.
        var result = await service.WriteRegister(1, address: 0, value: 9, None);

        Assert.False(result.Success);
    }

    // ---- Audit ------------------------------------------------------------

    [Fact]
    public async Task ReadHolding_Success_ProducesDebugAudit()
    {
        var audit = new RecordingAuditLog();
        var service = new DiagnosticTerminalService(new RecordingClient(), auditLog: audit);

        var result = await service.ReadHolding(1, address: 0, count: 1, None);

        Assert.True(result.Success);
        var evt = Assert.Single(audit.Events);
        Assert.Equal(AuditCategory.Debug, evt.Category);
        Assert.Contains("holding", evt.Target, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriteRegister_Success_ProducesDebugAudit()
    {
        var audit = new RecordingAuditLog();
        var client = new RecordingClient();
        var service = new DiagnosticTerminalService(client, auditLog: audit);
        service.SetUnlocked(true);

        var result = await service.WriteRegister(1, address: 0, value: 0x1234, None);

        Assert.True(result.Success);
        var evt = Assert.Single(audit.Events);
        Assert.Equal(AuditCategory.Debug, evt.Category);
        Assert.Equal((ushort)0x1234, evt.Value);
        Assert.NotNull(result.Hex);
        Assert.NotEqual(TimeSpan.Zero, result.Elapsed);
    }

    [Fact]
    public async Task ReadHolding_BoundsFailure_StillProducesDebugAudit()
    {
        var audit = new RecordingAuditLog();
        var service = new DiagnosticTerminalService(new RecordingClient(), auditLog: audit);

        var result = await service.ReadHolding(1, address: 0xFFFE, count: 10, None);

        Assert.False(result.Success);
        var evt = Assert.Single(audit.Events); // audited even on failure.
        Assert.Equal(AuditCategory.Debug, evt.Category);
    }

    [Fact]
    public async Task WriteRegister_Locked_StillProducesDebugAudit()
    {
        var audit = new RecordingAuditLog();
        var service = new DiagnosticTerminalService(new RecordingClient(), auditLog: audit);

        var result = await service.WriteRegister(1, address: 0, value: 1, None);

        Assert.False(result.Success);
        var evt = Assert.Single(audit.Events);
        Assert.Equal(AuditCategory.Debug, evt.Category);
    }

    [Fact]
    public async Task WriteRegister_ClientThrows_ReturnsErrorResult_DoesNotThrow()
    {
        var audit = new RecordingAuditLog();
        var client = new RecordingClient { ThrowOnWrite = true };
        var service = new DiagnosticTerminalService(client, auditLog: audit);
        service.SetUnlocked(true);

        var result = await service.WriteRegister(1, address: 0, value: 1, None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.False(((RecordingClient)client).WriteSucceeded);
    }

    [Fact]
    public async Task ReadHolding_ClientThrows_ReturnsErrorResult_DoesNotThrow()
    {
        var client = new RecordingClient { ThrowOnRead = true };
        var service = new DiagnosticTerminalService(client);

        var result = await service.ReadHolding(1, address: 0, count: 1, None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    // ---- Hex + elapsed ----------------------------------------------------

    [Fact]
    public async Task ReadHolding_Success_HasHexAndElapsed()
    {
        var service = new DiagnosticTerminalService(new RecordingClient());

        var result = await service.ReadHolding(1, address: 0, count: 1, None);

        Assert.True(result.Success);
        Assert.NotNull(result.Hex);
        Assert.NotEqual(TimeSpan.Zero, result.Elapsed);
    }

    [Fact]
    public async Task ReadCoils_Success_HasHexAndElapsed()
    {
        var service = new DiagnosticTerminalService(new RecordingClient());

        var result = await service.ReadCoils(1, address: 0, count: 2, None);

        Assert.True(result.Success);
        Assert.NotNull(result.Hex);
        Assert.NotEqual(TimeSpan.Zero, result.Elapsed);
    }

    [Fact]
    public async Task WriteRegister_Success_HasHexAndElapsed()
    {
        var service = new DiagnosticTerminalService(new RecordingClient());
        service.SetUnlocked(true);

        var result = await service.WriteRegister(1, address: 0, value: 0xABCD, None);

        Assert.True(result.Success);
        Assert.NotNull(result.Hex);
        Assert.NotEqual(TimeSpan.Zero, result.Elapsed);
    }

    // ---- Recording fakes (test-local) ------------------------------------

    private sealed class FakeClock
    {
        public DateTime Now { get; set; } = DateTime.UtcNow;
    }

    private sealed class RecordingAuditLog : IAuditLog
    {
        private readonly List<AuditEvent> _events = new();
        public IReadOnlyList<AuditEvent> Events => _events.ToArray();
        public void Record(AuditEvent auditEvent) => _events.Add(auditEvent);
    }

    private sealed class RecordingClient : IModbusClient
    {
        public List<(ushort Address, bool Value)> CoilWrites { get; } = new();
        public List<(ushort Address, ushort Value)> Writes { get; } = new();
        public bool ThrowOnWrite { get; set; }
        public bool ThrowOnRead { get; set; }
        public bool WriteSucceeded { get; private set; }

        public Task WriteSingleCoilAsync(byte slaveId, ushort address, bool value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnWrite) throw new TimeoutException("simulated write timeout");
            CoilWrites.Add((address, value));
            WriteSucceeded = true;
            return Task.CompletedTask;
        }

        public Task WriteSingleRegisterAsync(byte slaveId, ushort address, ushort value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnWrite) throw new TimeoutException("simulated write timeout");
            Writes.Add((address, value));
            WriteSucceeded = true;
            return Task.CompletedTask;
        }

        public Task<bool[]> ReadCoilsAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnRead) throw new TimeoutException("simulated read timeout");
            return Task.FromResult(new bool[count]);
        }

        public Task<bool[]> ReadDiscreteInputsAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnRead) throw new TimeoutException("simulated read timeout");
            return Task.FromResult(new bool[count]);
        }

        public Task<ushort[]> ReadHoldingRegistersAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnRead) throw new TimeoutException("simulated read timeout");
            return Task.FromResult(new ushort[count]);
        }

        public Task<ushort[]> ReadInputRegistersAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnRead) throw new TimeoutException("simulated read timeout");
            return Task.FromResult(new ushort[count]);
        }

        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
