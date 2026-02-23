using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Google.Cloud.Firestore;
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
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? DetectMac()
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
            if (int.TryParse(obj["NumberOfCores"]?.ToString() ?? "0", out int cores))
                specs.CpuCores = cores;
            if (int.TryParse(obj["NumberOfLogicalProcessors"]?.ToString() ?? "0", out int threads))
                specs.CpuThreads = threads;

            // MaxClockSpeed is in MHz
            if (double.TryParse(obj["MaxClockSpeed"]?.ToString() ?? "0", out double mhz))
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
        specs.RamGB = (int)Math.Min(totalBytes / (1024L * 1024 * 1024), int.MaxValue);
    }

    [SupportedOSPlatform("windows")]
    private static void DetectGpuWindows(MachineSpecs specs)
    {
        // Iterate all adapters and pick the one with the most VRAM so that a
        // dedicated GPU is always preferred over integrated graphics.
        long bestVram = -1;
        using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_VideoController");
        foreach (ManagementObject obj in searcher.Get())
        {
            // AdapterRAM is in bytes (WMI caps at ~4GB for legacy 32-bit field)
            if (!long.TryParse(obj["AdapterRAM"]?.ToString(), out long vramBytes))
                vramBytes = 0;

            if (vramBytes > bestVram)
            {
                bestVram = vramBytes;
                specs.GpuName = obj["Name"]?.ToString()?.Trim() ?? string.Empty;
                specs.VramGB = (int)Math.Min(vramBytes / (1024L * 1024 * 1024), int.MaxValue);
            }
        }

        // GpuCores are not exposed by WMI — left at 0
        specs.GpuCores = 0;
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
        specs.DiskGB = (int)Math.Min(totalBytes / (1024L * 1024 * 1024), int.MaxValue);
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
    // macOS Detection (sysctl & system_profiler)
    // ==========================================

    private static MachineSpecs DetectMac()
    {
        var specs = new MachineSpecs
        {
            OsName = RuntimeInformation.OSDescription
        };

        try { DetectCpuMac(specs); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"CPU detection failed: {ex.Message}"); }
        try { DetectRamMac(specs); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"RAM detection failed: {ex.Message}"); }
        try { DetectGpuMac(specs); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"GPU detection failed: {ex.Message}"); }
        try { DetectDiskMac(specs); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Disk detection failed: {ex.Message}"); }

        return specs;
    }

    private static string ExecuteCommand(string command, string arguments)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    private static void DetectCpuMac(MachineSpecs specs)
    {
        try
        {
            // Get machine model name (e.g., "MacBookPro18,2")
            specs.CpuName = ExecuteCommand("sysctl", "-n hw.model").Trim();

            // Get CPU core count
            string coreOutput = ExecuteCommand("sysctl", "-n hw.ncpu").Trim();
            if (int.TryParse(coreOutput, out int cores))
            {
                specs.CpuCores = cores;
                specs.CpuThreads = cores;
            }

            // Get CPU frequency in Hz, convert to GHz
            string freqOutput = ExecuteCommand("sysctl", "-n hw.cpufrequency").Trim();
            if (long.TryParse(freqOutput, out long freq))
            {
                specs.CpuClockSpeedGHz = Math.Round(freq / 1_000_000_000.0, 2);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error detecting Mac CPU: {ex.Message}");
        }
    }

    private static void DetectRamMac(MachineSpecs specs)
    {
        try
        {
            string memOutput = ExecuteCommand("sysctl", "-n hw.memsize").Trim();
            if (long.TryParse(memOutput, out long bytes))
            {
                specs.RamGB = (int)(bytes / (1024L * 1024 * 1024));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error detecting Mac RAM: {ex.Message}");
        }
    }

    private static void DetectGpuMac(MachineSpecs specs)
    {
        try
        {
            // Use system_profiler to get GPU information
            string output = ExecuteCommand("system_profiler", "SPDisplaysDataType");

            // Parse output for GPU name (looks for "Chipset Model:" line)
            var lines = output.Split('\n');
            foreach (var line in lines)
            {
                if (line.Contains("Chipset Model:", StringComparison.OrdinalIgnoreCase))
                {
                    specs.GpuName = line.Split(':').LastOrDefault()?.Trim() ?? string.Empty;
                    break;
                }
            }

            // Note: GPU VRAM on integrated graphics uses shared system memory.
            // Getting dedicated GPU VRAM would require additional parsing.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error detecting Mac GPU: {ex.Message}");
        }
    }

    private static void DetectDiskMac(MachineSpecs specs)
    {
        try
        {
            // Use 'df -k' to get the main disk capacity in KB (macOS compatible)
            string output = ExecuteCommand("df", "-k /");
            
            var lines = output.Split('\n');
            if (lines.Length >= 2)
            {
                // Second line contains: filesystem total-kb used available use% mounted
                var parts = lines[1].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && long.TryParse(parts[1], out long kilobytes))
                {
                    specs.DiskGB = (int)(kilobytes / (1024L * 1024));  // KB to GB
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error detecting Mac disk via df: {ex.Message}");
        }

        // Fallback: use DriveInfo on the main root drive only
        try
        {
            var rootDrive = DriveInfo.GetDrives().FirstOrDefault(d => d.Name == "/");
            if (rootDrive?.IsReady == true)
            {
                specs.DiskGB = (int)(rootDrive.TotalSize / (1024L * 1024 * 1024));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error detecting Mac disk via DriveInfo: {ex.Message}");
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

        try { DetectCpuLinux(specs); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"CPU detection failed: {ex.Message}"); }
        try { DetectRamLinux(specs); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"RAM detection failed: {ex.Message}"); }
        try { DetectDiskLinux(specs); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Disk detection failed: {ex.Message}"); }

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

        specs.DiskGB = (int)Math.Min(totalBytes / (1024L * 1024 * 1024), int.MaxValue);
    }

    // Listen for real-time changes to a provider's machine specs
    public FirestoreChangeListener ListenSpecs(string providerId, Action<MachineSpecs?> onChanged)
    {
        return _repository.Listen(providerId, onChanged);
    }
}
