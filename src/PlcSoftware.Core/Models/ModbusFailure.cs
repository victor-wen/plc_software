namespace PlcSoftware.Core.Models;

/// <summary>
/// Broad category of a Modbus failure, so upper layers can react (retry, re-connect, surface a
/// device exception) without depending concrete clients on how the transport reports an error.
/// </summary>
public enum ModbusFailureKind
{
    /// <summary>The device returned a Modbus exception (a non-zero exception code).</summary>
    ProtocolException,

    /// <summary>The request did not complete within the configured timeout.</summary>
    Timeout,

    /// <summary>The client was disconnected when the request was made.</summary>
    Disconnected,
}

/// <summary>
/// Unified, immutable Modbus failure model. <see cref="ExceptionCode"/> is the protocol exception
/// code when <see cref="Kind"/> is <see cref="ModbusFailureKind.ProtocolException"/> and is null
/// otherwise; <see cref="Message"/> carries an optional human-readable detail.
/// </summary>
public sealed record ModbusFailure(
    ModbusFailureKind Kind,
    byte? ExceptionCode = null,
    string? Message = null);
