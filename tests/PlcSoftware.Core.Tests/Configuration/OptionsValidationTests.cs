using PlcSoftware.Core.Configuration;
using PlcSoftware.Core.Models;

namespace PlcSoftware.Core.Tests.Configuration;

public class OptionsValidationTests
{
    [Fact]
    public void SerialConnectionOptions_Valid_ProducesNoErrors()
    {
        var options = new SerialConnectionOptions
        {
            PortName = "COM1",
            BaudRate = 9600,
            DataBits = 8,
            SlaveId = 1,
            TimeoutMs = 1000,
            Retries = 3,
        };

        Assert.Empty(options.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(248)]
    [InlineData(255)]
    public void SerialConnectionOptions_SlaveIdOutOfRange_ProducesError(int slaveId)
    {
        var options = new SerialConnectionOptions { SlaveId = (byte)slaveId };

        Assert.Contains("slaveId", string.Join(" ", options.Validate()), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(247)]
    public void SerialConnectionOptions_SlaveIdInRtuRange_ProducesNoErrors(int slaveId)
    {
        var options = new SerialConnectionOptions { SlaveId = (byte)slaveId };

        Assert.Empty(options.Validate());
    }

    [Fact]
    public void SerialConnectionOptions_NegativeTimeout_ProducesError()
    {
        var options = new SerialConnectionOptions { TimeoutMs = -1 };

        Assert.Contains("timeout", string.Join(" ", options.Validate()), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PollingOptions_NonPositiveInterval_ProducesError()
    {
        var options = new PollingOptions { FastIntervalMs = 0 };

        Assert.Contains("interval", string.Join(" ", options.Validate()), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PollingOptions_Valid_ProducesNoErrors()
    {
        var options = new PollingOptions
        {
            FastIntervalMs = 250,
            ProcessIntervalMs = 500,
            DiagnosticsIntervalMs = 500,
        };

        Assert.Empty(options.Validate());
    }

    [Fact]
    public void HistoryOptions_NonPositiveRetention_ProducesError()
    {
        var options = new HistoryOptions { RetentionDays = 0 };

        Assert.Contains("retention", string.Join(" ", options.Validate()), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HistoryOptions_Valid_ProducesNoErrors()
    {
        var options = new HistoryOptions { RetentionDays = 365 };

        Assert.Empty(options.Validate());
    }

    [Fact]
    public void ParameterDefinition_MinGreaterThanMax_ProducesError()
    {
        var parameter = new ParameterDefinition { Min = 10, Max = 5 };

        Assert.Contains("max", string.Join(" ", parameter.Validate()), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParameterDefinition_EqualOrOrderedBounds_ProducesNoErrors()
    {
        Assert.Empty(new ParameterDefinition { Min = 5, Max = 5 }.Validate());
        Assert.Empty(new ParameterDefinition { Min = 1, Max = 100 }.Validate());
    }
}
