using Google.Cloud.Firestore;

namespace UniCore.Shared.Models;

[FirestoreData]
public class VirtualMachine
{
    [FirestoreProperty("vm_id")]
    public string VmId { get; set; } = string.Empty;

    [FirestoreProperty("name")]
    public string Name { get; set; } = string.Empty;

    [FirestoreProperty("client")]
    public string Client { get; set; } = "Unknown";

    [FirestoreProperty("status")]
    public string Status { get; set; } = "Stopped";

    [FirestoreProperty("uptime")]
    public string UptimeString { get; set; } = "00:00:00";

    [FirestoreProperty("is_paused")]
    public bool IsPaused { get; set; } = false;

    [FirestoreProperty("resume_success")]
    public bool ResumeSuccess { get; set; } = true;

    // Computed property — not stored in Firestore, derived from UptimeString
    public TimeSpan Uptime
    {
        get
        {
            if (TimeSpan.TryParse(UptimeString, out var timeSpan))
                return timeSpan;
            return TimeSpan.Zero;
        }
    }

    // Hardware Specs
    [FirestoreProperty("cpu_cores")]
    public int CpuCores { get; set; }

    [FirestoreProperty("ram_gb")]
    public int RamGB { get; set; }

    [FirestoreProperty("volume_requested_gb")]
    public int? VolumeRequestedGb { get; set; }

    // Financials
    [FirestoreProperty("cost_per_hour")]
    public double CostPerHour { get; set; }

    [FirestoreProperty("current_session_cost")]
    public double CurrentSessionCost { get; set; }

    // Live Metrics
    [FirestoreProperty("current_cpu_usage")]
    public double CurrentCpuUsage { get; set; }

    [FirestoreProperty("current_gpu_usage")]
    public double CurrentGpuUsage { get; set; }

    [FirestoreProperty("current_ram_usage")]
    public double CurrentRamUsage { get; set; }

    // History
    [FirestoreProperty("cpu_history")]
    public List<double> CpuHistory { get; set; } = new();

    [FirestoreProperty("gpu_history")]
    public List<double> GpuHistory { get; set; } = new();

    [FirestoreProperty("ram_history")]
    public List<double> RamHistory { get; set; } = new();

    [FirestoreProperty("providerId")]
    public string ProviderId { get; set; } = string.Empty;

    // Docker container info
    [FirestoreProperty("container_id")]
    public string ContainerId { get; set; } = string.Empty;

    [FirestoreProperty("volume_name")]
    public string? VolumeName { get; set; }

    [FirestoreProperty("started_at")]
    public DateTime? StartedAt { get; set; }

    [FirestoreProperty("image")]
    public string Image { get; set; } = string.Empty;

    // SSH access info
    [FirestoreProperty("ssh_port")]
    public int? SshPort { get; set; }

    // FRP relay port — the remotePort registered on the GCP relay VM for this container's SSH tunnel
    [FirestoreProperty("relay_port")]
    public int? RelayPort { get; set; }

    // Tracks how many consecutive heartbeat cycles this VM has failed to respond
    [FirestoreProperty("consecutive_misses")]
    public int ConsecutiveMisses { get; set; } = 0;

    [FirestoreProperty("gcs_bucket")]
    public string? GcsBucket { get; set; }

    [FirestoreProperty("gcs_path")]
    public string? GcsPath { get; set; }

    [FirestoreProperty("last_volume_sync_at")]
    public DateTime? LastVolumeSyncAt { get; set; }

    [FirestoreProperty("volume_sync_status")]
    public string? VolumeSyncStatus { get; set; }

    [FirestoreProperty("snapshot_image")]
    public string? SnapshotImage { get; set; }

    [FirestoreProperty("last_snapshot_at")]
    public DateTime? LastSnapshotAt { get; set; }

    [FirestoreProperty("snapshot_status")]
    public string? SnapshotStatus { get; set; }

    [FirestoreProperty("migration_status")]
    public string? MigrationStatus { get; set; }

    [FirestoreProperty("migration_requested_at")]
    public DateTime? MigrationRequestedAt { get; set; }

    [FirestoreProperty("migration_error")]
    public string? MigrationError { get; set; }

    [FirestoreProperty("original_vm_id")]
    public string? OriginalVmId { get; set; }
}
