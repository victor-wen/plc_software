namespace PlcSoftware.Core.Abstractions;

/// <summary>
/// Documented Modbus protocol limits shared by every <see cref="IModbusClient"/> implementation.
///
/// These are the boundaries enforced by the Modbus contract (see <c>ModbusContractTests</c>) and
/// must be respected by all concrete clients (Task 5/7). They are Modbus standard maximums: at
/// most 2000 bits (coils / discrete inputs) per read and at most 125 registers (holding / input
/// registers) per read. A read must also stay inside the 16-bit address space, i.e. the request
/// covers addresses <c>[address, address + count)</c> with <c>address + count &lt;= AddressSpaceSize</c>.
/// </summary>
public static class ModbusLimits
{
    /// <summary>Maximum coil / discrete-input count readable per request (FC01 / FC02).</summary>
    public const int MaxBitsPerRead = 2000;

    /// <summary>Maximum holding / input register count readable per request (FC03 / FC04).</summary>
    public const int MaxRegistersPerRead = 125;

    /// <summary>Size of the 16-bit Modbus address space (addresses 0..<c>AddressSpaceSize - 1</c>).</summary>
    public const int AddressSpaceSize = 0x10000;
}
