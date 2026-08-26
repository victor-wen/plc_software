using PlcSoftware.Core.Services;
using PlcSoftware.Infrastructure.Configuration;

namespace PlcSoftware.Infrastructure.Tests.Configuration;

public class JsonConfigurationLoaderTests
{
    private readonly JsonConfigurationLoader _loader = new();

    private static string ConfigPath(string name)
        => Path.Combine(AppContext.BaseDirectory, "config", name);

    private static string WriteTempJson(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllText(path, content);
        return path;
    }

    private static readonly string[] GivenPointAddresses =
    {
        // X inputs
        "X0", "X1", "X2", "X3", "X4", "X5", "X6", "X7",
        "X10", "X11", "X12", "X13", "X14", "X15", "X16", "X17",
        "X20", "X21", "X22",
        // Y outputs
        "Y0", "Y1", "Y2", "Y3", "Y4", "Y5", "Y6", "Y7",
        "Y10", "Y11", "Y12", "Y13", "Y14", "Y15",
        // D registers
        "D100", "D101", "D102", "D103", "D104", "D105.bit0", "D106", "D110",
        "D200", "D201", "D202", "D203", "D204", "D205", "D207", "D208", "D210", "D212", "D213",
        // M command points (host-written)
        "M100", "M101", "M102", "M103", "M104", "M105", "M106", "M107", "M108", "M109", "M110", "M111",
        // M status points
        "M0", "M1", "M2", "M3", "M4", "M5", "M6", "M7", "M10", "M11", "M12", "M13", "M14",
        "M30", "M31", "M32", "M33", "M34", "M35", "M40", "M41",
        // M flow / field mapping points
        "M200", "M201", "M202", "M203", "M204", "M205",
        "M300", "M301", "M302", "M303", "M304", "M305", "M306", "M307",
        "M310", "M311", "M312", "M313", "M314", "M315", "M316",
    };

    [Fact]
    public void Faults_K1ToK7_Load()
    {
        var faults = _loader.LoadFaults(ConfigPath("faults.json"));

        Assert.Equal(7, faults.Count);
        Assert.Contains(faults, f => f.Code == 1 && f.Message == "急停");
        Assert.Contains(faults, f => f.Code == 2 && f.Message == "安全门打开");
        Assert.Contains(faults, f => f.Code == 3 && f.Message == "安全光栅");
        Assert.Contains(faults, f => f.Code == 4 && f.Message == "气压低");
        Assert.Contains(faults, f => f.Code == 5 && f.Message == "气缸挡停伸出超时");
        Assert.Contains(faults, f => f.Code == 6 && f.Message == "挡停未缩回");
        Assert.Contains(faults, f => f.Code == 7 && f.Message == "扫码超时");
    }

    [Fact]
    public void PointMap_AllGivenPointNames_Load()
    {
        var points = _loader.LoadPointMap(ConfigPath("point-map.simulation.json"));

        var addresses = points.Select(p => p.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var expected in GivenPointAddresses)
        {
            Assert.Contains(expected, addresses);
        }
    }

    [Fact]
    public void PointMap_ConfigSatisfiesValidator()
    {
        var points = _loader.LoadPointMap(ConfigPath("point-map.simulation.json"));

        Assert.Empty(PointMapValidator.Validate(points));
    }

    [Fact]
    public void AppSettings_SerialOptions_Load()
    {
        var options = _loader.LoadSerialOptions(ConfigPath("appsettings.json"));

        Assert.Equal(9600, options.BaudRate);
        Assert.Equal(1, options.SlaveId);
    }

    [Fact]
    public void AppSettings_PollingOptions_Load()
    {
        var options = _loader.LoadPollingOptions(ConfigPath("appsettings.json"));

        Assert.Equal(250, options.FastIntervalMs);
        Assert.Equal(500, options.ProcessIntervalMs);
        Assert.Equal(500, options.DiagnosticsIntervalMs);
    }

    [Fact]
    public void AppSettings_HistoryOptions_Load()
    {
        var options = _loader.LoadHistoryOptions(ConfigPath("appsettings.json"));

        Assert.Equal(365, options.RetentionDays);
    }

    [Theory]
    [InlineData("\"{ not valid json")]
    [InlineData("null")]
    [InlineData("[]")]
    public void LoadFaults_InvalidContent_ThrowsNamingFile(string content)
    {
        var path = WriteTempJson(content);
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => _loader.LoadFaults(path));

            Assert.Contains(path, ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadPointMap_NullContent_ThrowsNamingFile()
    {
        var path = WriteTempJson("null");
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => _loader.LoadPointMap(path));

            Assert.Contains(path, ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadPointMap_CorruptContent_ThrowsNamingFile()
    {
        var path = WriteTempJson("\"{ not valid json");
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => _loader.LoadPointMap(path));

            Assert.Contains(path, ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PointMap_ProtocolOffsets_AreOffsetBasedPerArea()
    {
        var points = _loader.LoadPointMap(ConfigPath("point-map.simulation.json"));
        var byAddress = points.ToDictionary(p => p.Address, StringComparer.OrdinalIgnoreCase);

        // D registers: protocol offset = logical number relative to their block base (D100-D110
        // fast block, D200-D213 process block).
        Assert.Equal(0, byAddress["D100"].ProtocolAddress);
        Assert.Equal(1, byAddress["D101"].ProtocolAddress);
        Assert.Equal(5, byAddress["D105.bit0"].ProtocolAddress);
        Assert.Equal(6, byAddress["D106"].ProtocolAddress);
        Assert.Equal(10, byAddress["D110"].ProtocolAddress);
        Assert.Equal(100, byAddress["D200"].ProtocolAddress);
        Assert.Equal(105, byAddress["D205"].ProtocolAddress);
        Assert.Equal(107, byAddress["D207"].ProtocolAddress);
        Assert.Equal(112, byAddress["D212"].ProtocolAddress);
        Assert.Equal(113, byAddress["D213"].ProtocolAddress);

        // M relays: protocol address == logical number.
        Assert.Equal(0, byAddress["M0"].ProtocolAddress);
        Assert.Equal(14, byAddress["M14"].ProtocolAddress);
        Assert.Equal(100, byAddress["M100"].ProtocolAddress);
        Assert.Equal(111, byAddress["M111"].ProtocolAddress);
        Assert.Equal(200, byAddress["M200"].ProtocolAddress);
        Assert.Equal(316, byAddress["M316"].ProtocolAddress);

        // X/Y are octal-numbered in H3U: logical X10 = protocol 8, X20 = 16, X22 = 18; Y likewise.
        Assert.Equal(8, byAddress["X10"].ProtocolAddress);
        Assert.Equal(16, byAddress["X20"].ProtocolAddress);
        Assert.Equal(18, byAddress["X22"].ProtocolAddress);
        Assert.Equal(8, byAddress["Y10"].ProtocolAddress);
        Assert.Equal(13, byAddress["Y15"].ProtocolAddress);
    }
}
