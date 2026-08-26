using PlcSoftware.Infrastructure.Simulation;

namespace PlcSoftware.Infrastructure.Tests.Simulation;

/// <summary>
/// Behavioural tests for <see cref="InMemoryModbusClient"/> (the FC01/02/03/04/05/06 simulation
/// over <see cref="SimulationMemory"/>). The argument-validation and cancellation rules here mirror
/// the ones pinned by the Core contract (<c>ModbusContractTests</c>); the disconnected-state rule is
/// specific to this client.
/// </summary>
public class InMemoryModbusClientTests
{
    private static readonly CancellationToken Cancelled = new(canceled: true);

    private static async Task<InMemoryModbusClient> CreateConnectedClientAsync()
    {
        var client = new InMemoryModbusClient();
        await client.ConnectAsync(CancellationToken.None);
        return client;
    }

    // --- Coils (FC01 read / FC05 write) -------------------------------------------

    [Fact]
    public async Task WriteSingleCoil_True_ReadBackTrue()
    {
        var client = await CreateConnectedClientAsync();

        await client.WriteSingleCoilAsync(1, 0x0003, true, CancellationToken.None);

        Assert.True((await client.ReadCoilsAsync(1, 0x0003, 1, CancellationToken.None))[0]);
    }

    [Fact]
    public async Task WriteSingleCoil_False_OverwritesPreviousTrue()
    {
        var client = await CreateConnectedClientAsync();

        await client.WriteSingleCoilAsync(1, 0x0003, true, CancellationToken.None);
        await client.WriteSingleCoilAsync(1, 0x0003, false, CancellationToken.None);

        Assert.False((await client.ReadCoilsAsync(1, 0x0003, 1, CancellationToken.None))[0]);
    }

    [Fact]
    public async Task ReadCoils_UnwrittenAddress_DefaultsToFalse()
    {
        var client = await CreateConnectedClientAsync();

        var coils = await client.ReadCoilsAsync(1, 0x0005, 3, CancellationToken.None);

        Assert.All(coils, c => Assert.False(c));
    }

    // --- Holding registers (FC03 read / FC06 write) ------------------------------

    [Fact]
    public async Task WriteSingleRegister_ReadBackValue()
    {
        var client = await CreateConnectedClientAsync();

        await client.WriteSingleRegisterAsync(1, 0x0100, 0x1234, CancellationToken.None);

        Assert.Equal((ushort)0x1234, (await client.ReadHoldingRegistersAsync(1, 0x0100, 1, CancellationToken.None))[0]);
    }

    [Fact]
    public async Task ReadHoldingRegisters_UnwrittenAddress_DefaultsToZero()
    {
        var client = await CreateConnectedClientAsync();

        var registers = await client.ReadHoldingRegistersAsync(1, 0x0100, 2, CancellationToken.None);

        Assert.All(registers, r => Assert.Equal((ushort)0, r));
    }

    // --- Discrete inputs (FC02 read) ---------------------------------------------

    [Fact]
    public async Task ReadDiscreteInputs_ReturnsSeededValues()
    {
        var memory = new SimulationMemory();
        memory.WriteDiscreteInput(0x0002, true);
        var client = new InMemoryModbusClient(memory);
        await client.ConnectAsync(CancellationToken.None);

        var inputs = await client.ReadDiscreteInputsAsync(1, 0, 3, CancellationToken.None);

        Assert.False(inputs[0]);
        Assert.False(inputs[1]);
        Assert.True(inputs[2]);
    }

    [Fact]
    public async Task ReadDiscreteInputs_UnwrittenAddress_DefaultsToFalse()
    {
        var client = await CreateConnectedClientAsync();

        var inputs = await client.ReadDiscreteInputsAsync(1, 0, 3, CancellationToken.None);

        Assert.All(inputs, i => Assert.False(i));
    }

    // --- Input registers (FC04 read) ---------------------------------------------

    [Fact]
    public async Task ReadInputRegisters_ReturnsSeededValues()
    {
        var memory = new SimulationMemory();
        memory.WriteInputRegister(0x0002, 0xABCD);
        var client = new InMemoryModbusClient(memory);
        await client.ConnectAsync(CancellationToken.None);

        var inputs = await client.ReadInputRegistersAsync(1, 0, 3, CancellationToken.None);

        Assert.Equal((ushort)0, inputs[0]);
        Assert.Equal((ushort)0, inputs[1]);
        Assert.Equal((ushort)0xABCD, inputs[2]);
    }

    [Fact]
    public async Task ReadInputRegisters_UnwrittenAddress_DefaultsToZero()
    {
        var client = await CreateConnectedClientAsync();

        var inputs = await client.ReadInputRegistersAsync(1, 0, 3, CancellationToken.None);

        Assert.All(inputs, r => Assert.Equal((ushort)0, r));
    }

