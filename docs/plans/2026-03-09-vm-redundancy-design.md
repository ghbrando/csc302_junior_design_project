# VM Redundancy & Failover System — Design Document

**Date:** 2026-03-09
**Branch:** feature/vm-redundancy
**Status:** Ready for implementation

---

## Context

Currently, UniCore containers are fully ephemeral. When a provider goes offline (hard crash or graceful shutdown), the consumer's VM — including all installed packages, config files, and user data — is permanently lost. There is no failover, no state preservation, and no notification to the consumer.

This feature adds a two-tier persistence layer:
1. **User data** (`/home/consumer`) synced incrementally to GCS every 5 minutes
2. **System state** (installed packages, `/etc` configs) committed as a Docker image to Artifact Registry every 2 hours + on graceful shutdown + on consumer demand

When a consumer's VM becomes unreachable, they manually trigger "Migrate VM" to restore their environment on a new provider using the latest snapshot + GCS backup.

---

## Architecture Summary

```
Provider Machine (no volume access)
├── Docker Container (black box)
│   ├── /home/consumer  ←── Docker Named Volume (unicore-vol-{vmId})
│   │   └── Background cron job runs every 5 min:
│   │       gsutil rsync → GCS (incremental)
│   │
│   ├── GCP service account key (read-only, /tmp/gcp-key.json)
│   │   └── File permissions 600 (consumer cannot read)
│   │
│   └── Writable layer  ←── docker commit every 2hr + shutdown + on-demand
│                           └── Pushed to Artifact Registry (:latest tag)
│
└── Provider App
    ├── SnapshotSchedulerService (2hr timer + shutdown hook)
    │   └── Commits writable layer (OS + packages + configs)
    │
    └── VolumeBackupService
        └── Monitors sync status via Firestore
            └── Supports "Backup Now" trigger (consumer-initiated)
```

**Key principle:** Provider has zero access to volumes or container internals. Container is a self-contained black box that syncs data automatically via internal cron job.

**Migration flow:** Consumer clicks "Migrate VM" → matchmaking finds new provider → new provider pulls snapshot image + restores GCS volume → new container starts with all state → consumer reconnects.

**GCP costs:** ~$12/month additional for 40 VMs (GCS storage ~$4 + Artifact Registry ~$8).

---

## New GCP Infrastructure

1. **GCS Bucket:** `unicore-vm-volumes` (Standard, us-central1)
2. **Artifact Registry Repo:** `unicore-vm-snapshots` (Docker format, us-central1, keep 1 version per VM)
3. **Service Accounts (two separate accounts):**
   - **Provider agent:** `unicore-provider-agent@{project}.iam.gserviceaccount.com`
     - `roles/artifactregistry.writer` on `unicore-vm-snapshots` repo (for image snapshots)
     - Secret in Secret Manager: `unicore-provider-gcp-key` (used by provider app at startup)
   - **Container/VM agent:** `unicore-vm-agent@{project}.iam.gserviceaccount.com`
     - `roles/storage.objectAdmin` on `unicore-vm-volumes` bucket **scoped to the consumer's path only** (via custom IAM binding or prefix)
     - Secret in Secret Manager: `unicore-vm-agent-gcp-key` (injected into each container)

**Scoping:** Each container is injected a **unique service account key** that has GCS permissions only for its own path: `gs://unicore-vm-volumes/consumers/{consumerUid}/{vmId}/`

---

## Implementation Phases

### Phase 1 — Data Model Changes

**File:** `unicore.shared/Models/VirtualMachine.cs`

Add 12 new nullable Firestore fields:

