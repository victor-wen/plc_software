namespace PlcSoftware.Core.Services;

/// <summary>
/// Decodes raw holding-register reads from the polled D-register blocks into the partial point-value
/// dictionary carried by <see cref="Models.DeviceSnapshot.Values"/> (keys are logical PLC addresses).
///
/// <para><b>Fast block</b> (D100-D110, protocol offsets 0-10). The register at index <c>i</c> is
/// <c>D(100+i)</c>. It decodes the packed M-bit maps — D100→M0-M15, D102→M200-M215, D103→M30-M45,
/// D104→M300-M315, D105.bit0→M316 — plus the heartbeat counter (D101), the host watchdog echo (D106)
/// and the fault code (D110).</para>
///
/// <para><b>Process block</b> (D200-D213, protocol offsets 100-113). The register at index <c>i</c> is
/// <c>D(200+i)</c>. It decodes the step number (D200), the parameter registers (D201, D202, D204, D205),
/// the current width (D203), the tuning delta (D210) and the two low-word-first UInt32 composites
/// (D207+D208 production count, D212+D213 width pulse count).</para>
///
/// <para><b>Graceful partials.</b> A block shorter than its full span is decoded for whatever registers
/// are present; absent registers are simply omitted, so a partial read never throws. A 32-bit composite
/// is emitted only when both low and high words are present, because a lone word cannot be combined. The
/// decoder only inspects the supplied register list; it never touches the PLC.</para>
/// </summary>
public static class RegisterDecoder
{
    private const string HeartbeatKey = "D101";
    private const string WatchdogKey = "D106";
    private const string FaultKey = "D110";
    private const string PressureBitKey = "M316";
    /// <summary>Composite key for the low-word-first UInt32 production count (D207 low, D208 high).</summary>
    public const string ProductionCountKey = "D207.D208";
    /// <summary>Composite key for the low-word-first UInt32 width pulse count (D212 low, D213 high).</summary>
    public const string WidthPulseCountKey = "D212.D213";

    /// <summary>Decodes the fast register block into a partial value dictionary.</summary>
    public static IReadOnlyDictionary<string, object?> DecodeFast(IReadOnlyList<ushort>? registers)
    {
        var values = new Dictionary<string, object?>();
        if (registers is null)
        {
            return values;
        }

        // Register index i ↔ protocol address i ↔ D100+i.
        if (Has(registers, 0)) DecodeBits(registers[0], baseAddress: 0, values);   // D100 → M0-M15.
        if (Has(registers, 1)) values[HeartbeatKey] = registers[1];               // D101.
        if (Has(registers, 2)) DecodeBits(registers[2], baseAddress: 200, values); // D102 → M200-M215.
        if (Has(registers, 3)) DecodeBits(registers[3], baseAddress: 30, values);  // D103 → M30-M45.
        if (Has(registers, 4)) DecodeBits(registers[4], baseAddress: 300, values); // D104 → M300-M315.
        if (Has(registers, 5)) values[PressureBitKey] = (registers[5] & 1) != 0;   // D105.bit0 → M316.
        if (Has(registers, 6)) values[WatchdogKey] = registers[6];                 // D106.
        if (Has(registers, 10)) values[FaultKey] = registers[10];                  // D110.

        return values;
    }

    /// <summary>Decodes the process register block into a partial value dictionary.</summary>
    public static IReadOnlyDictionary<string, object?> DecodeProcess(IReadOnlyList<ushort>? registers)
    {
        var values = new Dictionary<string, object?>();
        if (registers is null)
        {
            return values;
        }

        // Register index i ↔ protocol address 100+i ↔ D200+i.
        if (Has(registers, 0)) values["D200"] = registers[0];   // Step number.
        if (Has(registers, 1)) values["D201"] = registers[1];   // Tuning speed.
        if (Has(registers, 2)) values["D202"] = registers[2];   // Target width.
        if (Has(registers, 3)) values["D203"] = registers[3];   // Current width.
        if (Has(registers, 4)) values["D204"] = registers[4];   // Pulse equivalent.
        if (Has(registers, 5)) values["D205"] = registers[5];   // Belt speed.
        if (Has(registers, 7) && Has(registers, 8))
        {
            // D208 high word first, D207 low word second.
            values[ProductionCountKey] = Compose(registers[8], registers[7]);
        }

        if (Has(registers, 10)) values["D210"] = registers[10]; // Tuning delta.
        if (Has(registers, 12) && Has(registers, 13))
        {
            // D213 high word first, D212 low word second.
            values[WidthPulseCountKey] = Compose(registers[13], registers[12]);
        }

        return values;
    }

    private static bool Has(IReadOnlyList<ushort> registers, int index) => index < registers.Count;

    private static uint Compose(ushort high, ushort low) => ((uint)high << 16) | low;

    private static void DecodeBits(ushort word, int baseAddress, IDictionary<string, object?> values)
    {
        for (var bit = 0; bit < 16; bit++)
        {
            values[$"M{baseAddress + bit}"] = (word & (1 << bit)) != 0;
        }
    }
}
