# Workstream 2: Docker Volumes & Snapshots

**Type:** Backend (Docker operations)
**Depends On:** Workstream 1 (data models)
**Blocks:** Workstreams 3, 5, 6
**Estimated Scope:** 400 lines, 8 hours
**Owner:** Backend developer (Docker expertise)

---

## 🎯 Overview

Extend the existing `IDockerService` interface with 4 new methods for volume management and container snapshots:
1. `CreateVolumeAsync` — Create a named Docker volume
2. `RemoveVolumeAsync` — Delete a volume
3. `CommitContainerAsync` — Save container state to Docker image
4. `PushImageAsync` — Push image to container registry

Also modify `StartContainerAsync` to handle volumes and inject GCP credentials.

**Why:** The Docker primitives (volumes + snapshots) are the foundation for backup & migration. Without them, you can't persist user data or save system state.

---

## 🏗️ Architecture

### Current Docker Service Design

```
IDockerService (interface)
├─ IsReachableAsync()
├─ StartContainerAsync(name, image, relayPort, cpu, ram)
├─ GetContainerSshPortAsync(containerId)
├─ StopContainerAsync(containerId, vmName)
├─ GetContainerStatsAsync(containerId)
├─ PauseContainerAsync(containerId)
└─ UnpauseContainerAsync(containerId)
```

**Current flow when launching a VM:**
```
1. StartContainerAsync creates container
2. Container gets FRP client + SSH setup via shell commands
3. Container starts
4. Consumer can SSH in and use it
```

### New Design (With Volumes & Snapshots)

```
IDockerService (interface)
├─ [existing methods]
├─ CreateVolumeAsync(volumeName) → string (volume ID)
├─ RemoveVolumeAsync(volumeName)
├─ InspectVolumeAsync(volumeName) → VolumeInfo
├─ CommitContainerAsync(containerId, repository, tag) → string (image ID)
└─ PushImageAsync(imageTag) → void
```

**New flow when launching a VM:**
```
1. CreateVolumeAsync("unicore-vol-{vmId}")
2. Inject GCP credentials into environment
3. StartContainerAsync creates container with volume binding: volume → /home/consumer
4. Container startup script runs:
   - Installs google-cloud-cli
   - Creates consumer user
   - Sets up cron job for GCS sync
   - Starts FRP relay + SSH
5. Consumer can SSH in and work
6. Every 5 min: cron job syncs /home/consumer to GCS
7. Every 2 hours: SnapshotService calls CommitContainerAsync + PushImageAsync
```

---

## 🔌 Integration Points

### Files to Modify

1. **`providerunicore/Services/IDockerService.cs`**
   - Add 4 new method signatures
   - Update `StartContainerAsync` signature to accept `vmId` and `volumeName`

2. **`providerunicore/Services/DockerService.cs`**
   - Implement all 4 new methods using Docker.DotNet SDK
   - Update `StartContainerAsync` implementation to:
     - Accept new parameters
     - Create volume before container
     - Handle volume binding in container config
     - Inject GCP credentials at runtime

3. **`providerunicore/Program.cs`**
   - Load `unicore-provider-gcp-key` secret from GCP Secret Manager at startup
   - Configure credentials for Docker API authentication (if pushing to GCP Artifact Registry)

4. **Docker startup script** (NEW)
   - Location: `providerunicore/Assets/container-startup.sh`
   - Runs as root inside container
   - Installs tools, creates consumer user, sets up sudoers, starts services

---

## ✅ What Needs to Be Done

### Task 1: Extend IDockerService Interface

**File:** `providerunicore/Services/IDockerService.cs`

Add 4 new method signatures:

```csharp
/// <summary>
/// Create a named Docker volume for persistent storage.
/// </summary>
/// <param name="volumeName">Volume name (e.g., "unicore-vol-{vmId}")</param>
/// <param name="ct">Cancellation token</param>
/// <returns>Volume ID</returns>
Task<string> CreateVolumeAsync(string volumeName, CancellationToken ct = default);

/// <summary>
/// Remove a Docker volume.
/// </summary>
/// <param name="volumeName">Volume name to remove</param>
/// <param name="ct">Cancellation token</param>
Task RemoveVolumeAsync(string volumeName, CancellationToken ct = default);

/// <summary>
/// Inspect a Docker volume for metadata.
/// </summary>
/// <param name="volumeName">Volume name</param>
/// <param name="ct">Cancellation token</param>
/// <returns>Volume metadata</returns>
Task<VolumeInspectResponse> InspectVolumeAsync(string volumeName, CancellationToken ct = default);

/// <summary>
/// Commit running container state to a Docker image.
/// </summary>
/// <param name="containerId">Container ID</param>
/// <param name="repository">Image repository (e.g., "myregistry.com/project/image")</param>
/// <param name="tag">Image tag (e.g., "latest" or a specific version)</param>
/// <param name="ct">Cancellation token</param>
/// <returns>Image ID</returns>
Task<string> CommitContainerAsync(string containerId, string repository, string tag, CancellationToken ct = default);

/// <summary>
/// Push Docker image to a remote registry.
/// </summary>
/// <param name="imageTag">Full image tag (e.g., "myregistry.com/project/image:tag")</param>
/// <param name="ct">Cancellation token</param>
Task PushImageAsync(string imageTag, CancellationToken ct = default);
```