```
volume_name          → VolumeName:              string?    (e.g., "unicore-vol-{vmId}")
gcs_bucket           → GcsBucket:               string?    (e.g., "unicore-vm-volumes")
gcs_path             → GcsPath:                 string?    (e.g., "consumers/{uid}/{vmId}/")
last_volume_sync_at  → LastVolumeSyncAt:         DateTime?
volume_sync_status   → VolumeSyncStatus:         string?    ("Idle"|"Syncing"|"Error")
snapshot_image       → SnapshotImage:            string?    (full Artifact Registry tag)
last_snapshot_at     → LastSnapshotAt:           DateTime?
snapshot_status      → SnapshotStatus:           string?    ("Idle"|"Committing"|"Pushing"|"Error")
migration_status     → MigrationStatus:          string?    ("Requested"|"Restoring"|"Migrated"|"Failed")
migration_requested_at → MigrationRequestedAt:  DateTime?
migration_error      → MigrationError:           string?
original_vm_id       → OriginalVmId:             string?
```

**File:** `unicore.shared/Models/VmMigrationRequest.cs` *(new)*

New Firestore model for `vm_migration_requests` collection:

```
migration_request_id  → MigrationRequestId: string   (Guid, document ID)
vm_id                 → VmId:               string
consumer_uid          → ConsumerUid:        string
source_provider_uid   → SourceProviderUid:  string
target_provider_uid   → TargetProviderUid:  string
status                → Status:             string   ("pending"→"restoring"→"completed"|"failed")
requested_at          → RequestedAt:        DateTime
completed_at          → CompletedAt:        DateTime?
error                 → Error:              string?
new_vm_id             → NewVmId:            string?
```

---

### Phase 2 — Docker Primitives

**File:** `providerunicore/Services/IDockerService.cs`

Add to interface:

```csharp
Task<string> CreateVolumeAsync(string volumeName, CancellationToken ct = default);
Task RemoveVolumeAsync(string volumeName, CancellationToken ct = default);
Task<string> CommitContainerAsync(string containerId, string repository, string tag, CancellationToken ct = default);
Task PushImageAsync(string imageTag, CancellationToken ct = default);
```

(Note: `ExecInContainerAsync` is NOT needed — containers handle their own syncing via internal cron jobs.)

Update `StartContainerAsync` signature:

```csharp
// Before:
Task<string> StartContainerAsync(string name, string image, int relayPort, int cpuCores, int ramGB)

// After:
Task<(string ContainerId, string VolumeName)> StartContainerAsync(
    string vmId, string name, string image, int relayPort, int cpuCores, int ramGB,
    string? existingVolumeName = null, CancellationToken ct = default)
```

**File:** `providerunicore/Services/DockerService.cs`

- Implement all 4 new interface methods using Docker.DotNet SDK (`CommitContainerChangesAsync` already exists in Docker.DotNet)
- `CreateVolumeAsync` → `DockerClient.Volumes.CreateAsync()`
- `RemoveVolumeAsync` → `DockerClient.Volumes.RemoveAsync()`
- `CommitContainerAsync` → `DockerClient.Images.CommitContainerChangesAsync()`
- `PushImageAsync` → `DockerClient.Images.PushImageAsync()` with auth config from provider's `GOOGLE_APPLICATION_CREDENTIALS`
- Update `StartContainerAsync`:
  - Accept `vmId` parameter
  - Call `CreateVolumeAsync($"unicore-vol-{vmId}")` before container creation
  - Add `HostConfig.Binds = ["{volumeName}:/home/consumer"]` to container config
  - **Security:** Write GCP service account key to `/tmp/gcp-key.json` **before** container starts, with restrictive permissions (600: readable only by root)
  - Pass only the path as env var: `GOOGLE_APPLICATION_CREDENTIALS=/tmp/gcp-key.json` (NOT the key content itself)
  - Pass bucket and path info: `GCS_BUCKET=unicore-vm-volumes`, `GCS_PATH=consumers/{consumerUid}/{vmId}/`
  - Extend startup shell command to install `google-cloud-cli` and set up cron job for periodic sync
  - Update callers: `Dashboard.razor` `LaunchVmAsync()` and `ProcessRequestAsync()`

---

### Phase 3 — Volume Backup Service

