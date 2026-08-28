using PlcSoftware.Core.Models;
using PlcSoftware.Core.Services;

namespace PlcSoftware.Core.Tests.Services;

public class PointMapValidatorTests
{
    private static PointDefinition Point(string address, ushort protocol, bool writable = false)
        => new() { Name = address, Address = address, ProtocolAddress = protocol, IsWritable = writable };

    [Fact]
    public void DuplicateLogicalAddress_ProducesError()
    {
        var points = new List<PointDefinition>
        {
            Point("X0", 0),
            Point("X0", 1),
        };

        var errors = PointMapValidator.Validate(points);

        Assert.Contains(errors, e => e.Contains("X0", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DuplicateProtocolAddress_ProducesError()
    {
        var points = new List<PointDefinition>
        {
            Point("X0", 0),
            Point("X10", 0),
        };

        var errors = PointMapValidator.Validate(points);

        Assert.Contains(errors, e => e.Contains("protocol", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("D105.bit16")]
    [InlineData("D105.bit-1")]
    [InlineData("D105.bit")]
    [InlineData("D105.bita")]
    [InlineData("D105.bit1.5")]
    public void IllegalBitIndex_ProducesError(string address)
    {
        var points = new List<PointDefinition> { Point(address, 0) };

        var errors = PointMapValidator.Validate(points);

        Assert.Contains(errors, e => e.Contains("bit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MissingRequiredPlcPoints_ProducesErrors()
    {
        // A point map without the PLC-new data registers must be rejected.
        var points = new List<PointDefinition>
        {
            Point("X0", 0),
            Point("Y0", 1),
            Point("M100", 2),
        };

        var errors = PointMapValidator.Validate(points);

        Assert.Contains(errors, e => e.Contains("D105", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, e => e.Contains("D106", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidMap_ProducesNoErrors()
    {
        var points = new List<PointDefinition>
        {
            Point("X0", 0),
            Point("X1", 1),
            Point("Y0", 2),
            Point("M100", 3, writable: true),
            Point("D105", 10),
            Point("D106", 11, writable: true),
        };

        Assert.Empty(PointMapValidator.Validate(points));
    }

    [Fact]
    public void EmptyAddress_ProducesError()
    {
        var points = new List<PointDefinition>
        {
            Point("", 0),
            Point("X1", 1),
        };

        var errors = PointMapValidator.Validate(points);

        Assert.Contains(errors, e => e.Contains("address", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Q0")]
    [InlineData("FOO")]
    public void IllegalAreaLetter_ProducesError(string address)
    {
        var points = new List<PointDefinition> { Point(address, 0) };

        var errors = PointMapValidator.Validate(points);

        Assert.Contains(errors, e => e.Contains("area", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("X7.bit1")]
    [InlineData("Y3.bit2")]
    [InlineData("M12.bit3")]
    public void BitNotationOnNonDRegister_ProducesError(string address)
    {
        // Include the required PLC points so the only .bit error is the one under test.
        var points = new List<PointDefinition>
        {
            Point(address, 0),
            Point("D105", 5),
            Point("D106", 6, writable: true),
        };

        var errors = PointMapValidator.Validate(points);

        Assert.Contains(errors, e => e.Contains("only D registers use .bit", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("D105.bit+1")]
    [InlineData("D105.bit1 ")]
    [InlineData("D105.bit 1")]
    public void BitIndexNotStrictlyDigits_ProducesError(string address)
    {
        // Include the required PLC points so the only .bit error is the one under test.
        var points = new List<PointDefinition>
        {
            Point(address, 0),
            Point("D105", 5),
            Point("D106", 6, writable: true),
        };

        var errors = PointMapValidator.Validate(points);

        Assert.Contains(errors, e => e.Contains("Illegal bit index", StringComparison.OrdinalIgnoreCase)
                                     && e.Contains(address, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidDRegisterBit0_ProducesNoErrors()
    {
        var points = new List<PointDefinition>
        {
            Point("D105", 0),
            Point("D106", 1, writable: true),
        };

        Assert.Empty(PointMapValidator.Validate(points));
    }
}
