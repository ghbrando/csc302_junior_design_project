using Google.Cloud.Firestore;

namespace unicoreprovider.Models;

[FirestoreData]
public class MachineSpecs
{
    [FirestoreProperty("provider_id")]
    public string ProviderId { get; set; } = string.Empty;

    // CPU
    [FirestoreProperty("cpu_name")]
    public string CpuName { get; set; } = string.Empty;

    [FirestoreProperty("cpu_cores")]
    public int CpuCores { get; set; }

    [FirestoreProperty("cpu_threads")]
    public int CpuThreads { get; set; }

    [FirestoreProperty("cpu_clock_speed_ghz")]
    public double CpuClockSpeedGHz { get; set; }

    // RAM
    [FirestoreProperty("ram_gb")]
    public int RamGB { get; set; }

    // GPU
    [FirestoreProperty("gpu_name")]
    public string GpuName { get; set; } = string.Empty;

    [FirestoreProperty("gpu_cores")]
    public int GpuCores { get; set; }

    [FirestoreProperty("vram_gb")]
    public int VramGB { get; set; }

    // Storage
    [FirestoreProperty("disk_gb")]
    public int DiskGB { get; set; }

    // OS
    [FirestoreProperty("os_name")]
    public string OsName { get; set; } = string.Empty;

    [FirestoreProperty("last_detected")]
    public DateTime LastDetected { get; set; } = DateTime.UtcNow;
}