**Architecture:** The container itself runs an automatic background cron job that syncs to GCS every 5 minutes. The provider app monitors and coordinates, but does NOT perform the sync (container is a black box).

**File:** `providerunicore/Services/IVolumeBackupService.cs` *(new)*

```csharp
Task StartMonitoringAsync(string vmId);
Task StopMonitoringAsync(string vmId);
Task RestoreFromGcsAsync(string containerId, string gcsPath, CancellationToken ct = default);
```

**File:** `providerunicore/Services/VolumeBackupService.cs` *(new)*

- `IHostedService` that monitors Firestore for `volume_sync_status` updates (same pattern as `ContainerMonitorService`)
- In-memory `ConcurrentDictionary<vmId, vmMetadata>` to track active VMs
- `StartMonitoringAsync`: registers a VM for monitoring
- `StopMonitoringAsync`: unregisters a VM
- Periodically reads `last_volume_sync_at` from Firestore to verify the container's cron job is running (health check)
- `RestoreFromGcsAsync`: pulls files from GCS into the volume (used during migration)
- **NOTE:** Provider does NOT initiate syncs. Container's internal cron job handles all uploads.

**Container startup modifications (in `DockerService.StartContainerAsync`):**

When the container starts, its startup script:
1. Installs `google-cloud-cli` (gsutil)
2. Verifies `/tmp/gcp-key.json` is readable (owned by root, permissions 600)
3. Sets up a cron job at `/etc/cron.d/unicore-backup-volume`:
   ```bash
   */5 * * * * root gsutil -m rsync -r -d /home/consumer \
       gs://${GCS_BUCKET}/${GCS_PATH}home/ >> /var/log/backup.log 2>&1
   ```
4. Optionally writes sync status/timestamps to Firestore via a simple HTTP POST call (if monitoring is needed)

**Security model:**
- Consumer user (`consumer`) has NO access to `/tmp/gcp-key.json` (file permissions 600, owned by root)
- Consumer cannot see the GCS credentials via `env` (only sees the path)
- Consumer cannot modify the backup cron job
- Provider app never touches the container's internals

**File:** `providerunicore/Components/Pages/Dashboard.razor`

- Wire `VolumeBackupService.StartMonitoringAsync(vmId)` at the same call sites as `ContainerMonitorService.StartMonitoring(...)`
- Wire `VolumeBackupService.StopMonitoringAsync(vmId)` alongside `ContainerMonitorService.StopMonitoring(...)`
- Health check: periodically read `last_volume_sync_at` from Firestore and alert if the timestamp is stale (e.g., > 10 minutes old)

---

### Phase 4 — Snapshot Service

**File:** `providerunicore/Services/ISnapshotService.cs` *(new)*

```csharp
Task TakeSnapshotAsync(string vmId, string containerId, CancellationToken ct = default);
Task PullSnapshotAsync(string imageTag, CancellationToken ct = default);
```

**File:** `providerunicore/Services/SnapshotService.cs` *(new)*

- `TakeSnapshotAsync`:
  1. Update Firestore `snapshot_status = "Committing"`
  2. Call `DockerService.CommitContainerAsync(containerId, registryHost/project/unicore-vm-snapshots, vmId)`
  3. Update Firestore `snapshot_status = "Pushing"`
  4. Call `DockerService.PushImageAsync(imageTag)`
  5. Update Firestore `snapshot_status = "Idle"`, `last_snapshot_at = utcNow`, `snapshot_image = imageTag`
- `PullSnapshotAsync`: calls `DockerService.PullImageIfMissingAsync(imageTag)` (reuse existing method)

**File:** `providerunicore/Services/SnapshotSchedulerService.cs` *(new)*

- `IHostedService` with `PeriodicTimer` at 2-hour intervals
- In-memory set of `(vmId, containerId)` pairs
- On each tick: calls `SnapshotService.TakeSnapshotAsync` for each registered VM
- Registers `ApplicationStopping` callback via `IHostApplicationLifetime` to run a final snapshot round on graceful shutdown
- `StartScheduling(vmId, containerId)` / `StopScheduling(vmId)` wired in Dashboard alongside other services

