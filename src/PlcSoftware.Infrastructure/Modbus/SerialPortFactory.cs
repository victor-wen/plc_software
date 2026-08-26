using System.IO.Ports;
using NModbus.IO;
using PlcSoftware.Core.Configuration;

namespace PlcSoftware.Infrastructure.Modbus;

/// <summary>
/// Produces the NModbus <see cref="IStreamResource"/> that backs an <see cref="NModbusRtuClient"/>.
///
/// This is the seam that keeps <see cref="NModbusRtuClient"/> testable without ever opening a real
/// serial device: a test supplies a factory that returns a resource over an in-memory stream, and the
/// production implementation creates a configured, opened <see cref="SerialPort"/>. The client owns
/// the returned resource's lifetime in both cases.
/// </summary>
public interface ISerialPortFactory
{
    /// <summary>Creates an opened, ready-to-use NModbus stream resource representing the serial link.</summary>
    IStreamResource Create();
}

/// <summary>
/// Default <see cref="ISerialPortFactory"/> that builds a real <see cref="SerialPort"/> from
/// <see cref="SerialConnectionOptions"/> and opens it. Configuration is delegated to
/// <see cref="Configure"/> so the options-to-port mapping is unit-testable without hardware.
/// </summary>
public sealed class SerialPortFactory : ISerialPortFactory
{
    private readonly SerialConnectionOptions _options;

    public SerialPortFactory(SerialConnectionOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Maps <see cref="SerialConnectionOptions"/> onto a <see cref="SerialPort"/> instance. Does not
    /// open the port; callers decide when (the factory does so in <see cref="Create"/>).
    /// </summary>
    public static void Configure(SerialPort port, SerialConnectionOptions options)
    {
        if (port is null)
        {
            throw new ArgumentNullException(nameof(port));
        }

        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        port.PortName = options.PortName;
        port.BaudRate = options.BaudRate;
        port.DataBits = options.DataBits;
        port.Parity = (System.IO.Ports.Parity)options.Parity;
        port.StopBits = (System.IO.Ports.StopBits)options.StopBits;
        port.ReadTimeout = options.TimeoutMs;
        port.WriteTimeout = options.TimeoutMs;
    }

    public IStreamResource Create()
    {
        var port = new SerialPort();
        Configure(port, _options);
        port.Open();
        return new SerialPortResource(port);
    }

    /// <summary>
    /// Adapts a <see cref="SerialPort"/> (a <see cref="Component"/>, not a <see cref="Stream"/>) to
    /// NModbus's <see cref="IStreamResource"/>. The factory owns the port; this resource closes it on
    /// dispose.
    /// </summary>
    private sealed class SerialPortResource : IStreamResource
    {
        private readonly SerialPort _port;

        public SerialPortResource(SerialPort port)
        {
            _port = port ?? throw new ArgumentNullException(nameof(port));
        }

        public int InfiniteTimeout => SerialPort.InfiniteTimeout;

        public int ReadTimeout
        {
            get => _port.ReadTimeout;
            set => _port.ReadTimeout = value;
        }

        public int WriteTimeout
        {
            get => _port.WriteTimeout;
            set => _port.WriteTimeout = value;
        }

        public int Read(byte[] buffer, int offset, int count) => _port.Read(buffer, offset, count);

        public void Write(byte[] buffer, int offset, int count) => _port.Write(buffer, offset, count);

        public void DiscardInBuffer() => _port.DiscardInBuffer();

        public void Dispose() => _port.Dispose();
    }
}
