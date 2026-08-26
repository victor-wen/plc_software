namespace PlcSoftware.Core.Abstractions;

/// <summary>
/// Shared read-request range validation for <see cref="IModbusClient"/> implementations.
///
/// Every concrete client (Task 5's in-memory simulation and Task 7's NModbus RTU adapter) must
/// reject reads the same way, so the checks live here instead of being triplicated. The rules are
/// pinned by the Modbus contract (see <c>ModbusContractTests</c>): <paramref name="count"/> must be
/// in <c>(0, <paramref name="maxCount"/>]</c> and the request must stay inside the 16-bit Modbus
/// address space (<c>address + count &lt;= <see cref="ModbusLimits.AddressSpaceSize"/></c>).
/// </summary>
public static class ModbusReadRange
{
    /// <summary>
    /// Validates a read covering <c>[address, address + count)</c>, throwing
    /// <see cref="ArgumentOutOfRangeException"/> when the range is invalid.
    /// </summary>
    /// <param name="address">Zero-based Modbus protocol address.</param>
    /// <param name="count">Number of points to read.</param>
    /// <param name="maxCount">Protocol maximum per read (see <see cref="ModbusLimits"/>).</param>
    public static void Validate(ushort address, ushort count, int maxCount)
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
