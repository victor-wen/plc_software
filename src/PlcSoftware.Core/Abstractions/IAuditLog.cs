namespace PlcSoftware.Core.Abstractions;

/// <summary>
/// A host write is audited by category. The categories mirror design §4.3/§4.4/§6.5: 屏蔽 writes
/// (M110 光栅屏蔽 / M111 门磁屏蔽), 参数 writes (D201/D202/D204/D205) and 调试 writes (the diagnostic
/// terminal). A future service (the diagnostic terminal) can record the <see cref="Debug"/> category
/// without changing this contract.
/// </summary>
public enum AuditCategory
{
    /// <summary>屏蔽 (bypass) writes — M110 光栅屏蔽 / M111 门磁屏蔽.</summary>
    Mask,

    /// <summary>Parameter register writes — D201/D202/D204/D205 (design §4.3).</summary>
    Parameter,

    /// <summary>调试 (debug) writes from the diagnostic terminal (design §6.5).</summary>
    Debug,
}

/// <summary>
/// One recorded host write. <see cref="Target"/> is the logical point name (e.g. "M110", "D201"); when
/// the written value is representable it is carried in <see cref="Value"/>, and <see cref="Message"/>
/// carries an optional reason. The recording implementation is responsible for timestamping / persisting
/// the event, so this contract carries no clock. Producers must not let a recording failure affect the
/// primary write outcome.
/// </summary>
public sealed record AuditEvent(
    AuditCategory Category,
    string Target,
    object? Value,
    string? Message = null);

/// <summary>
/// Receives audit events for the host write surface (屏蔽/参数/调试, design audit). Implementations are
/// expected never to throw: a failed record must not turn an already-committed PLC write into a "failure".
/// </summary>
public interface IAuditLog
{
    /// <summary>Records one host write event. Must not throw.</summary>
    void Record(AuditEvent auditEvent);
}
