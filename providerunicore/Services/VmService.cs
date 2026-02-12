using unicoreprovider.Models;

namespace unicoreprovider.Services;

public class VmService
{
    private List<VirtualMachine> _dummyVms;

    public VmService()
    {
        _dummyVms = new List<VirtualMachine>
        {
            new VirtualMachine 
            { 
                VmId = "1", 
                Name = "Deep Learning Rig", 
                Status = "Running", 
                CpuCores = 8, 
                RamGB = 16, 
                CostPerHour = 0.35m, 
                CurrentSessionCost = 1.20m,
                CurrentCpuUsage = 45,
                CurrentGpuUsage = 88,
                CurrentRamUsage = 60
            },
            new VirtualMachine 
            { 
                VmId = "2", 
                Name = "Web Server", 
                Status = "Running", 
                CpuCores = 2, 
                RamGB = 4, 
                CostPerHour = 0.05m, 
                CurrentSessionCost = 0.15m,
                CurrentCpuUsage = 12,
                CurrentGpuUsage = 0,
                CurrentRamUsage = 35
            },
            new VirtualMachine 
            { 
                VmId = "3", 
                Name = "Render Node", 
                Status = "Stopped", 
                CpuCores = 12, 
                RamGB = 32, 
                CostPerHour = 0.50m, 
                CurrentSessionCost = 0.00m,
                CurrentCpuUsage = 0,
                CurrentGpuUsage = 0,
                CurrentRamUsage = 0
            }
        };
    }

    public Task<List<VirtualMachine>> GetMyVmsAsync()
    {
        // Simulate "Live" data by slightly randomizing the metrics on every fetch
        var rng = new Random();
        foreach (var vm in _dummyVms.Where(v => v.Status == "Running"))
        {
            vm.CurrentCpuUsage = Math.Clamp(vm.CurrentCpuUsage + rng.Next(-10, 10), 0, 100);
            vm.CurrentGpuUsage = Math.Clamp(vm.CurrentGpuUsage + rng.Next(-10, 10), 0, 100);
            vm.CurrentRamUsage = Math.Clamp(vm.CurrentRamUsage + rng.Next(-5, 5), 0, 100);

            vm.CpuHistory.Add((double)vm.CurrentCpuUsage);
            if (vm.CpuHistory.Count > 20) vm.CpuHistory.RemoveAt(0);

            vm.GpuHistory.Add((double)vm.CurrentGpuUsage);
            if (vm.GpuHistory.Count > 20) vm.GpuHistory.RemoveAt(0);

            vm.RamHistory.Add((double)vm.CurrentRamUsage);
            if (vm.RamHistory.Count > 20) vm.RamHistory.RemoveAt(0);
        }

        return Task.FromResult(_dummyVms);
    }
}