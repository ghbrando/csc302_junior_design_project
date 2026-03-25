using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Configuration;
using System.Runtime.InteropServices;
using providerunicore.Services;

namespace unicoreprovider.Services;

public class DockerService : IDockerService, IDisposable
{
    private readonly string _relayAddr;
    private readonly int _relayServerPort;
    private readonly string _relayToken;
    private readonly INotificationService _notificationService;
    private readonly IServiceScopeFactory _scopeFactory;

    public DockerService(IConfiguration config, INotificationService notificationService, IServiceScopeFactory scopeFactory)
    {
        _relayAddr = config["FrpRelay:ServerAddr"] ?? "136.116.172.0";
        _relayServerPort = config.GetValue<int>("FrpRelay:ServerPort", 7000);
        _relayToken = config["FrpRelay:AuthToken"] ?? "unicore-relay-secret";
        _notificationService = notificationService;
        _scopeFactory = scopeFactory;
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

    // Cached host total RAM in bytes, fetched once from Docker system info.
    private long? _hostRamBytes;

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

    public async Task<(string ContainerId, string VolumeName)> StartContainerAsync(
        string vmId, string name, string image, int relayPort, int cpuCores, int ramGB,
        string? existingVolumeName = null, string? consumerUid = null,
        int? volumeGb = null, CancellationToken ct = default)
    {
        var client = await GetClientAsync();
        await PullImageIfMissingAsync(client, image);

        // Create or reuse volume for persistent storage
        string volumeName = existingVolumeName ?? $"unicore-vol-{vmId}";
        try
        {
            await CreateVolumeAsync(volumeName, volumeGb, ct);
        }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // Volume already exists, which is fine
        }

        // Load GCP service account key from environment
        var gcpKeyJson = Environment.GetEnvironmentVariable("GCP_SERVICE_ACCOUNT_KEY") ?? "";
        var gcpKeyContent = gcpKeyJson.Replace("\"", "\\\"").Replace("\n", "\\n");

        // Determine the consumer context for GCS path
        var consumerContext = consumerUid ?? "shared";

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

        // GCP key setup: write service account key to /tmp/gcp-key.json with restricted permissions
        var gcpKeySetup = $"echo '{gcpKeyContent}' | sed 's/\\\\n/\\n/g' > /tmp/gcp-key.json && chmod 600 /tmp/gcp-key.json && ";

        // Google Cloud SDK setup and cron job for GCS sync
        var gcsSetup =
            "apt-get install -y cron > /dev/null 2>&1 && " +
            "echo '*/5 * * * * root /usr/bin/gsutil -m rsync -r /home/consumer gs://unicore-vm-volumes/consumers/" + consumerContext + "/" + vmId + "/home/' > /etc/cron.d/unicore-backup-volume && " +
            "chmod 0644 /etc/cron.d/unicore-backup-volume && " +
            "/etc/init.d/cron start > /dev/null 2>&1 && ";

        var cmd = new List<string>
        {
            "/bin/sh",
            "-c",
            "apt-get update && apt-get install -y openssh-server curl sudo > /dev/null 2>&1 && " +
            "mkdir -p /run/sshd && " +
            "useradd -m -s /bin/bash consumer 2>/dev/null || true && " +
            "echo 'consumer:consumer123' | chpasswd && " +
            "echo 'consumer ALL=(ALL) NOPASSWD:ALL' >> /etc/sudoers && " +
            "sed -i 's/#PermitRootLogin prohibit-password/PermitRootLogin yes/' /etc/ssh/sshd_config && " +
            gcpKeySetup +
            gcsSetup +
            "chown -R consumer:consumer /home/consumer && " +
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
                },
                Binds = new[] { $"{volumeName}:/home/consumer" },  // Mount the named volume
                NanoCPUs = (long)(cpuCores * 1_000_000_000L),
                Memory = (long)(ramGB * 1024L * 1024L * 1024L),
                MemorySwap = (long)(ramGB * 1024L * 1024L * 1024L),  // equals Memory → no swap headroom
                PidsLimit = 200   // prevent fork bombs from exhausting the host process table
            }
        });

        await client.Containers.StartContainerAsync(response.ID, new ContainerStartParameters());

        // Notify the provider that a new VM is running
        await _notificationService.SendVmStartedNotificationAsync(name, response.ID);

        return (response.ID, volumeName);
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

    public async Task StopContainerAsync(string containerId, string vmName, string? volumeName = null)
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

        // Remove associated volume if provided
        if (!string.IsNullOrEmpty(volumeName))
        {
            try
            {
                await RemoveVolumeAsync(volumeName);
            }
            catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Volume already removed, which is fine
            }
        }

        // Notify the provider that a VM is stopping
        await _notificationService.SendVmStoppedNotificationAsync(vmName, containerId);
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

        var hostRam = await GetHostRamBytesAsync();
        return (CalculateCpuPercent(stats), CalculateRamPercent(stats, hostRam));
    }

    public async Task PauseContainerAsync(string containerId)
    {
        var client = await GetClientAsync();
        await client.Containers.PauseContainerAsync(containerId);
    }

    public async Task UnpauseContainerAsync(string containerId, string vmId)
    {
        var client = await GetClientAsync();
        await client.Containers.UnpauseContainerAsync(containerId);

        // Use scope factory to safely access scoped services from this singleton
        using (var scope = _scopeFactory.CreateScope())
        {
            var vmService = scope.ServiceProvider.GetRequiredService<IVmService>();
            var providerService = scope.ServiceProvider.GetRequiredService<IProviderService>();
            await vmService.UpdateResumedFlag(vmId);
        }
    }

    // Returns host total RAM in bytes, fetched once and cached.
    private async Task<long> GetHostRamBytesAsync()
    {
        if (_hostRamBytes.HasValue) return _hostRamBytes.Value;
        var client = await GetClientAsync();
        var info = await client.System.GetSystemInfoAsync();
        _hostRamBytes = info.MemTotal;
        return _hostRamBytes.Value;
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
        var lastSlash = imageString.LastIndexOf('/');
        var lastColon = imageString.LastIndexOf(':');

        if (lastColon > lastSlash)
            return (imageString[..lastColon], imageString[(lastColon + 1)..]);

        return (imageString, "latest");
    }

    // Returns % of total host CPU (0–100 regardless of core count).
    // systemDelta already accounts for all cores, so dividing without scaling
    // by cpuCount gives the fraction of the whole machine's CPU budget.
    private static double CalculateCpuPercent(ContainerStatsResponse stats)
    {
        var cpuDelta = (double)(stats.CPUStats.CPUUsage.TotalUsage - stats.PreCPUStats.CPUUsage.TotalUsage);
        var systemDelta = (double)(stats.CPUStats.SystemUsage - stats.PreCPUStats.SystemUsage);

        if (systemDelta <= 0 || cpuDelta <= 0)
            return 0;

        return cpuDelta / systemDelta * 100.0;
    }

    // Returns % of total host RAM (0–100) using the machine's actual total,
    // not the container's capped limit.
    private static double CalculateRamPercent(ContainerStatsResponse stats, long hostRamBytes)
    {
        if (hostRamBytes <= 0)
            return 0;

        var cache = stats.MemoryStats.Stats?.TryGetValue("cache", out var c) == true ? c : 0;
        var usedBytes = stats.MemoryStats.Usage - cache;

        return (double)usedBytes / hostRamBytes * 100.0;
    }

    public async Task<string> CreateVolumeAsync(string volumeName, int? sizeGb = null, CancellationToken ct = default)
    {
        var client = await GetClientAsync();
        var createParams = new VolumesCreateParameters { Name = volumeName };

        // Store requested size as a label so it's visible via docker volume inspect.
        // The local driver doesn't enforce size limits, but the label makes the
        // requested allocation discoverable for monitoring and future enforcement.
        if (sizeGb.HasValue)
        {
            createParams.Labels = new Dictionary<string, string>
            {
                ["unicore.volume-size-gb"] = sizeGb.Value.ToString()
            };
        }

        var response = await client.Volumes.CreateAsync(createParams, ct);
        return response.Name;
    }

    public async Task RemoveVolumeAsync(string volumeName, CancellationToken ct = default)
    {
        var client = await GetClientAsync();
        await client.Volumes.RemoveAsync(volumeName, force: false, ct);
    }

    public async Task<VolumeResponse> InspectVolumeAsync(string volumeName, CancellationToken ct = default)
    {
        var client = await GetClientAsync();
        return await client.Volumes.InspectAsync(volumeName, ct);
    }

    public async Task<string> CommitContainerAsync(string containerId, string repository, string tag, CancellationToken ct = default)
    {
        var client = await GetClientAsync();
        var response = await client.Images.CommitContainerChangesAsync(
            new CommitContainerChangesParameters
            {
                ContainerID = containerId,
                RepositoryName = repository,
                Tag = tag
            },
            ct);
        return response.ID;
    }

    public async Task PushImageAsync(string imageTag, CancellationToken ct = default)
    {
        var client = await GetClientAsync();

        // Load GCP credentials for authentication with Artifact Registry
        var gcpKeyJson = Environment.GetEnvironmentVariable("GCP_SERVICE_ACCOUNT_KEY") ?? "";

        var authConfig = new AuthConfig
        {
            Username = "_json_key",
            Password = gcpKeyJson
        };

        await client.Images.PushImageAsync(
            imageTag,
            new ImagePushParameters(),
            authConfig,
            new Progress<JSONMessage>(),
            ct);
    }

    public async Task PullImageAsync(string imageTag, CancellationToken ct = default)
    {
        var client = await GetClientAsync();
        var (imageName, tag) = ParseImage(imageTag);

        await client.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = imageName, Tag = tag },
            null,
            new Progress<JSONMessage>(),
            ct);
    }

    public void Dispose() => _cachedClient?.Dispose();
}