Also update the existing `StartContainerAsync` signature:

```csharp
// BEFORE:
Task<string> StartContainerAsync(
    string name, string image, int relayPort, int cpuCores, int ramGB,
    CancellationToken ct = default);

// AFTER:
Task<(string ContainerId, string VolumeName)> StartContainerAsync(
    string vmId, string name, string image, int relayPort, int cpuCores, int ramGB,
    string? existingVolumeName = null, CancellationToken ct = default);
```

**Why the new return type?**
- Return both `ContainerId` and `VolumeName` so caller knows what was created
- Useful for cleanup if something fails later

### Task 2: Implement New Methods in DockerService

**File:** `providerunicore/Services/DockerService.cs`

**CreateVolumeAsync:**
```csharp
public async Task<string> CreateVolumeAsync(string volumeName, CancellationToken ct = default)
{
    var response = await _dockerClient.Volumes.CreateAsync(
        new VolumesCreateParameters { Name = volumeName },
        ct);
    return response.Name;
}
```

**RemoveVolumeAsync:**
```csharp
public async Task RemoveVolumeAsync(string volumeName, CancellationToken ct = default)
{
    await _dockerClient.Volumes.RemoveAsync(volumeName, false, ct);
}
```

**InspectVolumeAsync:**
```csharp
public async Task<VolumeInspectResponse> InspectVolumeAsync(string volumeName, CancellationToken ct = default)
{
    return await _dockerClient.Volumes.InspectAsync(volumeName, ct);
}
```

**CommitContainerAsync:**
```csharp
public async Task<string> CommitContainerAsync(
    string containerId, string repository, string tag, CancellationToken ct = default)
{
    var response = await _dockerClient.Images.CommitContainerChangesAsync(
        new CommitContainerChangesParameters
        {
            ContainerID = containerId,
            RepositoryName = repository,
            Tag = tag
        },
        ct);
    return response.ID;
}
```

**PushImageAsync:**
```csharp
public async Task PushImageAsync(string imageTag, CancellationToken ct = default)
{
    var authConfig = new AuthConfig
    {
        // If using GCP Artifact Registry, need to provide credentials
        // Can be extracted from the loaded GCP service account key
    };

    await _dockerClient.Images.PushImageAsync(
        imageTag,
        new ImagePushParameters { },
        new Progress<JSONMessage>(),
        authConfig,
        ct);
}
```

**Note on PushImageAsync:** You'll need to configure Docker auth for pushing to Artifact Registry. This typically involves:
1. Loading the service account key (from Secret Manager)
2. Running `gcloud auth configure-docker` or equivalent
3. Passing auth credentials to Docker client

### Task 3: Update StartContainerAsync

**File:** `providerunicore/Services/DockerService.cs`

Modify the `StartContainerAsync` implementation to:

```csharp
public async Task<(string ContainerId, string VolumeName)> StartContainerAsync(
    string vmId, string name, string image, int relayPort, int cpuCores, int ramGB,
    string? existingVolumeName = null, CancellationToken ct = default)
{
    // 1. Create volume (or use existing one if provided)
    string volumeName = existingVolumeName ?? $"unicore-vol-{vmId}";
    try
    {
        await CreateVolumeAsync(volumeName, ct);
    }
    catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
    {
        // Volume already exists (OK for migration scenario)
    }

    // 2. Prepare GCP credentials (inject into container)
    // Read from environment or Secret Manager
    var gcpKeyJson = Environment.GetEnvironmentVariable("GCP_SERVICE_ACCOUNT_KEY")
        ?? await LoadGcpKeyFromSecretManager(); // Implement this

    // 3. Create container with volume binding
    var createParams = new CreateContainerParameters
    {
        Image = image,
        Name = name,
        HostConfig = new HostConfig
        {
            // Bind volume to /home/consumer
            Binds = new[] { $"{volumeName}:/home/consumer" },
            CpuShares = cpuCores * 1024,
            Memory = ramGB * 1024 * 1024 * 1024,
            PortBindings = new Dictionary<string, IList<PortBinding>>
            {
                { "22/tcp", new[] { new PortBinding { HostPort = relayPort.ToString() } } }
            }
        },
        Env = new[]
        {
            "GOOGLE_APPLICATION_CREDENTIALS=/tmp/gcp-key.json",
            $"GCS_BUCKET=unicore-vm-volumes",
            $"GCS_PATH=consumers/{{CONSUMER_UID}}/{vmId}/",
            // ... other env vars
        }
    };

    // 4. Create and start container (existing logic)
    var containerResponse = await _dockerClient.Containers.CreateContainerAsync(createParams, ct);
    var containerId = containerResponse.ID;

    // 5. Write GCP key to container's /tmp before starting
    // (requires helper method or use docker cp logic)
    await WriteGcpKeyToContainerAsync(containerId, gcpKeyJson, ct);

    // 6. Start container with startup script
    await _dockerClient.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), ct);

    return (containerId, volumeName);
}
```

