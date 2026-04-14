# UniCore Networking Architecture

## The Core Problem

Provider machines are typically behind home routers with:
- No static IP address
- No open inbound ports
- NAT/firewall blocking incoming connections

Consumers need a terminal into a VM running on that machine. The answer is **FRP (Fast Reverse Proxy)**: the Docker container on the provider's machine makes an outbound TCP tunnel to a GCP VM, and the consumer SSHes through that tunnel.

---

## Overview

```
┌──────────────────┐         SSH (port 2222–2300)        ┌────────────────────────┐
│  Consumer        │ ───────────────────────────────────> │  GCP FRP Relay VM      │
│  (Browser /      │                                      │  IP: 136.116.172.0     │
│   SSH.NET)       │                                      │                        │
└──────────────────┘                                      │  frps listening:       │
                                                          │   :7000  (control)     │
                                                          │   :2222–2300 (SSH)     │
                                                          │   :8000–8200 (HTTP)    │
                                                          └──────────┬─────────────┘
                                                                     │
                                                          FRP tunnel (outbound from container)
                                                                     │
                                                          ┌──────────▼─────────────┐
                                                          │  Docker Container      │
                                                          │  (on provider machine) │
                                                          │                        │
                                                          │  frpc  → relay :7000   │
                                                          │  sshd  → :22           │
                                                          │  cron  → GCS sync      │
                                                          └────────────────────────┘
                                                                     ▲
                                                          ┌──────────┴─────────────┐
                                                          │  Provider Desktop App  │
                                                          │  (Docker.DotNet SDK)   │
                                                          └────────────────────────┘

                              All shared state and coordination via Firestore
                         (real-time listeners replace any need for a message broker)
```

---

## Connection Lifecycle

### Phase 1: Provider Comes Online

1. Provider logs into the desktop app (Firebase JWT).
2. App updates `node_status = "Online"` in Firestore.
3. `MachineService` detects hardware specs (WMI on Windows, `/proc` on Linux) and writes a `MachineSpecs` document to Firestore.
4. `PauseResumeListenerService` starts a Firestore real-time listener on this provider's VMs, watching `is_paused` and `status` fields.

No outbound connection to GCP at this stage — the provider is simply registered in Firestore as available.

---

### Phase 2: Consumer Rents a VM

1. Consumer picks CPU cores, RAM, disk, region, and base image in the browser.
2. `MatchmakingService.FindBestMatchAsync()` queries Firestore:
   - Filter: `node_status == "Online"`, matching region
   - Cross-reference `MachineSpecs` to verify available resources
   - Sort by `ConsistencyScore` descending
   - Return the top match
3. A `VirtualMachine` document is written to Firestore with `status = "Provisioning"`.

---

### Phase 3: Provider Starts the Container

The provider app's Firestore listener fires when the new VM document appears.

`DockerService.StartContainerAsync()` creates and starts a container. The container's startup command chain does the following **inside the container**:

```bash
# 1. Install SSH server
apt-get update && apt-get install -y openssh-server curl

# 2. Create consumer user
useradd -m consumer && echo "consumer:consumer123" | chpasswd
mkdir -p /home/consumer/.ssh && chmod 700 /home/consumer/.ssh

# 3. Configure sshd
sed -i 's/#PasswordAuthentication yes/PasswordAuthentication yes/' /etc/ssh/sshd_config
mkdir -p /run/sshd

# 4. Download FRP v0.61.0
curl -L -o /tmp/frp.tar.gz https://github.com/fatedier/frp/releases/download/v0.61.0/frp_0.61.0_linux_amd64.tar.gz
tar -xzf /tmp/frp.tar.gz -C /tmp/

# 5. Write frpc.toml (relay address and auth token come from GCP Secret Manager)
cat > /tmp/frpc.toml << EOF
serverAddr = "<relay-ip>"
serverPort = 7000
auth.token  = "<auth-token>"

[[proxies]]
name       = "ssh-<vmId>"
type       = "tcp"
localIP    = "127.0.0.1"
localPort  = 22
remotePort = <relay-port>       # allocated from 2222–2300

[[proxies]]
name       = "svc-<vmId>"
type       = "tcp"
localPort  = 8080
remotePort = <service-relay-port>   # allocated from 8000–8200
EOF

# 6. Start FRP client in background
/tmp/frp_0.61.0_linux_amd64/frpc -c /tmp/frpc.toml &

# 7. Set up GCS backup cron (every 5 min)
echo "*/5 * * * * gsutil -m rsync -r /home/consumer gs://unicore-vm-volumes/..." \
  | crontab -
service cron start

# 8. Start sshd in foreground (keeps container alive)
/usr/sbin/sshd -D
```

