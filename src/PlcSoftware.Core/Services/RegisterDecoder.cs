namespace PlcSoftware.Core.Services;

/// <summary>
/// Decodes raw holding-register reads from the polled D-register blocks into the partial point-value
/// dictionary carried by <see cref="Models.DeviceSnapshot.Values"/> (keys are logical PLC addresses).
///
/// <para><b>Fast block</b> (D100-D110, protocol offsets 0-10). The register at index <c>i</c> is
/// <c>D(100+i)</c>. It decodes the packed M-bit maps — D100→M0-M15, D102→M200-M215, D103→M30-M45,
/// D104→M300-M315, D105→M316 (only bit0, the rest of M317-M331 is reserved) — plus the host
/// watchdog echo (D106) and the fault code (D110).</para>
///
/// <para><b>Process block</b> (D120-D140, protocol offsets 20-40). The register at index <c>i</c> is
/// <c>D(120+i)</c> i.e. protocol 20+i. It decodes the step number (D120), the process scalars
/// (D122 belt speed, D124 spare调宽速度设定值, D126 tuning speed Hz, D128 target width, D130
/// current width, D136 width pulse count single, D138 production count single) and the PLC heartbeat
/// (D140). D124 and D220 share the same label in the user table; both are decoded — D124 in this
/// block and D220 in the param block — to avoid missing whichever the PLC actually populates.</para>
///
/// <para><b>Param block</b> (D204-D220, protocol offsets 104-120). The register at index <c>i</c> is
/// <c>D(204+i)</c> i.e. protocol 104+i. It decodes the pulse equivalent (D204), the tuning delta
/// (D210) and the调宽速度设定值 (D220).</para>
///
/// <para><b>Graceful partials.</b> A block shorter than its full span is decoded for whatever registers
/// are present; absent registers are simply omitted, so a partial read never throws. The decoder only
/// inspects the supplied register list; it never touches the PLC.</para>
/// </summary>
public static class RegisterDecoder
{
    private const string HeartbeatKey = "D140";
    private const string WatchdogKey = "D106";
    private const string FaultKey = "D110";
    private const string PressureBitKey = "M316";
    /// <summary>Single-word production count (D138).</summary>
    public const string ProductionCountKey = "D138";
    /// <summary>Single-word width pulse count (D136).</summary>
    public const string WidthPulseCountKey = "D136";
    /// <summary>调宽速度设定值 at D124 (process block).</summary>
    public const string TuningSpeedSetting124Key = "D124";
    /// <summary>调宽速度设定值 at D220 (param block).</summary>
    public const string TuningSpeedSetting220Key = "D220";

    /// <summary>Decodes the fast register block (protocol 0-10, D100-D110) into a partial value dictionary.</summary>
    public static IReadOnlyDictionary<string, object?> DecodeFast(IReadOnlyList<ushort>? registers)
    {
        var values = new Dictionary<string, object?>();
        if (registers is null)
        {
            return values;
        }

        // Register index i ↔ protocol address i ↔ D100+i.
        if (Has(registers, 0)) DecodeBits(registers[0], baseAddress: 0, values);   // D100 → M0-M15.
        if (Has(registers, 2)) DecodeBits(registers[2], baseAddress: 200, values); // D102 → M200-M215.
        if (Has(registers, 3)) DecodeBits(registers[3], baseAddress: 30, values);  // D103 → M30-M45.
        if (Has(registers, 4)) DecodeBits(registers[4], baseAddress: 300, values); // D104 → M300-M315.
        if (Has(registers, 5)) values[PressureBitKey] = (registers[5] & 1) != 0;   // D105 bit0 → M316.
        if (Has(registers, 6)) values[WatchdogKey] = registers[6];                 // D106.
        if (Has(registers, 10)) values[FaultKey] = registers[10];                  // D110.

        return values;
    }

    /// <summary>Decodes the process register block (protocol 20-40, D120-D140) into a partial value dictionary.</summary>
    public static IReadOnlyDictionary<string, object?> DecodeProcess(IReadOnlyList<ushort>? registers)
    {
        var values = new Dictionary<string, object?>();
        if (registers is null)
        {
            return values;
        }

        // Register index i ↔ protocol address 20+i ↔ D120+i.
        if (Has(registers, 0)) values["D120"] = registers[0];   // Step number.
        if (Has(registers, 2)) values["D122"] = registers[2];   // Belt speed Hz.
        if (Has(registers, 4)) values[TuningSpeedSetting124Key] = registers[4]; // D124 调宽速度设定值.
        if (Has(registers, 6)) values["D126"] = registers[6];   // Tuning speed Hz.
        if (Has(registers, 8)) values["D128"] = registers[8];   // Target width mm.
        if (Has(registers, 10)) values["D130"] = registers[10]; // Current width mm.
        if (Has(registers, 16)) values[WidthPulseCountKey] = registers[16]; // D136 single.
        if (Has(registers, 18)) values[ProductionCountKey] = registers[18]; // D138 single.
        if (Has(registers, 20)) values[HeartbeatKey] = registers[20]; // D140 heartbeat.

        return values;
    }

    /// <summary>Decodes the param register block (protocol 104-120, D204-D220) into a partial value dictionary.</summary>
    public static IReadOnlyDictionary<string, object?> DecodeParams(IReadOnlyList<ushort>? registers)
    {
        var values = new Dictionary<string, object?>();
        if (registers is null)
        {
            return values;
        }

        // Register index i ↔ protocol address 104+i ↔ D204+i.
        if (Has(registers, 0)) values["D204"] = registers[0];   // Pulse equivalent.
        if (Has(registers, 6)) values["D210"] = registers[6]; // Tuning delta (D210 = 104+6).
        if (Has(registers, 16)) values[TuningSpeedSetting220Key] = registers[16]; // D220 = 104+16.

        return values;
    }

    private static bool Has(IReadOnlyList<ushort> registers, int index) => index < registers.Count;

    private static void DecodeBits(ushort word, int baseAddress, IDictionary<string, object?> values)
    {
        for (var bit = 0; bit < 16; bit++)
        {
            values[$"M{baseAddress + bit}"] = (word & (1 << bit)) != 0;
        }
    }
}
