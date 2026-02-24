using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Configuration;
using System.Runtime.InteropServices;

namespace unicoreprovider.Services;

public class DockerService : IDockerService, IDisposable
{
    private readonly string _relayAddr;
    private readonly int _relayServerPort;
    private readonly string _relayToken;
    private readonly INotificationService _notificationService;

    public DockerService(IConfiguration config, INotificationService notificationService)
    {
        _relayAddr = config["FrpRelay:ServerAddr"] ?? "136.116.172.0";
        _relayServerPort = config.GetValue<int>("FrpRelay:ServerPort", 7000);
        _relayToken = config["FrpRelay:AuthToken"] ?? "unicore-relay-secret";
        _notificationService = notificationService;
    }

    // Endpoints tried in order on Windows. Named pipe = Docker Desktop or native
    // dockerd. TCP = our WSL2-based Docker Engine setup.
    private static readonly Uri[] WindowsEndpoints =
    [
        new Uri("npipe://./pipe/docker_engine"),
        new Uri("tcp://127.0.0.1:2375"),
    ];

    private static readonly Uri[] LinuxEndpoints =
    [
        new Uri("unix:///var/run/docker.sock"),
    ];

    private IEnumerable<Uri> Endpoints => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? WindowsEndpoints
        : LinuxEndpoints;

    // Cached client for the currently working endpoint. Reset if it becomes stale.
    private DockerClient? _cachedClient;

    public async Task<bool> IsReachableAsync()
    {
        // Try all endpoints without touching the cache so this is always a fresh check.
        foreach (var uri in Endpoints)
        {
            try
            {
                using var client = new DockerClientConfiguration(uri).CreateClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await client.System.PingAsync(cts.Token);
                return true;
            }
            catch { }
        }
        return false;
    }