### Task 4: Add GCP Credential Loading to Program.cs

**File:** `providerunicore/Program.cs`

At startup, load the provider's GCP service account key:

```csharp
// Before building the app
var gcpKeySecret = await LoadSecretFromGcpAsync("unicore-provider-gcp-key");
Environment.SetEnvironmentVariable("GCP_SERVICE_ACCOUNT_KEY", gcpKeySecret);

// Or register as a singleton for dependency injection
builder.Services.AddSingleton<GcpCredentialProvider>(sp =>
    new GcpCredentialProvider(gcpKeySecret));
```

### Task 5: Create Container Startup Script

**File:** `providerunicore/Assets/container-startup.sh` (NEW)

This script runs as root inside the container **before** the consumer can log in:

```bash
#!/bin/bash
set -e

echo "=== UniCore Container Startup ==="

# 1. Verify GCP credentials exist and are readable
if [ ! -f /tmp/gcp-key.json ]; then
    echo "ERROR: GCP credentials not found at /tmp/gcp-key.json"
    exit 1
fi
chmod 600 /tmp/gcp-key.json
echo "✓ GCP credentials verified"

# 2. Install google-cloud-cli if not present
if ! command -v gsutil &> /dev/null; then
    echo "Installing google-cloud-cli..."
    apt-get update && apt-get install -y google-cloud-cli
    echo "✓ google-cloud-cli installed"
fi

# 3. Create consumer user (unprivileged)
if ! id -u consumer > /dev/null 2>&1; then
    echo "Creating consumer user..."
    useradd -m -s /bin/bash consumer
    echo "✓ consumer user created"
fi

# 4. Set up sudoers rules for consumer (allow package management, deny dangerous ops)
cat > /etc/sudoers.d/consumer << 'EOF'
# Allow package management
consumer ALL=(ALL) /usr/bin/apt, /usr/bin/apt-get, /usr/bin/apt-cache

# Allow viewing logs
consumer ALL=(ALL) /usr/bin/tail, /usr/bin/less /var/log/*

# Allow file management in home directory
consumer ALL=(ALL) /bin/chmod, /bin/chown, /bin/rm, /bin/mv, /bin/cp

# DENY access to critical resources
consumer ALL=(ALL) !/bin/cat /tmp/gcp-key.json
consumer ALL=(ALL) !/usr/bin/cat /etc/frpc/frpc.toml
consumer ALL=(ALL) !/usr/bin/crontab
consumer ALL=(ALL) !/usr/sbin/reboot
consumer ALL=(ALL) !/usr/sbin/shutdown

# Allow package management without password
consumer ALL=(ALL) NOPASSWD: /usr/bin/apt, /usr/bin/apt-get, /usr/bin/apt-cache
EOF

chmod 440 /etc/sudoers.d/consumer
visudo -c -f /etc/sudoers.d/consumer
echo "✓ Sudoers rules configured"

# 5. Set up cron job for periodic GCS sync
cat > /etc/cron.d/unicore-backup-volume << 'EOF'
*/5 * * * * root gsutil -m rsync -r -d /home/consumer \
    gs://${GCS_BUCKET}/${GCS_PATH}home/ >> /var/log/unicore/backup.log 2>&1
EOF

chmod 644 /etc/cron.d/unicore-backup-volume
mkdir -p /var/log/unicore
echo "✓ Cron job configured"

# 6. Start SSH daemon (for consumer login)
echo "Starting SSH daemon..."
/etc/init.d/ssh start || /usr/sbin/sshd -D &
echo "✓ SSH daemon started"

# 7. Start FRP relay client (if frpc config exists)
if [ -f /etc/frpc/frpc.toml ]; then
    echo "Starting FRP relay client..."
    frpc -c /etc/frpc/frpc.toml &
    echo "✓ FRP relay client started"
fi

echo "=== UniCore Container Ready ==="
sleep infinity
```

**Note:** This script assumes:
- GCP key already written to `/tmp/gcp-key.json` by provider app
- FRP config already written to `/etc/frpc/frpc.toml` (existing logic)
- google-cloud-cli, openssh-server installed in base image

