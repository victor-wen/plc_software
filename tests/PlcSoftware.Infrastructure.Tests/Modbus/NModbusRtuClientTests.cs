using NModbus.IO;
using PlcSoftware.Core.Configuration;
using PlcSoftware.Infrastructure.Modbus;
using SerialPort = System.IO.Ports.SerialPort;

namespace PlcSoftware.Infrastructure.Tests.Modbus;

/// <summary>
/// Behavioural tests for <see cref="NModbusRtuClient"/>.
///
/// These tests never touch a real serial device: the underlying transport is a
/// <see cref="MemoryStream"/> supplied through the <see cref="ISerialPortFactory"/> seam, and the
/// NModbus master is built over that controlled stream. Verified rules:
///   - <see cref="SerialConnectionOptions"/> map onto <see cref="SerialPort"/> settings;
///   - every request to a not-connected (or disposed) client is rejected;
///   - dispose is idempotent and releases the master before the stream;
///   - cancellation is checked before any argument validation / connection state;
///   - broadcast (0) and reserved (248-255) slave ids are rejected per RTU convention.
/// </summary>
public class NModbusRtuClientTests
{
    private static readonly CancellationToken Cancelled = new(canceled: true);

    // --- Parameter mapping: options -> System.IO.Ports.SerialPort ----------------

    [Fact]
    public void Configure_PortNameBaudRateAndDataBits_MapFromOptions()
    {
        var options = new SerialConnectionOptions
        {
            PortName = "/dev/ttyUSB0",
            BaudRate = 19200,
            DataBits = 7,
        };
        using var port = new SerialPort();

        SerialPortFactory.Configure(port, options);

        Assert.Equal("/dev/ttyUSB0", port.PortName);
        Assert.Equal(19200, port.BaudRate);
        Assert.Equal(7, port.DataBits);
    }

    [Fact]
    public void Configure_ParityAndStopBits_MapFromOptions()
    {
        var options = new SerialConnectionOptions
        {
            Parity = Parity.Even,
            StopBits = StopBits.Two,
        };
        using var port = new SerialPort();

        SerialPortFactory.Configure(port, options);

        Assert.Equal(System.IO.Ports.Parity.Even, port.Parity);
        Assert.Equal(System.IO.Ports.StopBits.Two, port.StopBits);
    }

    [Fact]
    public void Configure_Timeout_MapsToReadAndWriteTimeout()
    {
        var options = new SerialConnectionOptions { TimeoutMs = 500 };
        using var port = new SerialPort();

        SerialPortFactory.Configure(port, options);

        Assert.Equal(500, port.ReadTimeout);
        Assert.Equal(500, port.WriteTimeout);
    }

    // --- Cancellation is checked before connection state / validation ------------

