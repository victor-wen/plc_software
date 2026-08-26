using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Models;

namespace PlcSoftware.Core.Tests.Abstractions;

/// <summary>
/// Executable contract for <see cref="IModbusClient"/>.
///
/// The rules pinned here (cancellation propagation and argument boundary validation) are
/// enforced by the in-test <see cref="ConformingModbusClient"/>, which mirrors exactly the
/// behaviour real implementations must have. Task 5/7 clients must satisfy the same rules:
///   - a cancelled token must surface <see cref="OperationCanceledException"/>;
///   - read <c>count</c> must be in <c>(0, protocol max]</c>;
///   - a read must stay inside the 16-bit Modbus address space
///     (<c>address + count &lt;= 0x10000</c>);
///   - the protocol max is 2000 bits (coils / discrete inputs) and 125 registers
///     (holding / input registers), see <see cref="ModbusLimits"/>.
/// </summary>
public class ModbusContractTests
{
    private readonly IModbusClient _client = new ConformingModbusClient();

    private static readonly CancellationToken Cancelled = new(canceled: true);

    // --- Cancellation propagation ------------------------------------------------

    [Fact]
    public async Task Connect_CancelledToken_ThrowsOperationCanceled()
        => await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _client.ConnectAsync(Cancelled));

    [Fact]
    public async Task Disconnect_CancelledToken_ThrowsOperationCanceled()
        => await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _client.DisconnectAsync(Cancelled));

    [Fact]
    public async Task ReadCoils_CancelledToken_ThrowsOperationCanceled()
        => await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _client.ReadCoilsAsync(1, 0, 1, Cancelled));

    [Fact]
    public async Task ReadDiscreteInputs_CancelledToken_ThrowsOperationCanceled()
        => await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _client.ReadDiscreteInputsAsync(1, 0, 1, Cancelled));

    [Fact]
    public async Task ReadHoldingRegisters_CancelledToken_ThrowsOperationCanceled()
        => await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _client.ReadHoldingRegistersAsync(1, 0, 1, Cancelled));

    [Fact]
    public async Task ReadInputRegisters_CancelledToken_ThrowsOperationCanceled()
        => await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _client.ReadInputRegistersAsync(1, 0, 1, Cancelled));

    [Fact]
    public async Task WriteSingleCoil_CancelledToken_ThrowsOperationCanceled()
        => await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _client.WriteSingleCoilAsync(1, 0, true, Cancelled));

    [Fact]
    public async Task WriteSingleRegister_CancelledToken_ThrowsOperationCanceled()
        => await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _client.WriteSingleRegisterAsync(1, 0, 0, Cancelled));

    // --- Count boundary (bit reads: max 2000) -------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(2001)]
    public async Task ReadCoils_InvalidCount_ThrowsArgumentOutOfRange(int count)
        => await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(
            () => _client.ReadCoilsAsync(1, 0, (ushort)count, CancellationToken.None));

    [Theory]
    [InlineData(0)]
    [InlineData(2001)]
    public async Task ReadDiscreteInputs_InvalidCount_ThrowsArgumentOutOfRange(int count)
        => await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(
            () => _client.ReadDiscreteInputsAsync(1, 0, (ushort)count, CancellationToken.None));

    // --- Count boundary (register reads: max 125) ---------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(126)]
    public async Task ReadHoldingRegisters_InvalidCount_ThrowsArgumentOutOfRange(int count)
        => await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(
            () => _client.ReadHoldingRegistersAsync(1, 0, (ushort)count, CancellationToken.None));

    [Theory]
    [InlineData(0)]
    [InlineData(126)]
    public async Task ReadInputRegisters_InvalidCount_ThrowsArgumentOutOfRange(int count)
        => await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(
            () => _client.ReadInputRegistersAsync(1, 0, (ushort)count, CancellationToken.None));

    // --- Address boundary -------------------------------------------------------

    [Fact]
    public async Task ReadCoils_AddressPlusCountPastAddressSpace_ThrowsArgumentOutOfRange()
        => await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(
            () => _client.ReadCoilsAsync(1, 0xFFFF, 2, CancellationToken.None));

    [Fact]
    public async Task ReadHoldingRegisters_AddressPlusCountPastAddressSpace_ThrowsArgumentOutOfRange()
        => await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(
            () => _client.ReadHoldingRegistersAsync(1, 0xFFFF, 2, CancellationToken.None));

    // --- Happy path -------------------------------------------------------------

    [Fact]
    public async Task ReadCoils_ValidRequest_ReturnsRequestedNumberOfCoils()
        => Assert.Equal(2, (await _client.ReadCoilsAsync(1, 0, 2, CancellationToken.None)).Length);

    [Fact]
    public async Task ReadDiscreteInputs_ValidRequest_ReturnsRequestedNumberOfInputs()
        => Assert.Equal(3, (await _client.ReadDiscreteInputsAsync(1, 0, 3, CancellationToken.None)).Length);

    [Fact]
    public async Task ReadHoldingRegisters_ValidRequest_ReturnsRequestedNumberOfRegisters()
        => Assert.Equal(125, (await _client.ReadHoldingRegistersAsync(1, 0, 125, CancellationToken.None)).Length);

    [Fact]
    public async Task ReadInputRegisters_ValidRequest_ReturnsRequestedNumberOfRegisters()
        => Assert.Equal(125, (await _client.ReadInputRegistersAsync(1, 0, 125, CancellationToken.None)).Length);

    [Fact]
    public async Task Writes_ValidRequest_CompleteWithoutError()
    {
        await _client.WriteSingleCoilAsync(1, 0, true, CancellationToken.None);
        await _client.WriteSingleRegisterAsync(1, 0, 0x1234, CancellationToken.None);
    }

    // --- Operation / failure model ------------------------------------------------

    [Fact]
    public void Operation_CapturesFields_AndDefaultsIrrelevantField()
    {
        var read = new ModbusOperation(1, ModbusFunctionCode.ReadHoldingRegisters, 0x0500, Count: 3);
        Assert.Equal((byte)1, read.SlaveId);
        Assert.Equal(ModbusFunctionCode.ReadHoldingRegisters, read.Function);
        Assert.Equal((ushort)0x0500, read.Address);
        Assert.Equal((ushort)3, read.Count);
        Assert.Equal((ushort)0, read.Value);

        var write = new ModbusOperation(1, ModbusFunctionCode.WriteSingleRegister, 0x0100, Value: 1234);
        Assert.Equal((ushort)1234, write.Value);
        Assert.Equal((ushort)0, write.Count);
    }

    [Fact]
    public void Failure_CapturesKindExceptionCodeAndMessage()
    {
        var timeout = new ModbusFailure(ModbusFailureKind.Timeout, Message: "timed out");
        Assert.Equal(ModbusFailureKind.Timeout, timeout.Kind);
        Assert.Null(timeout.ExceptionCode);
        Assert.Equal("timed out", timeout.Message);

        var protocol = new ModbusFailure(ModbusFailureKind.ProtocolException, ExceptionCode: 2, Message: "illegal data address");
        Assert.Equal(ModbusFailureKind.ProtocolException, protocol.Kind);
        Assert.Equal((byte)2, protocol.ExceptionCode);

        var disconnected = new ModbusFailure(ModbusFailureKind.Disconnected);
        Assert.Equal(ModbusFailureKind.Disconnected, disconnected.Kind);
        Assert.Null(disconnected.Message);
    }

    /// <summary>
    /// Reference implementation of <see cref="IModbusClient"/> that enforces exactly the
    /// documented contract: cancellation propagation and argument boundary validation.
    /// It performs no I/O — reads return zeroed arrays of the requested length, writes are
    /// no-ops — so the contract can be exercised offline.
    /// </summary>
    private sealed class ConformingModbusClient : IModbusClient
    {
        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<bool[]> ReadCoilsAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateReadRange(address, count, ModbusLimits.MaxBitsPerRead);
            return Task.FromResult(new bool[count]);
        }

        public Task<bool[]> ReadDiscreteInputsAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateReadRange(address, count, ModbusLimits.MaxBitsPerRead);
            return Task.FromResult(new bool[count]);
        }

        public Task<ushort[]> ReadHoldingRegistersAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateReadRange(address, count, ModbusLimits.MaxRegistersPerRead);
            return Task.FromResult(new ushort[count]);
        }

        public Task<ushort[]> ReadInputRegistersAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateReadRange(address, count, ModbusLimits.MaxRegistersPerRead);
            return Task.FromResult(new ushort[count]);
        }

        // A single-point write has no count and any ushort address is in the 16-bit space, so
        // there is no argument range to validate beyond the already-compliant ushort parameters.
        public Task WriteSingleCoilAsync(byte slaveId, ushort address, bool value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task WriteSingleRegisterAsync(byte slaveId, ushort address, ushort value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static void ValidateReadRange(ushort address, ushort count, int maxCount)
        {
            if (count == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count), count, "count must be greater than 0.");
            }

            if (count > maxCount)
            {
                throw new ArgumentOutOfRangeException(nameof(count), count, $"count must be no greater than {maxCount}.");
            }

            if (address + count > ModbusLimits.AddressSpaceSize)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(address),
                    address,
                    $"address + count must not exceed the {ModbusLimits.AddressSpaceSize}-wide Modbus address space.");
            }
        }
    }
}
