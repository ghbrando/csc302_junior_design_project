using Google.Cloud.Firestore;
namespace UniCore.Shared.Models;

[FirestoreData]
public class VmMigrationRequest
{
    [FirestoreProperty("migration_request_id")]
    public string MigrationRequestId { get; set; } = String.Empty;

    [FirestoreProperty("vm_id")]
    public string VmId { get; set; } = String.Empty;

    [FirestoreProperty("consumer_uid")]
    public string ConsumerUid { get; set; } = String.Empty;

    [FirestoreProperty("source_provider_uid")]
    public string SourceProviderUid { get; set; } = String.Empty;

    [FirestoreProperty("target_provider_uid")]
    public string TargetProviderUid { get; set; } = String.Empty;

    [FirestoreProperty("status")]
    public string Status { get; set; } = "pending"; // "pending", "restoring", "Completed", "Failed"

    [FirestoreProperty("requested_at")]
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

    [FirestoreProperty("completed_at")]
    public DateTime? CompletedAt { get; set; }

    [FirestoreProperty("error")]
    public string? Error { get; set; }

    [FirestoreProperty("new_vm_id")]
    public string? NewVmId { get; set; } // The VM ID on the new provider after successful migration

    [FirestoreProperty("requested_cpu_cores")]
    public int? RequestedCpuCores { get; set; }
    [FirestoreProperty("requested_ram_gb")]
    public int? RequestedRamGb { get; set; }

    [FirestoreProperty("effective_cpu_cores")]
    public int EffectiveCpuCores { get; set; }
    [FirestoreProperty("effective_ram_gb")]
    public int EffectiveRamGb { get; set; }
}