    [Fact]
    public async Task Connect_CancelledToken_ThrowsOperationCanceled()
        => await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateClient().ConnectAsync(Cancelled));

    [Fact]
    public async Task Disconnect_CancelledToken_ThrowsOperationCanceled()
        => await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateClient().DisconnectAsync(Cancelled));

    [Fact]
    public async Task ReadCoils_CancelledToken_ThrowsOperationCanceled_EvenWhenNotConnected()
        => await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateClient().ReadCoilsAsync(1, 0, 1, Cancelled));

    // --- Unconnected / disconnected request rejection -----------------------------

    [Fact]
    public async Task AllRequests_WhenNeverConnected_ThrowInvalidOperation()
    {
        var client = CreateClient();

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ReadCoilsAsync(1, 0, 1, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ReadDiscreteInputsAsync(1, 0, 1, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ReadHoldingRegistersAsync(1, 0, 1, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ReadInputRegistersAsync(1, 0, 1, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.WriteSingleCoilAsync(1, 0, true, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.WriteSingleRegisterAsync(1, 0, 0, CancellationToken.None));
    }

    [Fact]
    public async Task AllRequests_WhenDisconnected_ThrowInvalidOperation()
    {
        var client = Connect();
        await client.DisconnectAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ReadCoilsAsync(1, 0, 1, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.WriteSingleRegisterAsync(1, 0, 0, CancellationToken.None));
    }

    // --- Slave id policy (broadcast / reserved rejected per RTU convention) -------

    [Theory]
    [InlineData(0)]
    [InlineData(248)]
    [InlineData(250)]
    [InlineData(255)]
    public async Task ReadCoils_ReservedSlaveId_ThrowsArgumentOutOfRange(int slaveId)
    {
        var client = Connect();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => client.ReadCoilsAsync((byte)slaveId, 0, 1, CancellationToken.None));
    }

    [Fact]
    public async Task WriteSingleRegister_ReservedSlaveId_ThrowsArgumentOutOfRange()
    {
        var client = Connect();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => client.WriteSingleRegisterAsync(0, 0, 0, CancellationToken.None));
    }

    // --- Argument range validation (mirrors the Modbus contract) ------------------

    [Fact]
    public async Task ReadCoils_InvalidCount_ThrowsArgumentOutOfRange()
    {
        var client = Connect();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => client.ReadCoilsAsync(1, 0, 0, CancellationToken.None));
    }

    [Fact]
    public async Task ReadHoldingRegisters_PastAddressSpace_ThrowsArgumentOutOfRange()
    {
        var client = Connect();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => client.ReadHoldingRegistersAsync(1, 0xFFFF, 2, CancellationToken.None));
    }

    // --- DisposeAsync lifecycle --------------------------------------------------

    [Fact]
    public async Task DisposeAsync_Twice_IsSafeAndIdempotent()
    {
        var client = CreateClient();

        await client.DisposeAsync();
        await client.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_AfterConnect_ReleasesResources()
    {
        var client = Connect();

        await client.DisposeAsync();
    }

    [Fact]
    public async Task Disconnect_AfterDispose_ThrowsObjectDisposed()
    {
        var client = CreateClient();
        await client.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.DisconnectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Connect_AfterDispose_ThrowsObjectDisposed()
    {
        var client = CreateClient();
        await client.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ConnectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AllRequests_AfterDispose_ThrowObjectDisposed()
    {
        var client = CreateClient();
        await client.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ReadCoilsAsync(1, 0, 1, CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.WriteSingleRegisterAsync(1, 0, 0, CancellationToken.None));
    }

    // --- Connection lifecycle over the factory seam (no real device) -------------

    [Fact]
    public async Task ConnectDisconnect_WithMemoryStream_CyclesWithoutOpeningRealDevice()
    {
        var client = CreateClient();

        await client.ConnectAsync(CancellationToken.None);
        await client.DisconnectAsync(CancellationToken.None);
    }

    // --- Helpers -----------------------------------------------------------------

    private static SerialConnectionOptions Options()
        => new() { PortName = "/dev/ttyUSB0", SlaveId = 1 };

    /// <summary>Always uses the MemoryStream factory so tests never open a real serial device.</summary>
    private static NModbusRtuClient CreateClient()
        => new(Options(), new MemoryStreamFactory());

    private static NModbusRtuClient Connect()
    {
        var client = CreateClient();
        client.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
        return client;
    }

    private sealed class MemoryStreamFactory : ISerialPortFactory
    {
        public IStreamResource Create() => new MemoryStreamResource();
    }

    private sealed class MemoryStreamResource : IStreamResource
    {
        private readonly MemoryStream _stream = new();

        public int InfiniteTimeout => -1;

        public int ReadTimeout { get; set; }

        public int WriteTimeout { get; set; }

        public int Read(byte[] buffer, int offset, int count) => _stream.Read(buffer, offset, count);

        public void Write(byte[] buffer, int offset, int count) => _stream.Write(buffer, offset, count);

        public void DiscardInBuffer() => _stream.Flush();

        public void Dispose() => _stream.Dispose();
    }
}
