using PlcSoftware.Core.Services;

namespace PlcSoftware.Core.Tests.Services;

/// <summary>
/// Behavioural tests for <see cref="RegisterDecoder"/>: decoding the fast block (D100-D110,
/// protocol offsets 0-10) and the process block (D200-D213, protocol offsets 100-113) into the
/// <see cref="Models.DeviceSnapshot.Values"/> shape (keys are logical PLC addresses).
///
/// Verified rules:
///   - the packed M-bit maps decode bit0..bit15 to the mapped M addresses (D100→M0-M15,
///     D102→M200-M215, D103→M30-M45, D104→M300-M315, D105.bit0→M316);
///   - the single-word points (heartbeat D101, watchdog echo D106, fault D110 and the process
///     scalars D200-D205/D210) decode as <see cref="ushort"/>;
///   - D207+D208 and D212+D213 combine low-word-first into a single <c>uint</c>;
///   - partial (short or null) register lists are decoded for whatever is present and never throw.
/// </summary>
public class RegisterDecoderTests
{
    private const ushort HeartbeatRegister = 0x0042;

    [Fact]
    public void DecodeFast_MapsD100Bits_ToM0M15()
    {
        var registers = new ushort[11];
        registers[0] = 0x8001; // D100: bit0 and bit15 set.

        var values = RegisterDecoder.DecodeFast(registers);

        Assert.True(Bit(values, "M0"));
        Assert.False(Bit(values, "M1"));
        Assert.False(Bit(values, "M14"));
        Assert.True(Bit(values, "M15"));
    }

    [Fact]
    public void DecodeFast_MapsD102Bits_ToM200M215()
    {
        var registers = new ushort[11];
        registers[2] = 0x8001; // D102: M200 and M215.

        var values = RegisterDecoder.DecodeFast(registers);

        Assert.True(Bit(values, "M200"));
        Assert.False(Bit(values, "M201"));
        Assert.True(Bit(values, "M215"));
    }

    [Fact]
    public void DecodeFast_MapsD103Bits_ToM30M45()
    {
        var registers = new ushort[11];
        registers[3] = 0x2000; // D103: bit13 → M43.

        var values = RegisterDecoder.DecodeFast(registers);

        Assert.True(Bit(values, "M43"));
        Assert.False(Bit(values, "M42"));
    }

    [Fact]
    public void DecodeFast_MapsD104Bits_ToM300M315()
    {
        var registers = new ushort[11];
        registers[4] = 0x0004; // D104: bit2 → M302.

        var values = RegisterDecoder.DecodeFast(registers);

        Assert.True(Bit(values, "M302"));
        Assert.False(Bit(values, "M303"));
    }

    [Fact]
    public void DecodeFast_MapsD105Bit0_ToM316_IgnoringOtherBits()
    {
        var registers = new ushort[11];
        registers[5] = 0x0001; // D105.bit0 set → M316.

        var values = RegisterDecoder.DecodeFast(registers);
        Assert.True(Bit(values, "M316"));

        registers[5] = 0x0002; // bit1 set, bit0 clear → M316 is false (only bit0 is defined).
        values = RegisterDecoder.DecodeFast(registers);
        Assert.False(Bit(values, "M316"));
    }

    [Fact]
    public void DecodeFast_DecodesSingleWordRegisters_AsUshort()
    {
        var registers = new ushort[11];
        registers[1] = HeartbeatRegister;     // D101 heartbeat.
        registers[6] = 0x1234;                // D106 watchdog echo.
        registers[10] = 0x0005;               // D110 fault code.

        var values = RegisterDecoder.DecodeFast(registers);

        Assert.Equal(HeartbeatRegister, Word(values, "D101"));
        Assert.Equal((ushort)0x1234, Word(values, "D106"));
        Assert.Equal((ushort)0x0005, Word(values, "D110"));
    }

    [Fact]
    public void DecodeProcess_ComposesD207D208_LowWordFirst()
    {
        var registers = new ushort[14];
        registers[7] = 0x1234; // D207 low word.
        registers[8] = 0xABCD; // D208 high word.

        var values = RegisterDecoder.DecodeProcess(registers);

        // Low word occupies the least-significant 16 bits: (0xABCD << 16) | 0x1234.
        Assert.Equal(0xABCD1234u, Dword(values, "D207.D208"));
    }

