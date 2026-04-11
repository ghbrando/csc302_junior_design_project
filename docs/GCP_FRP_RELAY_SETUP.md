# GCP FRP Relay Setup

Guide for setting up a GCP VM as a TCP relay between Docker containers (providers) and consumers using [FRP (Fast Reverse Proxy)](https://github.com/fatedier/frp).

```
Consumer ──SSH──> GCP VM :2222–2300 ──FRP tunnel──> Docker container :22
```

Each running VM gets its own relay port allocated dynamically from the 2222–2300 range (SSH) and optionally from 8000–8200 (HTTP services). Port assignments are stored in Firestore on the `VirtualMachine` document (`relay_port`, `service_relay_port`).

---

## Prerequisites

- `gcloud` CLI installed and authenticated (`gcloud auth login`)
- GCP project with billing enabled (project: `unicore-junior-design`)
- Docker container with SSH (`sshd`) running inside

---

## Phase 1: Create the GCP VM

> **PowerShell note**: Do not use backslash `\` for line continuation. Use backtick `` ` `` or run as a single line.

### 1.1 Create the VM
```powershell
gcloud compute instances create frp-relay --project=unicore-junior-design --zone=us-central1-a --machine-type=e2-micro --image-family=debian-12 --image-project=debian-cloud --tags=frp-relay --boot-disk-size=10GB
```

### 1.2 Reserve a static external IP
```powershell
gcloud compute addresses create frp-relay-ip --region=us-central1
gcloud compute instances add-access-config frp-relay --access-config-name="External NAT" --address=frp-relay-ip --zone=us-central1-a
```
> Skip this if an ephemeral IP is acceptable (changes on VM restart).

### 1.3 Create firewall rules
```powershell
# FRP control port — frpc (Docker containers) connect here to register tunnels
gcloud compute firewall-rules create allow-frp-server --project=unicore-junior-design --allow=tcp:7000 --target-tags=frp-relay --description="FRP server control port"

# SSH relay range — one port per running VM
gcloud compute firewall-rules create allow-frp-forwarded-ssh --project=unicore-junior-design --allow=tcp:2222-2300 --target-tags=frp-relay --description="FRP forwarded SSH ports for consumers (one per VM)"

# HTTP service relay range — for VMs that expose a web service
gcloud compute firewall-rules create allow-frp-service-ports --project=unicore-junior-design --allow=tcp:8000-8200 --target-tags=frp-relay --description="FRP forwarded HTTP service ports"
```

Port layout:
| Port / Range | Purpose |
|-------------|---------|
| 7000 | FRP server control (`frpc` in each container registers here) |
| 2222–2300 | Consumer SSH entry — one port per running VM |
| 8000–8200 | HTTP service relay — one port per VM with an exposed web service |
| 80, 443 | Caddy HTTPS termination (see [SERVICE_EXPOSURE_SETUP.md](SERVICE_EXPOSURE_SETUP.md)) |

---

## Phase 2: Install FRP Server on the GCP VM

### 2.1 SSH into the VM
```bash
gcloud compute ssh frp-relay --zone=us-central1-a --project=unicore-junior-design
```

### 2.2 Download and extract FRP
```bash
# Check latest at https://github.com/fatedier/frp/releases
wget https://github.com/fatedier/frp/releases/download/v0.61.0/frp_0.61.0_linux_amd64.tar.gz
tar -xzf frp_0.61.0_linux_amd64.tar.gz
cd frp_0.61.0_linux_amd64
```

### 2.3 Configure the FRP server
```bash
cat > frps.toml << 'EOF'
bindPort = 7000
auth.token = "your-secret-token-here"
EOF
```
> Replace `your-secret-token-here` with a strong shared secret. Both server and client must use the same token.

### 2.4 Start the FRP server
```bash
# Foreground (for testing):
./frps -c frps.toml

# Background (persistent):
nohup ./frps -c frps.toml > frps.log 2>&1 &
```
Successful start looks like:
```
frps started successfully
```

---

## Phase 3: Configure FRP Client in the Docker Container (Provider)

### 3.1 Get the GCP VM's external IP
```bash
# Run this locally (not on the VM)
gcloud compute instances describe frp-relay \
  --zone=us-central1-a \
  --project=unicore-junior-design \
  --format='get(networkInterfaces[0].accessConfigs[0].natIP)'
```

### 3.2 Install FRP in the Docker container
```bash
# Inside the container — wget likely not available, use curl
apt-get update && apt-get install -y curl
curl -L -o frp.tar.gz https://github.com/fatedier/frp/releases/download/v0.61.0/frp_0.61.0_linux_amd64.tar.gz
tar -xzf frp.tar.gz
```

### 3.3 Configure the FRP client

The provider app generates this config dynamically at container startup time. `RELAY_PORT` is allocated by `MigrationService.AllocateRelayPortAsync()` from the 2222–2300 range and stored in Firestore before the container starts.

```bash
cat > frpc.toml << EOF
serverAddr = "GCP_VM_EXTERNAL_IP"
serverPort = 7000
auth.token = "your-secret-token-here"

[[proxies]]
name = "ssh-<vmId>"
type = "tcp"
localIP = "127.0.0.1"
localPort = 22
remotePort = RELAY_PORT          # e.g. 2223 — unique per VM

[[proxies]]
name = "svc-<vmId>"
type = "tcp"
localPort = 8080
remotePort = SERVICE_RELAY_PORT  # e.g. 8001 — only set if VM exposes a web service
EOF
```
> Replace `GCP_VM_EXTERNAL_IP`, `your-secret-token-here`, `RELAY_PORT`, and `SERVICE_RELAY_PORT` with real values. In production these come from GCP Secret Manager and Firestore.

### 3.4 Start the FRP client
```bash
# frpc binary is inside the extracted folder
frp_0.61.0_linux_amd64/frpc -c frpc.toml
```
Successful registration looks like:
```
[docker-ssh] start proxy success
```

---

## Phase 4: Test the Connection (Consumer)

From your local machine:
```bash
ssh -p RELAY_PORT CONTAINER_USER@GCP_VM_EXTERNAL_IP
```
Replace `RELAY_PORT` with the port allocated for this VM (visible in Firestore `virtual_machines/<vmId>.relay_port`).

This SSH connection goes: `client → GCP VM :RELAY_PORT → FRP tunnel → Docker container :22`

In the consumer web app, `WebShellService` resolves this endpoint automatically from Firestore and connects via SSH.NET.

---

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| frpc can't connect to server | Verify firewall rule for port 7000; check frps is running |
| Consumer can't reach port 2222 | Verify firewall rule for port 2222; check frpc proxy is active |
| Auth error in frpc logs | Ensure `auth.token` matches exactly on both sides |
| SSH refused inside container | Verify `sshd` is running: `service ssh start` or `sshd -D` |
