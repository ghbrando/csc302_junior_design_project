using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Configuration;
using System.Runtime.InteropServices;
using providerunicore.Services;

namespace unicoreprovider.Services;

public class DockerService : IDockerService, IDisposable
{
    private readonly ILogger<DockerService> _logger;
    private readonly string _relayAddr;
    private readonly int _relayServerPort;
    private readonly string _relayToken;
    private readonly INotificationService _notificationService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAuditService _audit;

    // Capabilities that have known container-escape or host-attack potential.
    // Dropped on every consumer VM regardless of image or consumer request.
    // Normal VM workloads (SSH, apt, web servers, compilers) don't need any of these.
    private static readonly string[] DroppedCapabilities =
    [
        // --- Remove from Docker defaults ---
        "NET_RAW",          // raw sockets → ARP spoofing, packet injection
        "MKNOD",            // create device files → potential escape vector
        // AUDIT_WRITE kept — required by PAM for sshd authentication logging.
        // Dropping it causes sshd to abort connections immediately after handshake.
        "SETFCAP",          // set file capabilities on arbitrary files
        "SETPCAP",          // manipulate process capability bounding sets
        // --- Belt-and-suspenders: not in defaults but block explicitly ---
        "SYS_ADMIN",        // mount filesystems, kernel params — near root
        "NET_ADMIN",        // reconfigure host network interfaces
        "SYS_PTRACE",       // attach debugger to host processes
        "SYS_MODULE",       // load / unload kernel modules
        "SYS_RAWIO",        // raw I/O to hardware
        "SYS_BOOT",         // reboot or shutdown the host
        "MAC_ADMIN",        // change mandatory access control policy
        "MAC_OVERRIDE",     // override MAC labels
        "LINUX_IMMUTABLE",  // set immutable / append-only file flags
        "SYS_TIME",         // change the host system clock
    ];

    // /proc and /sys paths hidden from consumers to prevent host information
    // leakage and hardware manipulation.
    private static readonly string[] MaskedKernelPaths =
    [
        "/proc/acpi",
        "/proc/kcore",       // direct read window into physical host memory
        "/proc/keys",
        "/proc/latency_stats",
        "/proc/timer_list",
        "/proc/timer_stats",
        "/proc/sched_debug",
        "/proc/scsi",
        "/sys/firmware",
        "/sys/devices/virtual/powercap",
    ];

    private static readonly string[] ReadonlyKernelPaths =
    [
        "/proc/asound",
        "/proc/bus",
        "/proc/fs",
        "/proc/irq",
        "/proc/sys",
        "/proc/sysrq-trigger",
    ];

    public DockerService(
        IConfiguration config,
        INotificationService notificationService,
        IServiceScopeFactory scopeFactory,
        IAuditService audit,
        ILogger<DockerService> logger)
    {
        _logger = logger;
        _relayAddr = config["FrpRelay:ServerAddr"] ?? "136.116.172.0";
        _relayServerPort = config.GetValue<int>("FrpRelay:ServerPort", 7000);
        _relayToken = config["FrpRelay:AuthToken"] ?? "unicore-relay-secret";
        _notificationService = notificationService;
        _scopeFactory = scopeFactory;
        _audit = audit;
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
        int? volumeGb = null, int? serviceRelayPort = null, string? providerUid = null,
        CancellationToken ct = default)
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

