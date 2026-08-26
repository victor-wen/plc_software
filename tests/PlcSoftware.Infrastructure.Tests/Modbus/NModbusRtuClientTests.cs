using System.Reflection;
using NModbus;
using NModbus.IO;
using PlcSoftware.Core.Configuration;
using PlcSoftware.Infrastructure.Modbus;
using SerialPort = System.IO.Ports.SerialPort;

namespace PlcSoftware.Infrastructure.Tests.Modbus;

/// <summary>
/// Behavioural tests for <see cref="NModbusRtuClient"/>.
///
/// These tests never touch a real serial device: the underlying transport is a
/// <see cref="MemoryStream"/> (or a scripted in-memory RTU frame) supplied through the
/// <see cref="ISerialPortFactory"/> seam, and the NModbus master is built over that controlled
/// stream. Verified rules:
///   - <see cref="SerialConnectionOptions"/> map onto <see cref="SerialPort"/> settings;
///   - every request to a not-connected (or disposed) client is rejected;
///   - dispose is idempotent and releases the master before the stream;
///   - cancellation is checked before any argument validation / connection state;
///   - broadcast (0) and reserved (248-255) slave ids are rejected per RTU convention;
///   - all requests are serialised: a second concurrent request waits for the first to finish;
///   - the transport retry count is mapped from the options;
///   - a successful read is parsed from a real held-modbus RTU response frame (FC01/FC02/FC03),
///     proving the client's method-to-function-code bindings.
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

    // --- Finding 1: request serialisation ----------------------------------------

    [Fact]
    public async Task ReadCoils_ConcurrentSecondRequest_SerializesUntilFirstCompletes()
    {
        var (client, factory) = NewClientWithFakeFactory();
        await client.ConnectAsync(CancellationToken.None);

        var master = factory.Master!;
        master.CoilReadPendings = new Queue<TaskCompletionSource<bool[]>>();
        master.SecondReadStarted = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = new TaskCompletionSource<bool[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<bool[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        master.CoilReadPendings.Enqueue(first);
        master.CoilReadPendings.Enqueue(second);

        var firstRequest = client.ReadCoilsAsync(1, 0, 2, CancellationToken.None);
        var secondRequest = client.ReadCoilsAsync(1, 0, 2, CancellationToken.None);

        // The second request must not have reached the master until the first completes.
        Assert.Equal(1, master.ReadCoilCalls);
        Assert.False(secondRequest.IsCompleted);

        // Completing the first read releases the waiting second request.
        first.SetResult(new[] { true, false });
        var firstValues = await firstRequest;
        Assert.Equal(new[] { true, false }, firstValues);

        // The second request now acquires the lock and reaches the master.
        await master.SecondReadStarted.Task;
        Assert.Equal(2, master.ReadCoilCalls);
        Assert.False(secondRequest.IsCompleted);

        second.SetResult(new[] { false, true });
        var secondValues = await secondRequest;
        Assert.Equal(new[] { false, true }, secondValues);
    }

    // --- Finding 2: retry count is mapped to the transport -----------------------

    [Fact]
    public async Task Connect_SetsTransportRetries_FromOptions()
    {
        var options = Options();
        options.Retries = 5;

        var log = new List<string>();
        var factory = (FakeFactory)(object)DispatchProxy.Create<IModbusFactory, FakeFactory>();
        factory.DisposeLog = log;
        var client = new NModbusRtuClient(options, new TrackingResourceFactory(log), (IModbusFactory)(object)factory);

        await client.ConnectAsync(CancellationToken.None);

        Assert.Equal(5, factory.Transport!.Retries);
    }

    // --- Finding 3a: release order master -> stream resource ---------------------

    [Fact]
    public async Task Disconnect_DisposesMasterBeforeStreamResource()
    {
        var log = new List<string>();
        var factory = (FakeFactory)(object)DispatchProxy.Create<IModbusFactory, FakeFactory>();
        factory.DisposeLog = log;
        var client = new NModbusRtuClient(Options(), new TrackingResourceFactory(log), (IModbusFactory)(object)factory);

        await client.ConnectAsync(CancellationToken.None);
        await client.DisconnectAsync(CancellationToken.None);

        Assert.Equal(new[] { "master", "resource" }, log);
    }

    [Fact]
    public async Task DisposeAsync_DisposesMasterBeforeStreamResource()
    {
        var log = new List<string>();
        var factory = (FakeFactory)(object)DispatchProxy.Create<IModbusFactory, FakeFactory>();
        factory.DisposeLog = log;
        var client = new NModbusRtuClient(Options(), new TrackingResourceFactory(log), (IModbusFactory)(object)factory);

        await client.ConnectAsync(CancellationToken.None);
        await client.DisposeAsync();

        Assert.Equal(new[] { "master", "resource" }, log);
    }

    // --- Finding 3b: happy path over the real master -----------------------------

    [Fact]
    public async Task ReadCoils_RealMasterOverScriptedFrame_ParsesBits()
    {
        // FC01 response: coils 0, 2 and 4 are on (LSB-first).
        var frame = BitsReadFrame(slaveId: 1, functionCode: 0x01, dataBytes: new byte[] { 0b00010101 });
        var client = new NModbusRtuClient(Options(), new ScriptedRtuFactory(new ScriptedRtuResource(frame)));

        await client.ConnectAsync(CancellationToken.None);
        var bits = await client.ReadCoilsAsync(1, 0, 5, CancellationToken.None);

        Assert.Equal(new[] { true, false, true, false, true }, bits);
    }

    [Fact]
    public async Task ReadDiscreteInputs_RealMasterOverScriptedFrame_ParsesBits()
    {
        // FC02 response: inputs 0 and 1 are on (LSB-first); distinct from the FC01 pattern so a
        // swapped FC01<->FC02 binding would fail loudly.
        var frame = BitsReadFrame(slaveId: 1, functionCode: 0x02, dataBytes: new byte[] { 0b00000011 });
        var client = new NModbusRtuClient(Options(), new ScriptedRtuFactory(new ScriptedRtuResource(frame)));

        await client.ConnectAsync(CancellationToken.None);
        var bits = await client.ReadDiscreteInputsAsync(1, 0, 3, CancellationToken.None);

        Assert.Equal(new[] { true, true, false }, bits);
    }

    [Fact]
    public async Task ReadHoldingRegisters_RealMasterOverScriptedFrame_ParsesRegisters()
    {
        // FC03 response: two registers 0x1234 and 0xABCD, big-endian bytes.
        var frame = RegisterReadFrame(slaveId: 1, registers: new ushort[] { 0x1234, 0xABCD });
        var client = new NModbusRtuClient(Options(), new ScriptedRtuFactory(new ScriptedRtuResource(frame)));

        await client.ConnectAsync(CancellationToken.None);
        var registers = await client.ReadHoldingRegistersAsync(1, 0, 2, CancellationToken.None);

        Assert.Equal(new ushort[] { 0x1234, 0xABCD }, registers);
    }

    // --- Small item: half-created transport is disposed on a failed connect ------

    [Fact]
    public async Task Connect_CreateMasterThrows_DisposesHalfCreatedTransportAndResource()
    {
        var log = new List<string>();
        var resource = new TrackingStreamResource(log);
        var factory = (FakeFactory)(object)DispatchProxy.Create<IModbusFactory, FakeFactory>();
        factory.DisposeLog = log;
        factory.ThrowOnCreateMaster = true;
        var client = new NModbusRtuClient(Options(), new SingleResourceFactory(resource), (IModbusFactory)(object)factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(CancellationToken.None));

        Assert.Contains("transport", log);
        Assert.Contains("resource", log);
        Assert.True(factory.Transport!.Disposed);
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

    private (NModbusRtuClient Client, FakeFactory Factory) NewClientWithFakeFactory()
    {
        var log = new List<string>();
        var factory = (FakeFactory)(object)DispatchProxy.Create<IModbusFactory, FakeFactory>();
        factory.DisposeLog = log;
        var client = new NModbusRtuClient(Options(), new TrackingResourceFactory(log), (IModbusFactory)(object)factory);
        return (client, factory);
    }

    /// <summary>Builds an RTU response frame: [slave, fc, payload..., crc-lo, crc-hi].</summary>
    private static byte[] RtuFrame(byte slaveId, byte functionCode, byte[] payload)
    {
        var frame = new List<byte> { slaveId, functionCode };
        frame.AddRange(payload);
        frame.AddRange(NModbus.Utility.ModbusUtility.CalculateCrc(frame.ToArray()));
        return frame.ToArray();
    }

    /// <summary>FC01/FC02 read response: [slave, fc, byteCount, packed bits...].</summary>
    private static byte[] BitsReadFrame(byte slaveId, byte functionCode, byte[] dataBytes)
    {
        var payload = new List<byte> { (byte)dataBytes.Length };
        payload.AddRange(dataBytes);
        return RtuFrame(slaveId, functionCode, payload.ToArray());
    }

    /// <summary>FC03/FC04 read response: [slave, fc, byteCount, hi, lo, ...].</summary>
    private static byte[] RegisterReadFrame(byte slaveId, ushort[] registers)
    {
        var payload = new List<byte> { (byte)(registers.Length * 2) };
        foreach (var register in registers)
        {
            payload.Add((byte)(register >> 8));
            payload.Add((byte)(register & 0xFF));
        }

        return RtuFrame(slaveId, 0x03, payload.ToArray());
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

    /// <summary>
    /// A stream resource that behaves like a slave: <see cref="Write"/> captures the request, and
    /// <see cref="Read"/> replays the pre-loaded RTU response frame. This lets tests drive a
    /// successful read through the real NModbus master over an in-memory RTU frame.
    /// </summary>
    private sealed class ScriptedRtuFactory : ISerialPortFactory
    {
        private readonly IStreamResource _resource;

        public ScriptedRtuFactory(IStreamResource resource) => _resource = resource;

        public IStreamResource Create() => _resource;
    }

    private sealed class ScriptedRtuResource : IStreamResource
    {
        private readonly byte[] _response;
        private int _readPos;

        public ScriptedRtuResource(byte[] response) => _response = response;

        public int InfiniteTimeout => -1;

        public int ReadTimeout { get; set; }

        public int WriteTimeout { get; set; }

        public int Read(byte[] buffer, int offset, int count)
        {
            int available = _response.Length - _readPos;
            if (available <= 0)
            {
                return 0;
            }

            int toRead = Math.Min(count, available);
            Array.Copy(_response, _readPos, buffer, offset, toRead);
            _readPos += toRead;
            return toRead;
        }

        public void Write(byte[] buffer, int offset, int count)
        {
        }

        public void DiscardInBuffer()
        {
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Returns a fresh tracking resource per call, recording dispose order by label.</summary>
    private sealed class TrackingResourceFactory : ISerialPortFactory
    {
        private readonly List<string>? _log;

        public TrackingResourceFactory(List<string>? log) => _log = log;

        public IStreamResource Create() => new TrackingStreamResource(_log);
    }

    private sealed class SingleResourceFactory : ISerialPortFactory
    {
        private readonly IStreamResource _resource;

        public SingleResourceFactory(IStreamResource resource) => _resource = resource;

        public IStreamResource Create() => _resource;
    }

    private sealed class TrackingStreamResource : IStreamResource
    {
        private readonly MemoryStream _stream = new();
        private readonly List<string>? _log;

        public TrackingStreamResource(List<string>? log) => _log = log;

        public bool Disposed { get; private set; }

        public int InfiniteTimeout => -1;

        public int ReadTimeout { get; set; }

        public int WriteTimeout { get; set; }

        public int Read(byte[] buffer, int offset, int count) => _stream.Read(buffer, offset, count);

        public void Write(byte[] buffer, int offset, int count) => _stream.Write(buffer, offset, count);

        public void DiscardInBuffer() => _stream.Flush();

        public void Dispose()
        {
            if (!Disposed)
            {
                Disposed = true;
                _log?.Add("resource");
            }
        }
    }

    // --- Fake proxied NModbus infrastructure --------------------------------------
    //
    // DispatchProxy lets a single small Invoke override stand in for the whole interface, so the
    // serialisation / retry / release-order / failed-connect tests record what they need without
    // hand-writing dozens of interface methods.

    public class FakeFactory : DispatchProxy
    {
        public List<string>? DisposeLog { get; set; }

        public bool ThrowOnCreateMaster { get; set; }

        public FakeMaster? Master { get; private set; }

        public FakeTransport? Transport { get; private set; }

        public IStreamResource? LastResource { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod!.Name)
            {
                case "CreateRtuTransport":
                    LastResource = (IStreamResource)args![0]!;
                    Transport = (FakeTransport)(object)DispatchProxy.Create<IModbusRtuTransport, FakeTransport>();
                    Transport.Resource = LastResource;
                    Transport.DisposeLog = DisposeLog;
                    return Transport;

                case "CreateMaster":
                    if (ThrowOnCreateMaster)
                    {
                        throw new InvalidOperationException("simulated master creation failure");
                    }

                    Master = (FakeMaster)(object)DispatchProxy.Create<IModbusSerialMaster, FakeMaster>();
                    Master.TransportValue = (FakeTransport)(object)args![0]!;
                    Master.DisposeLog = DisposeLog;
                    return Master;

                default:
                    return default;
            }
        }
    }

    public class FakeMaster : DispatchProxy
    {
        public FakeTransport? TransportValue { get; set; }

        public List<string>? DisposeLog { get; set; }

        public Queue<TaskCompletionSource<bool[]>>? CoilReadPendings { get; set; }

        public Queue<TaskCompletionSource<bool[]>>? InputReadPendings { get; set; }

        public TaskCompletionSource<object>? SecondReadStarted { get; set; }

        public bool Disposed { get; private set; }

        public int ReadCoilCalls { get; private set; }

        public int ReadInputCalls { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod!.Name)
            {
                case "get_Transport":
                    return TransportValue;

                case "Dispose":
                    if (!Disposed)
                    {
                        Disposed = true;
                        DisposeLog?.Add("master");
                    }

                    return null;

                case "ReadCoilsAsync":
                    ReadCoilCalls++;
                    if (ReadCoilCalls == 2)
                    {
                        SecondReadStarted?.TrySetResult(true);
                    }

                    if (CoilReadPendings is { Count: > 0 })
                    {
                        return CoilReadPendings.Dequeue().Task;
                    }

                    return Task.FromResult(new[] { true });

                case "ReadInputsAsync":
                    ReadInputCalls++;
                    if (InputReadPendings is { Count: > 0 })
                    {
                        return InputReadPendings.Dequeue().Task;
                    }

                    return Task.FromResult(new[] { true });

                case "WriteSingleCoilAsync":
                    return Task.CompletedTask;

                case "WriteSingleRegisterAsync":
                    return Task.CompletedTask;

                default:
                    return default;
            }
        }
    }

    public class FakeTransport : DispatchProxy
    {
        public IStreamResource? Resource { get; set; }

        public List<string>? DisposeLog { get; set; }

        public bool Disposed { get; private set; }

        public int Retries { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod!.Name)
            {
                case "get_Retries":
                    return Retries;

                case "set_Retries":
                    Retries = (int)args![0]!;
                    return null;

                case "Dispose":
                    if (!Disposed)
                    {
                        Disposed = true;
                        DisposeLog?.Add("transport");
                        Resource?.Dispose();
                    }

                    return null;

                default:
                    return default;
            }
        }
    }
}
