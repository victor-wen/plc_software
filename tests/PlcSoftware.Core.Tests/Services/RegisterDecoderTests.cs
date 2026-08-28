using PlcSoftware.Core.Services;

namespace PlcSoftware.Core.Tests.Services;

/// <summary>
/// Behavioural tests for <see cref="RegisterDecoder"/>: decoding the fast block (D100-D110,
/// protocol offsets 0-10), the process block (D120-D140, offsets 20-40, 21 regs) and the params
/// block (D204-D220, offsets 104-120, 17 regs) into <see cref="Models.DeviceSnapshot.Values"/>.
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
        registers[6] = 0x1234;                // D106 watchdog echo.
        registers[10] = 0x0005;               // D110 fault code.

        var values = RegisterDecoder.DecodeFast(registers);

        Assert.Equal((ushort)0x1234, Word(values, "D106"));
        Assert.Equal((ushort)0x0005, Word(values, "D110"));
        Assert.False(values.ContainsKey("D140")); // heartbeat is in process block, not fast.
    }

    [Fact]
    public void DecodeProcess_DecodesSingleWord_ProductionAndPulse()
    {
        var registers = new ushort[21];
        registers[16] = 0x1234; // D136 width pulse single.
        registers[18] = 0xABCD; // D138 production single.

        var values = RegisterDecoder.DecodeProcess(registers);

        Assert.Equal((ushort)0x1234, Word(values, "D136"));
        Assert.Equal((ushort)0xABCD, Word(values, "D138"));
        Assert.Equal("D138", RegisterDecoder.ProductionCountKey);
        Assert.Equal("D136", RegisterDecoder.WidthPulseCountKey);
    }

    [Fact]
    public void DecodeProcess_DecodesHeartbeat_AtD140()
    {
        var registers = new ushort[21];
        registers[20] = HeartbeatRegister; // D140 heartbeat.

        var values = RegisterDecoder.DecodeProcess(registers);

        Assert.Equal(HeartbeatRegister, Word(values, "D140"));
    }

    [Fact]
    public void DecodeProcess_DecodesScalars_AsUshort()
    {
        var registers = new ushort[21];
        registers[0] = 3;       // D120 step number.
        registers[2] = 30;      // D122 belt speed.
        registers[4] = 55;      // D124 tuning speed setting.
        registers[6] = 1000;    // D126 tuning speed Hz.
        registers[8] = 250;     // D128 target width.
        registers[10] = 240;    // D130 current width.
        registers[16] = 0x0005; // D136 width pulse.
        registers[18] = 0x0011; // D138 production.
        registers[20] = 0x0042; // D140 heartbeat.

        var values = RegisterDecoder.DecodeProcess(registers);

        Assert.Equal((ushort)3, Word(values, "D120"));
        Assert.Equal((ushort)30, Word(values, "D122"));
        Assert.Equal((ushort)55, Word(values, "D124"));
        Assert.Equal((ushort)1000, Word(values, "D126"));
        Assert.Equal((ushort)250, Word(values, "D128"));
        Assert.Equal((ushort)240, Word(values, "D130"));
        Assert.Equal((ushort)0x0005, Word(values, "D136"));
        Assert.Equal((ushort)0x0011, Word(values, "D138"));
        Assert.Equal(HeartbeatRegister, Word(values, "D140"));
    }

    [Fact]
    public void DecodeParams_DecodesScalars_AsUshort()
    {
        var registers = new ushort[17];
        registers[0] = 50;      // D204 pulse equivalent.
        registers[6] = 0x0002; // D210 tuning difference.
        registers[16] = 75;     // D220 tuning speed setting.

        var values = RegisterDecoder.DecodeParams(registers);

        Assert.Equal((ushort)50, Word(values, "D204"));
        Assert.Equal((ushort)0x0002, Word(values, "D210"));
        Assert.Equal((ushort)75, Word(values, "D220"));
    }

    [Fact]
    public void DecodeFast_MissingRegisters_DecodesWhatIsPresent()
    {
        var registers = new ushort[7];
        registers[0] = 0x0001; // D100 → M0.
        registers[5] = 0x0000; // D105 present → M316 false.
        registers[6] = 0x0003; // D106.

        var values = RegisterDecoder.DecodeFast(registers);

        Assert.True(Bit(values, "M0"));
        Assert.False(Bit(values, "M316"));
        Assert.Equal((ushort)0x0003, Word(values, "D106"));
        Assert.False(values.ContainsKey("D110"));
    }

    [Fact]
    public void DecodeProcess_MissingRegisters_DecodesWhatIsPresent()
    {
        var registers = new ushort[8];
        registers[0] = 7;         // D120.
        registers[6] = 1000;    // D126 present.

        var values = RegisterDecoder.DecodeProcess(registers);

        Assert.Equal((ushort)7, Word(values, "D120"));
        Assert.Equal((ushort)1000, Word(values, "D126"));
        Assert.False(values.ContainsKey("D128"));
        Assert.False(values.ContainsKey("D140"));
    }

    [Fact]
    public void Decode_NullRegisters_ReturnsEmpty()
    {
        Assert.Empty(RegisterDecoder.DecodeFast(null));
        Assert.Empty(RegisterDecoder.DecodeProcess(null));
        Assert.Empty(RegisterDecoder.DecodeParams(null));
    }

    [Fact]
    public void CompositeKeys_ArePublicConstants_WithDesignValues()
    {
        Assert.Equal("D138", RegisterDecoder.ProductionCountKey);
        Assert.Equal("D136", RegisterDecoder.WidthPulseCountKey);
        Assert.Equal("D124", RegisterDecoder.TuningSpeedSetting124Key);
        Assert.Equal("D220", RegisterDecoder.TuningSpeedSetting220Key);
    }

    private static bool Bit(IReadOnlyDictionary<string, object?> values, string key) => (bool)values[key]!;

    private static ushort Word(IReadOnlyDictionary<string, object?> values, string key) => (ushort)values[key]!;
}