        // Load GCP VM agent key from environment (for container GCS sync)
        var gcpKeyJson = Environment.GetEnvironmentVariable("GCP_VM_AGENT_KEY") ?? "";
        var gcpKeyBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(gcpKeyJson));

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

        // Append a second [[proxies]] block for the HTTP service tunnel when a service relay port is provided
        if (serviceRelayPort.HasValue)
        {
            frpSetup =
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
                $"echo '' >> {frpCfg} && " +
                $"echo '[[proxies]]' >> {frpCfg} && " +
                $"echo 'name = \"{name}-svc\"' >> {frpCfg} && " +
                $"echo 'type = \"tcp\"' >> {frpCfg} && " +
                $"echo 'localIP = \"127.0.0.1\"' >> {frpCfg} && " +
                $"echo 'localPort = 8080' >> {frpCfg} && " +
                $"echo 'remotePort = {serviceRelayPort.Value}' >> {frpCfg} && " +
                $"{frpBin} -c {frpCfg} > /tmp/frpc.log 2>&1 &) || true";
        }

        // GCP key setup: decode base64 to get valid JSON, write to /tmp/gcp-key.json with restricted permissions
        var gcpKeySetup = $"echo '{gcpKeyBase64}' | base64 -d > /tmp/gcp-key.json && chmod 600 /tmp/gcp-key.json && ";

        // Google Cloud SDK setup and cron job for GCS sync using Python
        var gcsPath = $"consumers/{consumerContext}/{vmId}/home/";
        var pythonScript = new System.Text.StringBuilder()
            .AppendLine("#!/usr/bin/env python3")
            .AppendLine("import os, sys, logging")
            .AppendLine("from pathlib import Path")
            .AppendLine("from google.cloud import storage")
            .AppendLine("from google.oauth2 import service_account")
            .AppendLine("log_dir = Path('/var/log/unicore')")
            .AppendLine("log_dir.mkdir(parents=True, exist_ok=True)")
            .AppendLine("logging.basicConfig(level=logging.INFO, format='[%(asctime)s] %(levelname)s: %(message)s', handlers=[logging.FileHandler(log_dir / 'backup.log'), logging.StreamHandler(sys.stdout)])")
            .AppendLine("logger = logging.getLogger(__name__)")
            .AppendLine("try:")
            .AppendLine("  creds = service_account.Credentials.from_service_account_file('/tmp/gcp-key.json')")
            .AppendLine("  client = storage.Client(credentials=creds, project=creds.project_id)")
            .AppendLine("  bucket = client.bucket('unicore-vm-volumes')")
            .AppendLine("  local_dir = Path('/home/consumer')")
            .AppendLine("  if not local_dir.exists():")
            .AppendLine("    sys.exit(0)")
            .AppendLine("  synced = 0")
            .AppendLine("  for local_file in local_dir.rglob('*'):")
            .AppendLine("    if local_file.is_file():")
            .AppendLine($"      blob_path = '{gcsPath}' + local_file.relative_to(local_dir.parent).as_posix()")
            .AppendLine("      try:")
            .AppendLine("        bucket.blob(blob_path).upload_from_filename(str(local_file))")
            .AppendLine("        synced += 1")
            .AppendLine("      except Exception as e:")
            .AppendLine("        logger.warning(f'Failed to upload: {e}')")
            .AppendLine("  logger.info(f'Synced {synced} files to gs://unicore-vm-volumes')")
            .AppendLine("except Exception as e:")
            .AppendLine("  logger.error(f'Sync failed: {e}')")
            .AppendLine("  sys.exit(1)")
            .ToString();

        // Normalize to Unix line endings for the container
        var pythonScriptUnix = pythonScript.Replace("\r\n", "\n");

        var gcsSetup =
            "apt-get install -y cron curl python3-pip > /dev/null 2>&1 && " +
            "pip3 install --quiet google-cloud-storage > /dev/null 2>&1 && " +
            "mkdir -p /var/log/unicore && " +
            $"cat > /usr/local/bin/unicore-backup.py << 'ENDPY'\n{pythonScriptUnix}ENDPY\n" +
            "chmod 755 /usr/local/bin/unicore-backup.py && " +
            "echo '*/5 * * * * root /usr/local/bin/unicore-backup.py >> /var/log/unicore/backup.log 2>&1' > /etc/cron.d/unicore-backup-volume && " +
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

        // Ensure an isolated bridge network exists for this consumer.
        // Containers from different consumers cannot reach each other at
        // the network layer even if they share the same provider host.
        var consumerNetworkName = $"unicore-net-{(string.IsNullOrWhiteSpace(consumerUid) ? "shared" : consumerUid)}";
        await EnsureConsumerNetworkAsync(client, consumerNetworkName);

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
                Binds        = new[] { $"{volumeName}:/home/consumer" },
                NanoCPUs     = (long)(cpuCores * 1_000_000_000L),
                Memory       = (long)(ramGB * 1024L * 1024L * 1024L),
                MemorySwap   = (long)(ramGB * 1024L * 1024L * 1024L),
                PidsLimit    = 512,
                NetworkMode  = consumerNetworkName,

                // Drop capabilities with known escape or host-attack potential.
                // Normal consumer workloads (SSH, apt, web servers) are unaffected.
                CapDrop      = DroppedCapabilities,

                // Hide / lock kernel paths that leak host info or allow manipulation.
                MaskedPaths  = MaskedKernelPaths,
                ReadonlyPaths = ReadonlyKernelPaths,
            }
        });

        await client.Containers.StartContainerAsync(response.ID, new ContainerStartParameters());

        // Audit: record that this provider started a consumer VM.
        if (!string.IsNullOrWhiteSpace(providerUid))
            _audit.Log(providerUid, "vm_started", vmId: vmId, consumerUid: consumerUid,
                detail: $"image={image} cpu={cpuCores} ram={ramGB}GB");

        var canSendStartNotification = await IsNotificationEnabledAsync(
            providerUid,
            vmId,
            p => p.NotifyVmStarted,
            "vm-started");

        if (canSendStartNotification)
        {
            _logger.LogInformation(
                "Sending VM-started notification for VM {VmId} (container {ContainerId}).",
                vmId,
                response.ID);
            await _notificationService.SendVmStartedNotificationAsync(name, response.ID);
        }
        else
        {
            _logger.LogInformation(
                "Skipping VM-started notification for VM {VmId} due to provider preference.",
                vmId);
        }

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

    public async Task StopContainerAsync(string containerId, string vmName, string? volumeName = null,
        string? vmId = null, string? providerUid = null)
    {
        var client = await GetClientAsync();

        await client.Containers.StopContainerAsync(containerId, new ContainerStopParameters
        {
            WaitBeforeKillSeconds = 5
        });

        // Capture any named volumes attached to this container before deleting it.
        // Volumes cannot be removed while still referenced by a container.
        var attachedNamedVolumes = new HashSet<string>(StringComparer.Ordinal);
        var container = await client.Containers.InspectContainerAsync(containerId);
        if (container.Mounts is not null)
        {
            foreach (var mount in container.Mounts)
            {
                if (string.Equals(mount.Type, "volume", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(mount.Name))
                {
                    attachedNamedVolumes.Add(mount.Name);
                }
            }
        }

        await client.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters
        {
            Force = true
        });

        // Also include the explicit volume name from metadata if provided.
        if (!string.IsNullOrWhiteSpace(volumeName))
        {
            attachedNamedVolumes.Add(volumeName);
        }

        // Remove associated named volumes now that the container is gone.
        foreach (var attachedVolume in attachedNamedVolumes)
        {
            try
            {
                await RemoveVolumeAsync(attachedVolume);
            }
            catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Volume already removed, which is fine
            }
        }

        // Clean up the consumer's isolated network if no containers remain on it.
        await TryCleanUpConsumerNetworkAsync(client, containerId);

        // Audit: record that this provider stopped a consumer VM.
        if (!string.IsNullOrWhiteSpace(providerUid))
            _audit.Log(providerUid, "vm_stopped", vmId: vmId, detail: $"container={containerId}");

        var canSendStoppedNotification = await IsNotificationEnabledAsync(
            providerUid,
            vmId,
            p => p.NotifyVmCompleted,
            "vm-completed");

        if (canSendStoppedNotification)
        {
            _logger.LogInformation(
                "Sending VM-completed notification for VM {VmId} (container {ContainerId}).",
                vmId ?? "(unknown)",
                containerId);
            await _notificationService.SendVmStoppedNotificationAsync(vmName, containerId);
        }
        else
        {
            _logger.LogInformation(
                "Skipping VM-completed notification for VM {VmId} due to provider preference.",
                vmId ?? "(unknown)");
        }
    }

    private async Task<bool> IsNotificationEnabledAsync(
        string? providerUid,
        string? vmId,
        Func<Provider, bool> selector,
        string notificationKind)
    {
        try
        {
            var resolvedProviderUid = await ResolveProviderUidAsync(providerUid, vmId);

            if (string.IsNullOrWhiteSpace(resolvedProviderUid))
            {
                _logger.LogWarning(
                    "Could not resolve provider UID while checking {NotificationKind} notification for VM {VmId}; defaulting to disabled.",
                    notificationKind,
                    vmId ?? "(unknown)");
                return false;
            }

            using var providerScope = _scopeFactory.CreateScope();
            var providerService = providerScope.ServiceProvider.GetRequiredService<IProviderService>();
            var provider = await providerService.GetByFirebaseUidAsync(resolvedProviderUid);

            if (provider == null)
            {
                _logger.LogWarning(
                    "Provider {ProviderUid} not found while checking {NotificationKind} preference; defaulting to enabled.",
                    resolvedProviderUid,
                    notificationKind);
                return true;
            }

            var enabled = selector(provider);
            _logger.LogInformation(
                "Notification preference {NotificationKind} for provider {ProviderUid}: {Enabled}",
                notificationKind,
                resolvedProviderUid,
                enabled);
            return enabled;
        }
        catch (Exception ex)
        {
            // Fail closed so notification toggles are respected even if preference lookup fails.
            _logger.LogWarning(
                ex,
                "Failed to evaluate {NotificationKind} preference for VM {VmId}; defaulting to disabled.",
                notificationKind,
                vmId ?? "(unknown)");
            return false;
        }
    }

    private async Task<string?> ResolveProviderUidAsync(string? providerUid, string? vmId)
    {
        if (!string.IsNullOrWhiteSpace(providerUid))
            return providerUid;

        if (!string.IsNullOrWhiteSpace(vmId))
        {
            using var scope = _scopeFactory.CreateScope();
            var vmService = scope.ServiceProvider.GetRequiredService<IVmService>();
            var vm = await vmService.GetByIdAsync(vmId);
            if (!string.IsNullOrWhiteSpace(vm?.ProviderId))
                return vm.ProviderId;
        }

        using var authScope = _scopeFactory.CreateScope();
        var authState = authScope.ServiceProvider.GetService<IAuthStateService>();
        if (!string.IsNullOrWhiteSpace(authState?.FirebaseUid))
            return authState.FirebaseUid;

        return null;
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

    public async Task TagImageAsync(string sourceTag, string targetTag, CancellationToken ct = default)
    {
        var client = await GetClientAsync();
        var (repo, tag) = ParseImage(targetTag);
        await client.Images.TagImageAsync(sourceTag, new ImageTagParameters
        {
            RepositoryName = repo,
            Tag = tag,
        }, ct);
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

        // Skip pull if the image already exists locally (e.g. local snapshot)
        var (imageName, tag) = ParseImage(imageTag);
        var existing = await client.Images.ListImagesAsync(new ImagesListParameters
        {
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["reference"] = new Dictionary<string, bool> { [$"{imageName}:{tag}"] = true }
            }
        }, ct);
        if (existing.Count > 0)
            return;

        var gcpKeyJson = Environment.GetEnvironmentVariable("GCP_SERVICE_ACCOUNT_KEY") ?? "";

        AuthConfig? authConfig = null;
        if (!string.IsNullOrEmpty(gcpKeyJson))
        {
            authConfig = new AuthConfig
            {
                Username = "_json_key",
                Password = gcpKeyJson
            };
        }

        await client.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = imageName, Tag = tag },
            authConfig,
            new Progress<JSONMessage>(),
            ct);
    }

    public async Task<bool> IsRootlessAsync()
    {
        var client = await GetClientAsync();
        var info = await client.System.GetSystemInfoAsync();
        return info.SecurityOptions?.Any(
            opt => opt.Contains("rootless", StringComparison.OrdinalIgnoreCase)) ?? false;
    }

    // ── Network helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Creates an isolated bridge network for a consumer if one does not already exist.
    /// Each consumer's containers share one network; containers from different consumers
    /// cannot route traffic to each other even when collocated on the same provider host.
    /// </summary>
    private async Task EnsureConsumerNetworkAsync(DockerClient client, string networkName,
        CancellationToken ct = default)
    {
        var existing = await client.Networks.ListNetworksAsync(new NetworksListParameters
        {
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["name"] = new Dictionary<string, bool> { [networkName] = true }
            }
        }, ct);

        // ListNetworks with a name filter returns prefix matches; verify exact name.
        if (existing.Any(n => n.Name == networkName))
            return;

        await client.Networks.CreateNetworkAsync(new NetworksCreateParameters
        {
            Name   = networkName,
            Driver = "bridge",
            Labels = new Dictionary<string, string>
            {
                ["unicore.managed"]  = "true",
                ["unicore.consumer"] = networkName,
            }
        }, ct);

        _logger.LogInformation("[Security] Created isolated network {Network}", networkName);
    }

    /// <summary>
    /// Removes a consumer network if it has no remaining containers attached.
    /// Called after a container is stopped so networks are not leaked indefinitely.
    /// Swallows all exceptions — this is best-effort cleanup.
    /// </summary>
    private async Task TryCleanUpConsumerNetworkAsync(DockerClient client, string removedContainerId)
    {
        try
        {
            // Find unicore-managed networks that were connected to this container.
            var networks = await client.Networks.ListNetworksAsync(new NetworksListParameters
            {
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["label"] = new Dictionary<string, bool> { ["unicore.managed=true"] = true }
                }
            });

            foreach (var net in networks)
            {
                // Skip networks that still have containers attached.
                var detail = await client.Networks.InspectNetworkAsync(net.ID);
                var remaining = detail.Containers?
                    .Keys
                    .Where(id => !id.StartsWith(removedContainerId, StringComparison.OrdinalIgnoreCase))
                    .ToList() ?? [];

                if (remaining.Count == 0)
                {
                    await client.Networks.DeleteNetworkAsync(net.ID);
                    _logger.LogInformation("[Security] Removed empty consumer network {Network}", net.Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Security] Non-fatal: could not clean up consumer network after container {Id}", removedContainerId);
        }
    }

    public void Dispose() => _cachedClient?.Dispose();
}
