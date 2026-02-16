using Google.Cloud.Firestore;

namespace unicoreprovider.Models;

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

    // Computed property to get uptime as TimeSpan
    [FirestoreProperty]
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
}