**File:** `providerunicore/Program.cs`

- Register `ISnapshotService`, `IVolumeBackupService`, `SnapshotSchedulerService` in DI
- Register `VmMigrationRequest` Firestore repository
- Extend shutdown timeout: `builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromMinutes(10))`
- Load `unicore-provider-gcp-key` secret from Secret Manager at startup; configure ADC

---

### Phase 5 — Migration Flow

**File:** `providerunicore/Components/Pages/Dashboard.razor`

Add a second Firestore listener (alongside existing `_requestListener`) watching `vm_migration_requests` where `target_provider_uid == providerId AND status == "pending"`.

`ProcessMigrationRequestAsync(VmMigrationRequest request)`:
1. Update request `status = "restoring"`, VM `migration_status = "Restoring"`
2. Pull snapshot image: `SnapshotService.PullSnapshotAsync(vm.SnapshotImage)` — this is the OS + packages + configs
3. Create new volume and allocate relay port (same logic as current `ProcessRequestAsync`)
4. Call `DockerService.StartContainerAsync(newVmId, name, vm.SnapshotImage, relayPort, cpu, ram, newVolumeName)` — starts from the snapshot image with a fresh volume
5. **Restore user data:** The container's startup script runs `gsutil rsync` one time to pull from GCS:
   ```
   gsutil -m rsync -r gs://unicore-vm-volumes/{gcsPath}home/ /home/consumer/
   ```
   This pulls the latest backup of user files into the new volume before the consumer connects.
