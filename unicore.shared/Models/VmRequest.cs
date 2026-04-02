using Google.Cloud.Firestore;

namespace UniCore.Shared.Models;

[FirestoreData]
public class VmRequest
{
    [FirestoreProperty("request_id")]
    public string RequestId { get; set; } = "";

    [FirestoreProperty("consumer_uid")]
    public string ConsumerUid { get; set; } = "";

    [FirestoreProperty("provider_uid")]
    public string ProviderUid { get; set; } = "";

    /// <summary>pending → processing → completed | failed</summary>
    [FirestoreProperty("status")]
    public string Status { get; set; } = "pending";

    [FirestoreProperty("vm_name")]
    public string VmName { get; set; } = "";

    [FirestoreProperty("image")]
    public string Image { get; set; } = "ubuntu:22.04";

    [FirestoreProperty("cpu_cores")]
    public int CpuCores { get; set; } = 2;

    [FirestoreProperty("ram_gb")]
    public int RamGb { get; set; } = 4;

    [FirestoreProperty("volume_gb")]
    public int? VolumeGb { get; set; }

    /// <summary>Filled by provider on success.</summary>
    [FirestoreProperty("vm_id")]
    public string VmId { get; set; } = "";

    /// <summary>Whether the consumer wants port 8080 exposed publicly via HTTPS subdomain.</summary>
    [FirestoreProperty("expose_service")]
    public bool ExposeService { get; set; } = false;

    /// <summary>Filled by provider on failure.</summary>
    [FirestoreProperty("error")]
    public string Error { get; set; } = "";
}