**Port allocation** (`MigrationService.AllocateRelayPortAsync()`): queries all existing VMs in Firestore, collects their `relay_port` values, and returns the first unused port in the 2222–2300 range. The allocated port is written to the VM's `relay_port` field before the container starts.

---

### Phase 4: Provisioning → Running

`VmProvisioningService` (background service, runs every 5 seconds) monitors all VMs in `"Provisioning"` state.

For each VM it TCP-probes `relay-ip:relay-port` and reads the SSH banner:

```
SSH-2.0-OpenSSH_9.x
```

Once the banner is received, the VM's `status` is updated to `"Running"` in Firestore and the consumer sees it in their dashboard.

**Startup grace period:** The heartbeat service skips probing VMs within 5 minutes of their `StartedAt` timestamp to avoid penalizing providers for normal container boot time.

---

### Phase 5: Consumer Opens the Terminal

1. Consumer clicks **Terminal** on a Running VM.
2. `WebShellService.GetConnectionInfoAsync(vmId, userUid)` validates ownership (`vm.Client == userUid`) and resolves the connection endpoint:
   - **Production:** `FrpRelay:ServerAddr : vm.RelayPort`
   - **Local testing:** `localhost : vm.SshPort` (Docker-mapped port, same machine only)
3. The browser opens a WebShell component backed by SSH.NET (Renci.SshNet).
4. SSH.NET connects to `relay-ip:relay-port` with credentials `consumer / consumer123`.
5. That TCP connection hits the FRP relay, which tunnels it to `sshd` on port 22 inside the container.
6. The consumer gets a fully interactive shell.

**Data path:**
```
Consumer browser  →  SSH.NET  →  GCP relay VM :relay-port
                                        │
                                  FRP tunnel
                                        │
                               Docker container :22 (sshd)
```

---

## Real-Time Coordination via Firestore

UniCore uses Firestore real-time listeners instead of a message broker for all provider-side control signals.

| Signal | Firestore field | Provider response |
|--------|----------------|-------------------|
| Pause VM | `is_paused = true` | `docker pause <containerId>` |
| Resume VM | `is_paused = false` | `docker unpause <containerId>` |
| Stop VM | `status = "Stopped"` | `docker stop <containerId>` |
| New VM assignment | New document with matching `provider_id` | `DockerService.StartContainerAsync()` |

`PauseResumeListenerService` registers Firestore snapshot listeners per provider. Changes fire within ~1 second of the consumer action.

---

## Health Monitoring

The **Heartbeat Service** (ASP.NET Core Worker Service, deployed to Cloud Run) runs every 10 seconds:

1. Fetches all `status = "Running"` VMs from Firestore.
2. Groups VMs by `provider_id`.
3. For each VM (skipping paused, stopped, or within the 5-minute grace period):
   - TCP-connects to `relay-ip:relay-port` with a 3-second timeout.
   - Reads the SSH banner.
   - **Success:** `provider.ConsistencyScore += 0.01`, `vm.ConsecutiveMisses = 0`
   - **Failure:** `provider.ConsistencyScore -= (0.01 × consecutiveMisses)`, `vm.ConsecutiveMisses++`
4. Writes targeted field updates to Firestore (not full document replacements).

`ConsistencyScore` is clamped to [0, 100] and used as the primary sort key in matchmaking. Unreliable providers receive fewer VM assignments and therefore less revenue.