6. Write new `VirtualMachine` doc with `OriginalVmId = vm.VmId`, all new provider/container/port values
7. Update old VM: `status = "Stopped"`, `migration_status = "Migrated"`
8. Update request: `status = "completed"`, `new_vm_id = newVmId`
9. Begin monitoring + snapshotting new VM (volume monitoring starts on its own via the container's cron job)

**File:** `consumerunicore/Services/IConsumerVmService.cs`

Add:

```csharp
Task RequestMigrationAsync(string vmId);
Task RequestBackupAsync(string vmId);
```

**File:** `consumerunicore/Services/ConsumerVmService.cs`

- `RequestMigrationAsync`:
  1. Read VM from Firestore
  2. Run matchmaking (exclude source provider, require sufficient resources)
  3. Write `VmMigrationRequest` doc to `vm_migration_requests`
  4. Update VM `migration_status = "Requested"`
- `RequestBackupAsync`: write `force_sync_requested = true` on the VM document

**File:** `consumerunicore/Program.cs`

- Register `VmMigrationRequest` Firestore repository

---

### Phase 6 — Consumer UI

**File:** `consumerunicore/Components/Dashboard/VmCard.razor`

- **"Backup Now" button:** Always visible on running VMs. Calls `RequestBackupAsync(vm.VmId)`. Disabled while `VolumeSyncStatus == "Syncing"`.
- **"Migrate VM" button:** Visible when `ConsecutiveMisses > 5` or always (your call). Disabled when `MigrationStatus != null`.
- **Last backup time:** Subtitle under VM name — "Last backup: 3 min ago" from `LastVolumeSyncAt`.
- **Migration status badge:** Replaces "RUNNING" badge with "MIGRATING" / "RESTORING" / "MIGRATED" when `MigrationStatus` is set.
- **Migration confirmation modal:** Same CSS pattern as existing modals. Warn: "Your home directory will be preserved. Installed packages will be restored from the last snapshot (taken N hours ago)."

**File:** `consumerunicore/Components/Pages/Dashboard.razor`

- Map new VM fields into `RunningVm` display record
- Add `HandleMigrateVm(vmId)` handler that calls `ConsumerVmService.RequestMigrationAsync(vmId)`
- Add Firestore listener watching the old VM document's `migration_status` during migration

---

## Critical Files

| File | Change |
|---|---|
| `unicore.shared/Models/VirtualMachine.cs` | +12 new Firestore fields |
| `unicore.shared/Models/VmMigrationRequest.cs` | **NEW** model |
| `providerunicore/Services/IDockerService.cs` | +5 new methods, updated `StartContainerAsync` sig |
| `providerunicore/Services/DockerService.cs` | Implement all above |
| `providerunicore/Services/ISnapshotService.cs` | **NEW** interface |
| `providerunicore/Services/SnapshotService.cs` | **NEW** implementation |
| `providerunicore/Services/IVolumeBackupService.cs` | **NEW** interface |
| `providerunicore/Services/VolumeBackupService.cs` | **NEW** hosted service |
| `providerunicore/Services/SnapshotSchedulerService.cs` | **NEW** hosted service (2hr timer + shutdown hook) |
| `providerunicore/Program.cs` | Register new services, load GCP key secret, extend shutdown timeout |
| `providerunicore/Components/Pages/Dashboard.razor` | Migration request listener + `ProcessMigrationRequestAsync`, wire new services |
| `consumerunicore/Services/IConsumerVmService.cs` | +`RequestMigrationAsync`, +`RequestBackupAsync` |
| `consumerunicore/Services/ConsumerVmService.cs` | Implement migration + backup request logic |
| `consumerunicore/Program.cs` | Register `VmMigrationRequest` repo |
| `consumerunicore/Components/Dashboard/VmCard.razor` | New buttons + status indicators |
| `consumerunicore/Components/Pages/Dashboard.razor` | New handlers + migration status listener |

---

## Patterns to Reuse

- `ContainerMonitorService` — exact pattern for `VolumeBackupService` (in-memory dict, `PeriodicTimer`, `StartMonitoring`/`StopMonitoring`)
- `PauseResumeListenerService` — Firestore snapshot listener pattern for migration request listener
- `Dashboard.razor` `ProcessRequestAsync` — template for `ProcessMigrationRequestAsync`
- `HeartbeatWorker` targeted field update pattern — for updating `snapshot_status` / `last_volume_sync_at`
- `DockerService.PullImageIfMissingAsync` — reuse directly in `PullSnapshotAsync`

---

## Security Model: Credential Injection

**Principle:** Provider injects minimal necessary credentials into containers. Containers are "black boxes" — provider has no visibility into their internals.

**Container startup credential flow:**

1. **Provider app at startup:**
   - Reads `unicore-vm-agent-gcp-key` (VM-scoped service account) from Secret Manager
   - Reads `unicore-provider-gcp-key` (provider service account) from Secret Manager

2. **For each container created:**
   - Provider writes the VM-scoped key to a temporary file: `/tmp/gcp-key.json`
   - Sets file permissions to `600` (readable only by root)
   - Passes env vars:
     ```
     GOOGLE_APPLICATION_CREDENTIALS=/tmp/gcp-key.json
     GCS_BUCKET=unicore-vm-volumes
     GCS_PATH=consumers/{consumerUid}/{vmId}/
     ```

3. 3. **Inside the container (startup script, runs as root):**
   - Verifies `/tmp/gcp-key.json` is readable (file is owned by root with permissions 600)
   - Creates `consumer` user WITHOUT sudo privileges: `useradd -m -s /bin/bash consumer` (no `sudoers` entry)
   - Sets up root crontab with backup job:
     ```bash
     */5 * * * * root gsutil -m rsync -r -d /home/consumer \
         gs://${GCS_BUCKET}/${GCS_PATH}home/ >> /var/log/backup.log 2>&1
     ```
   - Starts FRPC relay daemon as root: `frpc -c /etc/frpc/frpc.toml &` (relays SSH port 22 to GCP)
   - Starts sshd as root (standard; required for authentication and privilege separation)

4. **Consumer cannot escalate privileges:**
   - Consumer user has NO `sudo` access (no `/etc/sudoers.d/consumer` entry)
   - Consumer cannot read `/tmp/gcp-key.json` (permissions 600, owned by root)
   - Consumer cannot modify cron jobs or FRPC config (all owned by root with write restrictions)
   - Consumer can only run unprivileged commands and SSH shell operations
   - Consumer's `env` shows only the path `GOOGLE_APPLICATION_CREDENTIALS=/tmp/gcp-key.json`, not the key itself

**Scope of each service account:**
- **Provider agent (`unicore-provider-agent`):** Can only push images to Artifact Registry, cannot access GCS
- **VM agent (`unicore-vm-agent`):** Can only read/write to its own GCS path (`consumers/{uid}/{vmId}/`), cannot see other consumers' data

---

## Container Process Model: Root-Only Critical Services

**Design principle:** All security-critical and system-level services run as root. Consumer user is unprivileged and cannot escalate.

**Processes running as root:**

| Process | Purpose | Config owner | Editable by consumer? |
|---|---|---|---|
| `sshd` | SSH daemon for consumer login | root (`/etc/ssh/sshd_config`) | ❌ No |
| `frpc` | FRP relay client (SSH tunnel to GCP) | root (`/etc/frpc/frpc.toml`) | ❌ No |
| `cron` | Runs backup job every 5 min | root (`/etc/cron.d/unicore-backup-volume`) | ❌ No |
| `gsutil rsync` | Syncs `/home/consumer` to GCS | root (runs from cron) | ❌ No |

**Processes running as unprivileged `consumer` user:**

| Process | Purpose | Scope |
|---|---|---|
| User shell commands | Consumer's terminal session | Only can execute unprivileged operations |
| User applications | Apps the consumer installs/runs | Isolated to consumer's user context |

**Consumer restrictions (enforced at startup via sudoers whitelist):**

Create `/etc/sudoers.d/consumer` with granular rules:

```sudoers
# Allow package management
consumer ALL=(ALL) /usr/bin/apt, /usr/bin/apt-get, /usr/bin/apt-cache

# Allow viewing/tailing logs (read-only)
consumer ALL=(ALL) /usr/bin/tail, /usr/bin/cat /var/log/*

# Allow service control for USER-DEFINED services only (not system services)
# consumer ALL=(ALL) /usr/bin/systemctl restart myapp
# consumer ALL=(ALL) /usr/bin/systemctl start myapp
# consumer ALL=(ALL) /usr/bin/systemctl stop myapp

# Allow directory/file operations in home
consumer ALL=(ALL) /bin/chmod, /bin/chown, /bin/rm, /bin/mv, /bin/cp

# EXPLICITLY DENY access to critical files/services
consumer ALL=(ALL) !/bin/cat /tmp/gcp-key.json
consumer ALL=(ALL) !/usr/bin/cat /etc/frpc/frpc.toml
consumer ALL=(ALL) !/usr/bin/crontab
consumer ALL=(ALL) !/usr/bin/systemctl restart sshd
consumer ALL=(ALL) !/usr/bin/systemctl restart frpc
consumer ALL=(ALL) !/usr/bin/systemctl daemon-reload
consumer ALL=(ALL) !/usr/sbin/reboot
consumer ALL=(ALL) !/usr/sbin/shutdown

# Optional: allow certain commands without password prompt
consumer ALL=(ALL) NOPASSWD: /usr/bin/apt update
consumer ALL=(ALL) NOPASSWD: /usr/bin/tail -f /var/log/backup.log
```

**Verification at startup:**

```bash
# Create consumer user
useradd -m -s /bin/bash consumer

# Write sudoers rules (use echo or heredoc, verify with visudo -c)
cat > /etc/sudoers.d/consumer << 'EOF'
# [rules above]
EOF

# Validate syntax (must succeed, or sshd breaks)
visudo -c -f /etc/sudoers.d/consumer

# Verify restrictions work
consumer@container$ sudo cat /tmp/gcp-key.json
# Output: Sorry, user consumer is not allowed to run this command

consumer@container$ sudo apt update
# Output: [apt output, no password required due to NOPASSWD]

consumer@container$ sudo tail -f /var/log/backup.log
# Output: [log output]
```

**What this allows vs. blocks:**
- ✅ Consumer CAN install packages with `sudo apt install`
- ✅ Consumer CAN view logs with `sudo tail /var/log/...`
- ✅ Consumer CAN manage their own files
- ✅ Consumer CAN run custom services (if whitelisted in sudoers)
- ❌ Consumer CANNOT read GCS credentials (`/tmp/gcp-key.json`)
- ❌ Consumer CANNOT read FRPC config
- ❌ Consumer CANNOT modify cron jobs
- ❌ Consumer CANNOT restart critical services (sshd, frpc)
- ❌ Consumer CANNOT reboot the container
- ❌ Consumer CANNOT reload systemd

---

## Scope Boundaries (Out of Scope)

- Automatic failover (no consumer action required)
- Live migration (CRIU / in-memory process state)
- Multi-region migration
- Billing continuity across `original_vm_id` chains
- GCS path cleanup on VM permanent deletion
- `nerdctl`-native implementation (interface defined now, implemented later if needed)

---

## Verification Plan

1. **Security — GCS credentials:**
   - SSH into container as `consumer` user
   - Try: `cat /tmp/gcp-key.json` → should fail with "Permission denied"
   - Try: `env | grep GCP` → should only show `GOOGLE_APPLICATION_CREDENTIALS=/tmp/gcp-key.json`, not the key content

2. **Container cron job setup:**
   - SSH into container and verify `/etc/cron.d/unicore-backup-volume` exists
   - Check cron logs: `tail -f /var/log/backup.log` or similar
   - Manually run: `gsutil -m rsync -r -d /home/consumer gs://unicore-vm-volumes/...` to verify credentials work

3. **Integration — Volume sync:**
   - Launch a VM, SSH in as consumer
   - Create a file: `echo "test" > /home/consumer/test.txt`
   - Wait 5+ minutes for the cron job to run
   - Check GCS: `gsutil ls gs://unicore-vm-volumes/consumers/{uid}/{vmId}/home/` → should include `test.txt`

4. **Integration — Snapshot:**
   - Launch a VM, run `apt install vim`, verify it's installed
   - Wait for auto-snapshot (2 hrs) or manually trigger via provider Dashboard
   - Verify `snapshot_image` and `last_snapshot_at` appear in Firestore
   - Verify image exists in Artifact Registry

5. **Integration — Migration (full flow):**
   - **Setup:** Launch VM on Provider A, install packages, create files in home dir, wait for volume sync (~5 min) and snapshot (~2 hrs, or trigger manually)
   - **Trigger failure:** Kill Provider A's app (simulate crash)
   - **Consumer action:** Consumer clicks "Migrate VM"
   - **Verify request:** Check Firestore `vm_migration_requests` has `status = "pending"`
   - **Provider B picks up:** Provider B's Dashboard detects the migration request
   - **Verify new VM:** New VM document created with `OriginalVmId = oldVmId`, new relay port, new container ID
   - **Restore check:** SSH into new container as consumer, verify:
     - Home directory files are present (`test.txt` created earlier)
     - Installed packages are present (`vim` from earlier install)
   - **Connection:** Verify consumer can SSH and terminal works

6. **Graceful shutdown:**
   - Stop provider app cleanly (e.g., `Ctrl+C` or systemd stop)
   - Observe Firestore: `snapshot_status` should go through "Committing" → "Pushing" → "Idle"
   - Verify snapshot image pushed to Artifact Registry

7. **Cost verification:**
   - After 24h with 2–3 VMs running, check GCP Console:
     - GCS bucket `unicore-vm-volumes`: should show ~5–10GB (user data)
     - Artifact Registry `unicore-vm-snapshots`: should show ~2–6GB per image
   - Verify costs align with design estimates (~$12/month for 40 VMs)
