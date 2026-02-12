namespace unicoreprovider.Models;

public class VirtualMachine
{
    public string VmId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = "Stopped";
    
    // Hardware Specs
    public int CpuCores { get; set; }
    public int RamGB { get; set; }
    
    // Financials
    public decimal CostPerHour { get; set; }
    public decimal CurrentSessionCost { get; set; }

    // Live Metrics (0.0 to 100.0)
    public decimal CurrentCpuUsage { get; set; } 
    public decimal CurrentGpuUsage { get; set; }
    public decimal CurrentRamUsage { get; set; }
    
    // Metric History
    public List<double> CpuHistory { get; set; } = new();
    public List<double> GpuHistory { get; set; } = new();
    public List<double> RamHistory { get; set; } = new();
}