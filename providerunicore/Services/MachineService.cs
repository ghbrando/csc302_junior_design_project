using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using providerunicore.Repositories;
using unicoreprovider.Models;

namespace unicoreprovider.Services;

public class MachineService : IMachineService
{
    private readonly IFirestoreRepository<MachineSpecs> _repository;
    private readonly IAuthStateService _authState;

    public MachineService(IFirestoreRepository<MachineSpecs> repository, IAuthStateService authState)
    {
        _repository = repository;
        _authState = authState;
    }

    public async Task<MachineSpecs> GetSpecsAsync()
    {
        var uid = _authState.FirebaseUid;
        if (string.IsNullOrEmpty(uid))
            throw new InvalidOperationException("Cannot detect machine specs: no authenticated provider.");

        var specs = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? DetectWindows()
            : DetectLinux();

        specs.ProviderId = uid;
        specs.LastDetected = DateTime.UtcNow;

        await _repository.UpdateAsync(specs.ProviderId, specs);
        return specs;
    }

    public async Task<MachineSpecs?> GetCachedSpecsAsync()
    {
        var uid = _authState.FirebaseUid;
        if (string.IsNullOrEmpty(uid)) return null;
        return await _repository.GetByIdAsync(uid);
    }

    // ==========================================
    // Windows Detection (WMI)
    // ==========================================

    [SupportedOSPlatform("windows")]
    private static MachineSpecs DetectWindows()
    {
        var specs = new MachineSpecs();
        DetectCpuWindows(specs);
        DetectRamWindows(specs);
        DetectGpuWindows(specs);
        DetectDiskWindows(specs);
        DetectOsWindows(specs);
        return specs;
    }

    [SupportedOSPlatform("windows")]
    private static void DetectCpuWindows(MachineSpecs specs)
    {
        using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Processor");
        foreach (ManagementObject obj in searcher.Get())
        {
            specs.CpuName = obj["Name"]?.ToString()?.Trim() ?? string.Empty;
            specs.CpuCores = Convert.ToInt32(obj["NumberOfCores"] ?? 0);
            specs.CpuThreads = Convert.ToInt32(obj["NumberOfLogicalProcessors"] ?? 0);

            // MaxClockSpeed is in MHz
            double mhz = Convert.ToDouble(obj["MaxClockSpeed"] ?? 0);
            specs.CpuClockSpeedGHz = Math.Round(mhz / 1000.0, 2);
            break; // Use first CPU
        }
    }

    [SupportedOSPlatform("windows")]
    private static void DetectRamWindows(MachineSpecs specs)
    {
        long totalBytes = 0;
        using var searcher = new ManagementObjectSearcher("SELECT Capacity FROM Win32_PhysicalMemory");
        foreach (ManagementObject obj in searcher.Get())
        {
            totalBytes += Convert.ToInt64(obj["Capacity"] ?? 0);
        }
        specs.RamGB = (int)(totalBytes / (1024L * 1024 * 1024));
    }

    [SupportedOSPlatform("windows")]
    private static void DetectGpuWindows(MachineSpecs specs)
    {
        using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_VideoController");
        foreach (ManagementObject obj in searcher.Get())
        {
            specs.GpuName = obj["Name"]?.ToString()?.Trim() ?? string.Empty;

            // AdapterRAM is in bytes (WMI caps at ~4GB for legacy 32-bit field)
            long vramBytes = Convert.ToInt64(obj["AdapterRAM"] ?? 0);
            specs.VramGB = (int)(vramBytes / (1024L * 1024 * 1024));

            // GpuCores are not exposed by WMI — left at 0
            specs.GpuCores = 0;
            break; // Use primary GPU
        }
    }

    [SupportedOSPlatform("windows")]
    private static void DetectDiskWindows(MachineSpecs specs)
    {
        long totalBytes = 0;
        using var searcher = new ManagementObjectSearcher("SELECT Size FROM Win32_DiskDrive");
        foreach (ManagementObject obj in searcher.Get())
        {
            totalBytes += Convert.ToInt64(obj["Size"] ?? 0);
        }
        specs.DiskGB = (int)(totalBytes / (1024L * 1024 * 1024));
    }

    [SupportedOSPlatform("windows")]
    private static void DetectOsWindows(MachineSpecs specs)
    {
        using var searcher = new ManagementObjectSearcher("SELECT Caption FROM Win32_OperatingSystem");
        foreach (ManagementObject obj in searcher.Get())
        {
            specs.OsName = obj["Caption"]?.ToString()?.Trim() ?? RuntimeInformation.OSDescription;
            break;
        }
    }

    // ==========================================
    // Linux Detection (/proc)
    // ==========================================

    private static MachineSpecs DetectLinux()
    {
        var specs = new MachineSpecs
        {
            OsName = RuntimeInformation.OSDescription
        };

        try { DetectCpuLinux(specs); } catch { /* best-effort */ }
        try { DetectRamLinux(specs); } catch { /* best-effort */ }
        try { DetectDiskLinux(specs); } catch { /* best-effort */ }

        return specs;
    }

    private static void DetectCpuLinux(MachineSpecs specs)
    {
        if (!File.Exists("/proc/cpuinfo")) return;

        var lines = File.ReadAllLines("/proc/cpuinfo");
        int coreCount = 0;
        double maxMhz = 0;

        foreach (var line in lines)
        {
            if (line.StartsWith("model name", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(specs.CpuName))
                specs.CpuName = line.Split(':').LastOrDefault()?.Trim() ?? string.Empty;

            if (line.StartsWith("processor", StringComparison.OrdinalIgnoreCase))
                coreCount++;

            if (line.StartsWith("cpu MHz", StringComparison.OrdinalIgnoreCase))
            {
                if (double.TryParse(line.Split(':').LastOrDefault()?.Trim(), out double mhz))
                    maxMhz = Math.Max(maxMhz, mhz);
            }
        }

        specs.CpuCores = coreCount;
        specs.CpuThreads = coreCount;
        specs.CpuClockSpeedGHz = Math.Round(maxMhz / 1000.0, 2);
    }

    private static void DetectRamLinux(MachineSpecs specs)
    {
        if (!File.Exists("/proc/meminfo")) return;

        foreach (var line in File.ReadAllLines("/proc/meminfo"))
        {
            if (line.StartsWith("MemTotal:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && long.TryParse(parts[1], out long kb))
                    specs.RamGB = (int)(kb / (1024 * 1024));
                break;
            }
        }
    }

    private static void DetectDiskLinux(MachineSpecs specs)
    {
        long totalBytes = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Sum(d => d.TotalSize);

        specs.DiskGB = (int)(totalBytes / (1024L * 1024 * 1024));
    }
}
