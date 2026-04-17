using Google.Cloud.Firestore;
using UniCore.Shared.Models;

namespace unicoreprovider.Services;

public class AuditService : IAuditService
{
    private readonly FirestoreDb _firestoreDb;
    private readonly ILogger<AuditService> _logger;

    public AuditService(FirestoreDb firestoreDb, ILogger<AuditService> logger)
    {
        _firestoreDb = firestoreDb;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Log(string providerUid, string action, string? vmId = null,
                    string? consumerUid = null, string? detail = null)
    {
        // Fire-and-forget — Firestore writes never block the calling operation.
        // A failed write logs a warning but never surfaces to the caller.
        _ = Task.Run(async () =>
        {
            try
            {
                var entry = new AuditLog
                {
                    ProviderUid   = providerUid,
                    Action        = action,
                    VmId          = vmId,
                    ConsumerUid   = consumerUid,
                    Detail        = detail,
                    Timestamp     = DateTime.UtcNow,
                };
                await _firestoreDb.Collection("provider_audit_logs").AddAsync(entry);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Audit] Failed to write log for action {Action} by provider {Provider}",
                    action, providerUid);
            }
        });
    }
}
