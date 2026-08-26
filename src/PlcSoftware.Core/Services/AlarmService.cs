namespace PlcSoftware.Core.Services;

using PlcSoftware.Core.Models;

/// <summary>
/// Translates the PLC fault code (D110) into K1-K7 alarm start/recovery events, deduplicating so the
/// same code never raises a duplicate alarm.
///
/// <para><b>Dedup.</b> Observing the same D110 value repeatedly raises nothing. Only a <em>change</em>
/// of code is acted on: a non-zero code raises <see cref="AlarmStarted"/> once and becomes the active
/// alarm; a different non-zero code recovers the previous alarm and starts the new one.</para>
///
/// <para><b>Recovery.</b> A code returning to 0 closes the active alarm (raises
/// <see cref="AlarmRecovered"/> once) and leaves the service with no active alarm.</para>
///
/// <para><b>Unknown codes.</b> Only codes present in the supplied <see cref="FaultDefinition"/> list
/// (K1-K7) are treated as alarms. An undefined non-zero code is a garbage sample: it raises
/// <strong>no</strong> event and leaves the current state untouched (the active alarm, if any, stays
/// active). Only a return to 0 closes the active alarm, so a single stale/garbage sample can never
/// clear a real safety alarm and then re-raise it on the next good sample (churn).</para>
/// </summary>
public sealed class AlarmService
{
    private readonly IReadOnlyDictionary<int, FaultDefinition> _defs;
    private int _activeCode;

    /// <summary>Builds the service from the loaded K1-K7 fault definitions.</summary>
    public AlarmService(IReadOnlyList<FaultDefinition> faults)
    {
        if (faults is null)
        {
            throw new ArgumentNullException(nameof(faults));
        }

        _defs = faults.ToDictionary(f => f.Code);
    }

    /// <summary>The currently active fault code; 0 means no active alarm.</summary>
    public int ActiveCode => _activeCode;

    /// <summary>Raised when a (K1-K7) fault code becomes active.</summary>
    public event Action<FaultDefinition>? AlarmStarted;

    /// <summary>Raised when the active alarm is cleared (D110 returned to 0, or the code changed).</summary>
    public event Action<FaultDefinition>? AlarmRecovered;

    /// <summary>
    /// Feeds one D110 fault-code observation. Repeated identical values are ignored; a change of code
    /// recovers the previous alarm and starts the new one; a return to 0 closes the active alarm. An
    /// undefined non-zero code keeps the current state (no recovered event, no state change).
    /// </summary>
    public void Observe(int code)
    {
        // Dedup: the same D110 value must not raise a duplicate alarm.
        if (code == _activeCode)
        {
            return;
        }

        // An undefined non-zero code is a garbage sample: treat it as no observation and keep the
        // current state (no recovered event, no state change). Otherwise a single undefined sample would
        // clear a real safety alarm and then re-raise it on the next good sample — churn.
        if (code != 0 && !_defs.ContainsKey(code))
        {
            return;
        }

        // A change of code closes the previously active alarm first (code == 0 or a known fault).
        if (_defs.TryGetValue(_activeCode, out var previous))
        {
            AlarmRecovered?.Invoke(previous);
        }

        if (code != 0 && _defs.TryGetValue(code, out var definition))
        {
            _activeCode = code;
            AlarmStarted?.Invoke(definition);
        }
        else
        {
            // code == 0 (no fault): no active alarm.
            _activeCode = 0;
        }
    }
}
