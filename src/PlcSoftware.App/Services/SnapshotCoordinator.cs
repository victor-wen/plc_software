using PlcSoftware.Core.Services;

namespace PlcSoftware.App.Services;

/// <summary>
/// Coordinator that turns <see cref="PollingService.ResultAvailable"/> into a single coherent
/// <see cref="Models.DeviceSnapshot"/> (Review Gate 4). The fast block is decoded with
/// <see cref="RegisterDecoder.DecodeFast"/>, the process block with
/// <see cref="RegisterDecoder.DecodeProcess"/>, and the two partial dictionaries are merged and
/// published atomically through <see cref="SnapshotMerger"/>.
///
/// <para>Bit groups (Io / Io.Y) carry no decoder and are ignored here; the supervisory snapshot only
/// carries the decoded D-register / M-bit values, which is what the UI and the gate read.</para>
/// </summary>
internal sealed class SnapshotCoordinator
{
    private readonly SnapshotMerger _merger;
    private IReadOnlyDictionary<string, object?> _fast = new Dictionary<string, object?>();
    private IReadOnlyDictionary<string, object?> _process = new Dictionary<string, object?>();

    public SnapshotCoordinator(SnapshotMerger merger, PollingService polling)
    {
        _merger = merger ?? throw new ArgumentNullException(nameof(merger));
        polling.ResultAvailable += OnResult;
    }

    private void OnResult(PollingResult result)
    {
        if (result.Group.Area != PollingArea.HoldingRegisters)
        {
            return;
        }

        if (result.Group.Name == "Fast")
        {
            _fast = RegisterDecoder.DecodeFast(result.Registers);
        }
        else if (result.Group.Name == "Process")
        {
            _process = RegisterDecoder.DecodeProcess(result.Registers);
        }
        else
        {
            return;
        }

        _merger.Publish(_fast, _process, DateTime.UtcNow);
    }
}