    public async Task<string> StartContainerAsync(string name, string image, int relayPort)
    {
        var client = await GetClientAsync();
        await PullImageIfMissingAsync(client, image);

        const string frpVersion = "0.61.0";
        var frpTar = $"/tmp/frp_{frpVersion}_linux_amd64.tar.gz";
        var frpBin = $"/tmp/frp_{frpVersion}_linux_amd64/frpc";
        var frpCfg = "/tmp/frpc.toml";
        var frpUrl = $"https://github.com/fatedier/frp/releases/download/v{frpVersion}/frp_{frpVersion}_linux_amd64.tar.gz";

        // Writes frpc.toml line by line using echo, then starts frpc in the background.
        // Wrapped in () || true so that SSH still starts even if the relay setup fails.
        var frpSetup =
            $"(curl -sL -o {frpTar} {frpUrl} && " +
            $"tar -xzf {frpTar} -C /tmp && " +
            $"echo 'serverAddr = \"{_relayAddr}\"' > {frpCfg} && " +
            $"echo 'serverPort = {_relayServerPort}' >> {frpCfg} && " +
            $"echo 'auth.token = \"{_relayToken}\"' >> {frpCfg} && " +
            $"echo '' >> {frpCfg} && " +
            $"echo '[[proxies]]' >> {frpCfg} && " +
            $"echo 'name = \"{name}\"' >> {frpCfg} && " +
            $"echo 'type = \"tcp\"' >> {frpCfg} && " +
            $"echo 'localIP = \"127.0.0.1\"' >> {frpCfg} && " +
            $"echo 'localPort = 22' >> {frpCfg} && " +
            $"echo 'remotePort = {relayPort}' >> {frpCfg} && " +
            $"{frpBin} -c {frpCfg} > /tmp/frpc.log 2>&1 &) || true";

        var cmd = new List<string>
        {
            "/bin/sh",
            "-c",
            "apt-get update && apt-get install -y openssh-server curl > /dev/null 2>&1 && " +
            "mkdir -p /run/sshd && " +
            "useradd -m -s /bin/bash consumer 2>/dev/null || true && " +
            "echo 'consumer:consumer123' | chpasswd && " +
            "sed -i 's/#PermitRootLogin prohibit-password/PermitRootLogin yes/' /etc/ssh/sshd_config && " +
            frpSetup + " && " +
            "/usr/sbin/sshd -D"
        };

        var response = await client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = image,
            Name = name,
            Cmd = cmd,
            ExposedPorts = new Dictionary<string, EmptyStruct>
            {
                { "22/tcp", default }  // Expose SSH port
            },
            HostConfig = new HostConfig
            {
                PortBindings = new Dictionary<string, IList<PortBinding>>
                {
                    {
                        "22/tcp",
                        new List<PortBinding>
                        {
                            new PortBinding { HostPort = "0" }  // Random port assignment
                        }
                    }
                }
            }
        });

        await client.Containers.StartContainerAsync(response.ID, new ContainerStartParameters());

        // Notify the provider that a new VM is running
        await _notificationService.SendVmStartedNotificationAsync(name, response.ID);

        return response.ID;
    }

    public async Task<int?> GetContainerSshPortAsync(string containerId)
    {
        var client = await GetClientAsync();
        
        var container = await client.Containers.InspectContainerAsync(containerId);
        
        // Look for the mapped SSH port (22/tcp)
        if (container.NetworkSettings?.Ports?.TryGetValue("22/tcp", out var portBindings) == true &&
            portBindings?.Count > 0)
        {
            var binding = portBindings.First();
            if (int.TryParse(binding.HostPort, out int port))
            {
                return port;
            }
        }

        return null;
    }

    public async Task StopContainerAsync(string containerId)
    {
        var client = await GetClientAsync();

        await client.Containers.StopContainerAsync(containerId, new ContainerStopParameters
        {
            WaitBeforeKillSeconds = 5
        });

        await client.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters
        {
            Force = true
        });

        // Notify the provider that a VM is stopping
        await _notificationService.SendVmStoppedNotificationAsync(containerId, containerId);
    }

    public async Task<(double CpuPercent, double RamPercent)> GetContainerStatsAsync(string containerId)
    {
        var client = await GetClientAsync();
        ContainerStatsResponse? stats = null;

        await client.Containers.GetContainerStatsAsync(
            containerId,
            new ContainerStatsParameters { Stream = false },
            new Progress<ContainerStatsResponse>(s => stats = s),
            CancellationToken.None);

        if (stats is null)
            return (0, 0);

        return (CalculateCpuPercent(stats), CalculateRamPercent(stats));
    }

    // Returns the cached client if it is still responsive, otherwise discovers
    // a new working endpoint and caches that.
    private async Task<DockerClient> GetClientAsync()
    {
        if (_cachedClient is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _cachedClient.System.PingAsync(cts.Token);
                return _cachedClient;
            }
            catch
            {
                _cachedClient.Dispose();
                _cachedClient = null;
            }
        }

        foreach (var uri in Endpoints)
        {
            try
            {
                var candidate = new DockerClientConfiguration(uri).CreateClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await candidate.System.PingAsync(cts.Token);
                _cachedClient = candidate;
                return _cachedClient;
            }
            catch { }
        }

        throw new InvalidOperationException(
            "Docker daemon is not reachable. Ensure Docker is installed and running.");
    }

    private static async Task PullImageIfMissingAsync(DockerClient client, string imageString)
    {
        var (imageName, tag) = ParseImage(imageString);

        var existing = await client.Images.ListImagesAsync(new ImagesListParameters
        {
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["reference"] = new Dictionary<string, bool> { [$"{imageName}:{tag}"] = true }
            }
        });

        if (existing.Count > 0)
            return;

        await client.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = imageName, Tag = tag },
            null,
            new Progress<JSONMessage>(),
            CancellationToken.None);
    }

    private static (string ImageName, string Tag) ParseImage(string imageString)
    {
        var parts = imageString.Split(':', 2);
        return parts.Length == 2 ? (parts[0], parts[1]) : (imageString, "latest");
    }

    private static double CalculateCpuPercent(ContainerStatsResponse stats)
    {
        var cpuDelta = (double)(stats.CPUStats.CPUUsage.TotalUsage - stats.PreCPUStats.CPUUsage.TotalUsage);
        var systemDelta = (double)(stats.CPUStats.SystemUsage - stats.PreCPUStats.SystemUsage);

        if (systemDelta <= 0 || cpuDelta <= 0)
            return 0;

        var cpuCount = stats.CPUStats.OnlineCPUs > 0
            ? (double)stats.CPUStats.OnlineCPUs
            : (double)(stats.CPUStats.CPUUsage.PercpuUsage?.Count ?? 1);

        return cpuDelta / systemDelta * cpuCount * 100.0;
    }

    private static double CalculateRamPercent(ContainerStatsResponse stats)
    {
        if (stats.MemoryStats.Limit == 0)
            return 0;

        var cache = stats.MemoryStats.Stats?.TryGetValue("cache", out var c) == true ? c : 0;
        var usedBytes = stats.MemoryStats.Usage - cache;

        return (double)usedBytes / stats.MemoryStats.Limit * 100.0;
    }

    public void Dispose() => _cachedClient?.Dispose();
}