    [Fact]
    public void DecodeProcess_ComposesD212D213_LowWordFirst()
    {
        var registers = new ushort[14];
        registers[12] = 0x0005; // D212 low word.
        registers[13] = 0x0001; // D213 high word.

        var values = RegisterDecoder.DecodeProcess(registers);

        Assert.Equal(0x10005u, Dword(values, "D212.D213"));
    }

    [Fact]
    public void DecodeProcess_DecodesScalars_AsUshort()
    {
        var registers = new ushort[14];
        registers[0] = 3;       // D200 step number.
        registers[1] = 1000;    // D201 tuning speed.
        registers[2] = 250;     // D202 target width.
        registers[3] = 240;     // D203 current width.
        registers[4] = 50;      // D204 pulse equivalent.
        registers[5] = 30;      // D205 belt speed.
        registers[10] = 0x0002; // D210 tuning difference.

        var values = RegisterDecoder.DecodeProcess(registers);

        Assert.Equal((ushort)3, Word(values, "D200"));
        Assert.Equal((ushort)1000, Word(values, "D201"));
        Assert.Equal((ushort)250, Word(values, "D202"));
        Assert.Equal((ushort)240, Word(values, "D203"));
        Assert.Equal((ushort)50, Word(values, "D204"));
        Assert.Equal((ushort)30, Word(values, "D205"));
        Assert.Equal((ushort)0x0002, Word(values, "D210"));
    }

    [Fact]
    public void DecodeFast_MissingRegisters_DecodesWhatIsPresent()
    {
        // Only D100..D106 are present (indices 0-6); D107-D110 are missing.
        var registers = new ushort[7];
        registers[0] = 0x0001; // D100 → M0.
        registers[1] = 0x0002; // D101.
        registers[5] = 0x0000; // D105 present → M316 false.
        registers[6] = 0x0003; // D106.

        var values = RegisterDecoder.DecodeFast(registers);

        Assert.True(Bit(values, "M0"));
        Assert.Equal((ushort)0x0002, Word(values, "D101"));
        Assert.False(Bit(values, "M316"));
        Assert.Equal((ushort)0x0003, Word(values, "D106"));
        Assert.False(values.ContainsKey("D110")); // absent register is omitted, not a crash.
    }

    [Fact]
    public void DecodeProcess_ComposesOnlyWhenBothWordsPresent()
    {
        // Only D207 is present (index 7); D208 (index 8) is out of the block, so no composite.
        var truncated = new ushort[8];
        truncated[7] = 0x0055;

        var values = RegisterDecoder.DecodeProcess(truncated);

        Assert.False(values.ContainsKey("D207.D208"));

        // Both words present → composite emitted.
        var full = new ushort[14];
        full[7] = 0x0055;
        full[8] = 0x00AA;
        Assert.Equal(0x00AA0055u, Dword(RegisterDecoder.DecodeProcess(full), "D207.D208"));
    }

    [Fact]
    public void DecodeProcess_MissingRegisters_DecodesWhatIsPresent()
    {
        // Indices 0-7 present (D200-D207); index 8 (D208) and above are missing.
        var registers = new ushort[8];
        registers[0] = 7;         // D200.
        registers[7] = 0x0055;    // D207 low word present, but D208 high word missing.

        var values = RegisterDecoder.DecodeProcess(registers);

        Assert.Equal((ushort)7, Word(values, "D200"));
        Assert.False(values.ContainsKey("D207.D208")); // only one of the two words present.
        Assert.False(values.ContainsKey("D210"));
        Assert.False(values.ContainsKey("D212.D213"));
    }

    [Fact]
    public void Decode_NullRegisters_ReturnsEmpty()
    {
        Assert.Empty(RegisterDecoder.DecodeFast(null));
        Assert.Empty(RegisterDecoder.DecodeProcess(null));
    }

    private static bool Bit(IReadOnlyDictionary<string, object?> values, string key) => (bool)values[key]!;

    private static ushort Word(IReadOnlyDictionary<string, object?> values, string key) => (ushort)values[key]!;

    private static uint Dword(IReadOnlyDictionary<string, object?> values, string key) => (uint)values[key]!;
}