---

## 🧪 Acceptance Criteria

- [ ] IDockerService interface has 4 new methods + updated StartContainerAsync signature
- [ ] All 5 methods implemented in DockerService.cs using Docker.DotNet SDK
- [ ] StartContainerAsync creates volume before creating container
- [ ] StartContainerAsync binds volume to `/home/consumer`
- [ ] StartContainerAsync injects GCP credentials as environment variable
- [ ] Container startup script created and includes:
  - [ ] GCP credential verification
  - [ ] Consumer user creation
  - [ ] Sudoers rules (allow package mgmt, deny critical ops)
  - [ ] Cron job setup for GCS sync
  - [ ] SSH and FRP startup
- [ ] GCP service account key loaded in Program.cs at startup
- [ ] No compilation errors
- [ ] Existing callers of StartContainerAsync updated to pass vmId parameter

---

## 🧠 Key Implementation Details

### Volume Binding in Docker
```csharp
// Volume binding: "{volumeName}:{mountPath}"
HostConfig.Binds = new[] { $"{volumeName}:/home/consumer" }
```

### Docker.DotNet SDK Methods
All operations use the Docker.DotNet library (already in project):
- `IDockerClient.Volumes.CreateAsync()`
- `IDockerClient.Volumes.RemoveAsync()`
- `IDockerClient.Images.CommitContainerChangesAsync()`
- `IDockerClient.Images.PushImageAsync()`

### GCP Credential Injection
Two approaches:

**Option A: File injection (recommended)**
- Provider writes key to container's `/tmp/gcp-key.json` before container starts
- Set file permissions to 600 (root only)
- Pass path via env var: `GOOGLE_APPLICATION_CREDENTIALS=/tmp/gcp-key.json`
- Consumer cannot read the file

**Option B: Environment variable injection**
- Pass entire key content as env var
- Less secure (env visible to all processes, harder to rotate)
- Not recommended

Use **Option A**.

---

## 🔗 Related Code

**Existing Docker integration:**
- `DockerService.cs` — See `StartContainerAsync` current implementation
- `ContainerMonitorService.cs` — Uses docker stats polling

**Docker.DotNet Examples:**
- GitHub: https://github.com/dotnet/Docker.DotNet
- API docs: Container, Image, Volume operations

**FRP relay setup (existing):**
- `StartContainerAsync` — Already sets up FRP config via shell commands
- Your code already has working FRP client integration

---

## 🚨 Gotchas & Edge Cases

### Volume Already Exists
If migrating a VM, the volume might already exist. Handle gracefully:
```csharp
try
{
    await CreateVolumeAsync(volumeName, ct);
}
catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
{
    // Volume already exists; that's OK for migration
}
```

### GCP Auth for Artifact Registry
Pushing images to GCP Artifact Registry requires:
1. Docker daemon must be authenticated (`gcloud auth configure-docker`)
2. Or: Pass auth credentials to PushImageAsync
3. Service account must have `roles/artifactregistry.writer`

### Filesystem Permissions
- GCP key file: permissions 600, owner root (consumer cannot read)
- Startup script: permissions 755, executable
- Cron job: owned by root, not writable by consumer

### Clean Up on Failure
If StartContainerAsync fails partway through:
- Volume created but container not started → leave volume (cleanup elsewhere)
- Don't try to auto-delete; let explicit cleanup handle it

---

## 📋 Code Review Checklist

- [ ] All Docker.DotNet SDK method signatures correct
- [ ] Error handling for common cases (volume exists, image not found, auth failed)
- [ ] Return types match interface definition
- [ ] GCP credential loading at startup
- [ ] Startup script is executable
- [ ] Volume binding syntax correct: `"volumeName:/containerPath"`
- [ ] Consumer user created with no sudo (or sudoers rules enforced)
- [ ] Cron job uses correct gsutil syntax
- [ ] No hardcoded credentials in code (use env vars or Secret Manager)
- [ ] Existing tests still pass

---

## ⏱️ Time Estimate

- Reading this document: 20 min
- Implementing 4 new methods: 60 min
- Updating StartContainerAsync: 45 min
- Adding credential loading to Program.cs: 15 min
- Creating startup script: 30 min
- Testing & refinement: 30 min

**Total: ~3.5 hours** (estimate 4–5 hours with edge cases)

---

## 🚀 Next Steps

Once this workstream is complete:
1. Workstream 3 can begin (consumer volume requests)
2. Workstream 5 can begin (background services using these primitives)
3. Workstreams 4 should complete (GCP infrastructure)

---

**Status:** Ready to implement (after Workstream 1 complete)
**Owner:** Backend developer (Docker expertise)
**Next Workstreams:** 3, 5 (parallel), then 6
