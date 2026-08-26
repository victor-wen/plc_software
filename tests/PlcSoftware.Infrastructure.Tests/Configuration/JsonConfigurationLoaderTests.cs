using PlcSoftware.Core.Services;
using PlcSoftware.Infrastructure.Configuration;

namespace PlcSoftware.Infrastructure.Tests.Configuration;

public class JsonConfigurationLoaderTests
{
    private readonly JsonConfigurationLoader _loader = new();

    private static string ConfigPath(string name)
        => Path.Combine(AppContext.BaseDirectory, "config", name);

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
}
