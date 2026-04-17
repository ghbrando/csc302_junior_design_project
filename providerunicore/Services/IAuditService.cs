namespace unicoreprovider.Services;

public interface IAuditService
{
    /// <summary>
    /// Writes an audit entry for a provider action. Fire-and-forget — never
    /// blocks the caller. All parameters except providerUid and action are optional.
    /// </summary>
    void Log(string providerUid, string action, string? vmId = null,
             string? consumerUid = null, string? detail = null);
}
