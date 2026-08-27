using Microsoft.Extensions.Logging;
using PlcSoftware.Core.Abstractions;

namespace PlcSoftware.App.Services;

/// <summary>
/// App-level <see cref="IAuditLog"/> that is a no-op until a logging backend is wired: it forwards each
/// 屏蔽/参数/调试 audit event to <see cref="ILogger"/> (which is a <c>NullLogger</c> until providers are
/// registered). The Core audit contract guarantees the producer never throws, so this observer only
/// logs and never affects a write outcome.
/// </summary>
internal sealed class ConsoleAuditLog : IAuditLog
{
    private readonly ILogger<ConsoleAuditLog> _logger;

    public ConsoleAuditLog(ILogger<ConsoleAuditLog> logger)
    {
        _logger = logger;
    }

    public void Record(AuditEvent auditEvent)
        => _logger.LogInformation("Audit {Category}: {Target} = {Value} {Message}",
            auditEvent.Category, auditEvent.Target, auditEvent.Value, auditEvent.Message);
}
