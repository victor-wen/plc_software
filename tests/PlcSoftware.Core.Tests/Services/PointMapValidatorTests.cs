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
        Assert.Contains(errors, e => e.Contains("D213", StringComparison.OrdinalIgnoreCase));
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
            Point("D105.bit0", 10),
            Point("D106", 11, writable: true),
            Point("D213", 12),
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
}