---

## Container Persistence

### Volume Backups (every 5 minutes)
A cron job inside each container runs `gsutil rsync` to sync `/home/consumer` to:
```
gs://unicore-vm-volumes/consumers/<consumerUid>/<vmId>/home/
```

The VM's `volume_sync_status` and `last_volume_sync_at` fields in Firestore are updated by `VolumeBackupService` on the provider.

### Container Snapshots (every 2 hours)
`SnapshotService` commits the running container to a Docker image and pushes it to GCP Artifact Registry:
```
us-central1-docker.pkg.dev/unicore-junior-design/unicore-vm-snapshots/<vmId>:latest
```

The image URI is stored in `vm.snapshot_image`. During migration, the target provider pulls this image to recreate the container with installed packages and system state intact.

---

## Live Migration

When a VM is migrated to a different provider:

1. Consumer (or source provider) triggers migration via the consumer web app.
2. A `VmMigrationRequest` document is created in Firestore with `status = "pending"`.
3. The **target provider's** `MigrationService` picks up the request and runs a 12-step state machine:

| Step | Action |
|------|--------|
| 1 | Mark request `restoring` |
| 2 | Mark old VM `Restoring` |
| 2.5 | Force fresh `docker commit` → push to Artifact Registry |
| 2.6 | Force GCS backup of `/home/consumer` |
| 3 | Pull snapshot image on target provider |
| 4 | Create new Docker volume |
| 5 | Restore user data from GCS |
| 6 | Allocate new relay port on target provider |
| 6.5 | Stop old container (releases name and resources) |
| 7 | Start new container from snapshot on target provider |
| 8 | Create new `VirtualMachine` document in Firestore |
| 9 | Mark old VM `Migrated` |
| 10 | Start monitoring new VM |
| 11 | Mark migration request `Completed` |
| 12 | Delete old VM document |

The consumer sees their VM reappear with the same name, full file system, and installed packages intact.

---

## Service Exposure (HTTP)

VMs can optionally expose a web service (port 8080 inside the container). This is tunneled through FRP to a port in the 8000–8200 range on the relay VM, then served publicly via Caddy:

```
Browser → https://<vmId>.services.cbu-unicore.com
        → Caddy (relay VM, port 443)
        → localhost:<service-relay-port>
        → FRP tunnel
        → Docker container :8080
```

Caddy uses a wildcard TLS certificate (`*.services.cbu-unicore.com`) issued via DNS-01 challenge against Cloud DNS. A cron job on the relay VM regenerates the Caddyfile from Firestore state every minute.

See [SERVICE_EXPOSURE_SETUP.md](SERVICE_EXPOSURE_SETUP.md) for the full setup guide.

---

## Port Reference

| Port range | Location | Purpose |
|-----------|----------|---------|
| 7000 | GCP relay VM | FRP control — `frpc` registers tunnels here |
| 2222–2300 | GCP relay VM | SSH relay — one port per running VM |
| 8000–8200 | GCP relay VM | HTTP service relay — one port per VM with `service_relay_port` set |
| 22 | Docker container | `sshd` inside container |
| 8080 | Docker container | Web service inside container (optional) |
| 80, 443 | GCP relay VM | Caddy HTTPS termination |

---

## Security Properties

| Property | How it's achieved |
|----------|------------------|
| Provider IP never exposed | FRP tunnel is outbound-only from container; consumer only knows relay IP |
| No inbound ports on provider | Container connects *out* to relay on port 7000; no listening ports needed |
| Consumer isolation | Each VM runs in its own Docker container with no access to provider host |
| Auth on every request | Firebase JWT validated on all API endpoints and Blazor pages |
| Relay credentials | FRP server address and auth token stored in GCP Secret Manager; fetched at runtime |
| Container GCP keys | Written with `chmod 600`; consumer user cannot read them |
| Consumer-to-VM auth | SSH password (`consumer:consumer123`) — scoped to the container only |
