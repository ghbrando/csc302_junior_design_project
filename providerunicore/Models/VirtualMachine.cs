namespace unicoreprovider.Models;

public class VirtualMachine
{
    public string VmId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Client { get; set; } = "Unknown";
    public string Status { get; set; } = "Stopped";
    public TimeSpan Uptime { get; set; } 
    
    // Hardware Specs
    public int CpuCores { get; set; }
    public int RamGB { get; set; }
    
    // Financials
    public decimal CostPerHour { get; set; }
    public decimal CurrentSessionCost { get; set; }

    // Live Metrics
    public decimal CurrentCpuUsage { get; set; } 
    public decimal CurrentGpuUsage { get; set; }
    public decimal CurrentRamUsage { get; set; }
    
    // History
    public List<double> CpuHistory { get; set; } = new();
    public List<double> GpuHistory { get; set; } = new();
    public List<double> RamHistory { get; set; } = new();
}