    // --- Count boundary ----------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(2001)]
    public async Task ReadCoils_InvalidCount_ThrowsArgumentOutOfRange(int count)
    {
        var client = await CreateConnectedClientAsync();

        await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(
            () => client.ReadCoilsAsync(1, 0, (ushort)count, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2001)]
    public async Task ReadDiscreteInputs_InvalidCount_ThrowsArgumentOutOfRange(int count)
    {
        var client = await CreateConnectedClientAsync();

        await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(
            () => client.ReadDiscreteInputsAsync(1, 0, (ushort)count, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(126)]
    public async Task ReadHoldingRegisters_InvalidCount_ThrowsArgumentOutOfRange(int count)
    {
        var client = await CreateConnectedClientAsync();

        await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(
            () => client.ReadHoldingRegistersAsync(1, 0, (ushort)count, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(126)]
    public async Task ReadInputRegisters_InvalidCount_ThrowsArgumentOutOfRange(int count)
    {
        var client = await CreateConnectedClientAsync();

        await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(
            () => client.ReadInputRegistersAsync(1, 0, (ushort)count, CancellationToken.None));
    }

    // --- Address space boundary --------------------------------------------------

    [Fact]
    public async Task ReadCoils_PastAddressSpace_ThrowsArgumentOutOfRange()
    {
        var client = await CreateConnectedClientAsync();

        await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(
            () => client.ReadCoilsAsync(1, 0xFFFF, 2, CancellationToken.None));
    }

    [Fact]
    public async Task ReadDiscreteInputs_PastAddressSpace_ThrowsArgumentOutOfRange()
    {
        var client = await CreateConnectedClientAsync();

        await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(
            () => client.ReadDiscreteInputsAsync(1, 0xFFFF, 2, CancellationToken.None));
    }

    [Fact]
    public async Task ReadHoldingRegisters_PastAddressSpace_ThrowsArgumentOutOfRange()
    {
        var client = await CreateConnectedClientAsync();

        await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(
            () => client.ReadHoldingRegistersAsync(1, 0xFFFF, 2, CancellationToken.None));
    }

    [Fact]
    public async Task ReadInputRegisters_PastAddressSpace_ThrowsArgumentOutOfRange()
    {
        var client = await CreateConnectedClientAsync();

        await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(
            () => client.ReadInputRegistersAsync(1, 0xFFFF, 2, CancellationToken.None));
    }

    // --- Cancellation propagation ------------------------------------------------

    [Fact]
    public async Task Connect_CancelledToken_ThrowsOperationCanceled()
        => await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new InMemoryModbusClient().ConnectAsync(Cancelled));

    [Fact]
    public async Task Disconnect_CancelledToken_ThrowsOperationCanceled()
    {
        var client = await CreateConnectedClientAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DisconnectAsync(Cancelled));
    }

    [Fact]
    public async Task ReadCoils_CancelledToken_ThrowsOperationCanceled()
    {
        var client = await CreateConnectedClientAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.ReadCoilsAsync(1, 0, 1, Cancelled));
    }

    [Fact]
    public async Task ReadHoldingRegisters_CancelledToken_ThrowsOperationCanceled()
    {
        var client = await CreateConnectedClientAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.ReadHoldingRegistersAsync(1, 0, 1, Cancelled));
    }

    [Fact]
    public async Task WriteSingleCoil_CancelledToken_ThrowsOperationCanceled()
    {
        var client = await CreateConnectedClientAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.WriteSingleCoilAsync(1, 0, true, Cancelled));
    }

    [Fact]
    public async Task WriteSingleRegister_CancelledToken_ThrowsOperationCanceled()
    {
        var client = await CreateConnectedClientAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.WriteSingleRegisterAsync(1, 0, 0, Cancelled));
    }

    // --- Disconnected-state rejection --------------------------------------------

    [Fact]
    public async Task ReadCoils_WhenDisconnected_ThrowsInvalidOperation()
    {
        var client = new InMemoryModbusClient();

        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => client.ReadCoilsAsync(1, 0, 1, CancellationToken.None));
    }

    [Fact]
    public async Task WriteSingleCoil_WhenDisconnected_ThrowsInvalidOperation()
    {
        var client = new InMemoryModbusClient();

        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => client.WriteSingleCoilAsync(1, 0, true, CancellationToken.None));
    }

    [Fact]
    public async Task Connect_ThenDisconnect_RequestIsRejected()
    {
        var client = await CreateConnectedClientAsync();
        await client.DisconnectAsync(CancellationToken.None);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => client.ReadCoilsAsync(1, 0, 1, CancellationToken.None));
    }

    // --- DisposeAsync lifecycle --------------------------------------------------

    [Fact]
    public async Task DisposeAsync_ReleasesResources_SubsequentReadThrows()
    {
        var client = await CreateConnectedClientAsync();

        await client.DisposeAsync();

        await Assert.ThrowsAnyAsync<ObjectDisposedException>(
            () => client.ReadCoilsAsync(1, 0, 1, CancellationToken.None));
    }
}
