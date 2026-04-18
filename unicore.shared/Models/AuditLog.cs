using Google.Cloud.Firestore;

namespace UniCore.Shared.Models;

[FirestoreData]
public class AuditLog
{
    [FirestoreProperty("provider_uid")]
    public string ProviderUid { get; set; } = string.Empty;

    [FirestoreProperty("action")]
    public string Action { get; set; } = string.Empty;

    [FirestoreProperty("vm_id")]
    public string? VmId { get; set; }

    [FirestoreProperty("consumer_uid")]
    public string? ConsumerUid { get; set; }

    [FirestoreProperty("detail")]
    public string? Detail { get; set; }

    [FirestoreProperty("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
