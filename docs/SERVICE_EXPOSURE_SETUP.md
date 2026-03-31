# Service Exposure Setup Guide (Caddy + FRP + Cloud DNS)

**Feature:** Public HTTPS subdomains for consumer VMs (`https://<vmid>.services.cbu-unicore.com`)
**Relay VM IP:** `136.116.172.0`
**DNS Zone:** `services.cbu-unicore.com` (must exist in Cloud DNS)

---

## Architecture Recap

```
Browser → https://<vmid>.services.cbu-unicore.com:443
  → Caddy (relay VM) → localhost:<serviceRelayPort>  (port 8000–8200)
    → FRP TCP tunnel → Provider → Docker container:8080
```

- FRP already handles the tunnel (same pattern as SSH on 2222–2300)
- Caddy terminates HTTPS and reverse-proxies to `localhost:<serviceRelayPort>`
- One wildcard cert `*.services.cbu-unicore.com` covers all VM subdomains via DNS-01 challenge
- A cron job regenerates the Caddyfile every minute from Firestore state

---

## Prerequisites

> **This guide runs on the relay VM created in [`docs/GCP_FRP_RELAY_SETUP.md`](GCP_FRP_RELAY_SETUP.md). Complete that guide first if the relay VM does not yet exist.**

- SSH access to the relay VM (`136.116.172.0`)
- `gcloud` CLI authenticated with the `unicore-junior-design` project
- `xcaddy` installed on the relay VM (see Step 1)
- FRP server (`frps`) already running and managing SSH tunnels on the relay VM (ports 2222–2300)
- Cloud DNS zone for `services.cbu-unicore.com` already exists (create it if not; see Step 3 note)

---

## Step 1 — Install xcaddy and build Caddy with the googleclouddns plugin

Run on the **relay VM**:

```bash
# Install Go (required by xcaddy)
sudo apt-get update
sudo apt-get install -y debian-keyring debian-archive-keyring apt-transport-https curl

# Install xcaddy
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/xcaddy/gpg.key' | sudo gpg --dearmor -o /usr/share/keyrings/caddy-xcaddy-archive-keyring.gpg
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/xcaddy/debian.deb.txt' | sudo tee /etc/apt/sources.list.d/caddy-xcaddy.list
sudo apt-get update
sudo apt-get install -y xcaddy

# Build Caddy with the Google Cloud DNS plugin
xcaddy build --with github.com/caddy-dns/googleclouddns

# Install the custom binary system-wide
sudo mv caddy /usr/local/bin/caddy
sudo chmod +x /usr/local/bin/caddy

# Verify
caddy version
```

---

## Step 2 — Create the GCP service account for DNS-01 challenges

Run from your **local machine** (or Cloud Shell):

```bash
export PROJECT_ID=unicore-junior-design

# Create the service account
gcloud iam service-accounts create caddy-dns-sa \
    --description="Caddy ACME DNS-01 challenge SA for services.cbu-unicore.com" \
    --display-name="Caddy DNS SA" \
    --project=$PROJECT_ID

# Grant dns.admin role (required for TXT record create/delete during cert issuance)
gcloud projects add-iam-policy-binding $PROJECT_ID \
    --member="serviceAccount:caddy-dns-sa@${PROJECT_ID}.iam.gserviceaccount.com" \
    --role="roles/dns.admin"

# Download the JSON key — copy this file to the relay VM
gcloud iam service-accounts keys create caddy-dns-sa.json \
    --iam-account="caddy-dns-sa@${PROJECT_ID}.iam.gserviceaccount.com"
```

Copy the key to the relay VM:

```bash
scp caddy-dns-sa.json <user>@136.116.172.0:/etc/caddy/dns-sa.json
```

On the relay VM, restrict permissions:

```bash
sudo mkdir -p /etc/caddy
sudo mv /tmp/dns-sa.json /etc/caddy/dns-sa.json   # if uploaded to /tmp
sudo chmod 600 /etc/caddy/dns-sa.json
sudo chown root:root /etc/caddy/dns-sa.json
```

---

## Step 3 — Add wildcard DNS A record

Run from your **local machine**:

```bash
export PROJECT_ID=unicore-junior-design
export DNS_ZONE=services-cbu-unicore-com   # your Cloud DNS zone name for services.cbu-unicore.com
export RELAY_IP=136.116.172.0

# Wildcard record: *.services.cbu-unicore.com → relay VM
gcloud dns record-sets transaction start --zone=$DNS_ZONE --project=$PROJECT_ID

gcloud dns record-sets transaction add $RELAY_IP \
    --name="*.services.cbu-unicore.com." \
    --ttl=300 \
    --type=A \
    --zone=$DNS_ZONE \
    --project=$PROJECT_ID

gcloud dns record-sets transaction execute --zone=$DNS_ZONE --project=$PROJECT_ID

# Verify
gcloud dns record-sets list --zone=$DNS_ZONE --project=$PROJECT_ID
```

> **Note:** If the `services.cbu-unicore.com` Cloud DNS zone doesn't exist yet, create it first:
> ```bash
> gcloud dns managed-zones create services-cbu-unicore-com \
>     --description="UniCore service exposure subdomain zone" \
>     --dns-name="services.cbu-unicore.com." \
>     --project=$PROJECT_ID
> ```
> Then update your domain registrar / parent DNS zone to delegate `services.cbu-unicore.com` NS records to Cloud DNS.

---

## Step 4 — Open firewall ports 80 and 443

Run from your **local machine**:

```bash
# Allow inbound HTTP and HTTPS to the relay VM from anywhere
# (Caddy needs port 80 for ACME HTTP-01 redirect; port 443 for HTTPS)
gcloud compute firewall-rules create allow-unicore-service-https \
    --network=default \
    --priority=1000 \
    --direction=ingress \
    --action=allow \
    --source-ranges=0.0.0.0/0 \
    --rules=tcp:80,tcp:443 \
    --project=unicore-junior-design

# Confirm ports 8000–8200 are NOT open to the internet (Caddy/localhost only)
# Run this to verify no existing rule exposes that range publicly:
gcloud compute firewall-rules list \
    --filter="direction=INGRESS AND allowed[].ports:8000" \
    --project=unicore-junior-design
```

---

## Step 5 — Write the Caddyfile regen script

This script queries Firestore for all running VMs with a `service_relay_port`, then generates `/etc/caddy/Caddyfile` and reloads Caddy.

Install the Firestore Python library on the relay VM:

```bash
sudo pip3 install google-cloud-firestore
```

Create the script:

```bash
sudo tee /usr/local/bin/unicore-caddy-regen.sh > /dev/null << 'SCRIPT'
#!/usr/bin/env python3
"""
Queries Firestore for running VMs with a service_relay_port and regenerates
/etc/caddy/Caddyfile. Called by cron every minute.
"""
import subprocess
import sys
import os

os.environ.setdefault("GOOGLE_APPLICATION_CREDENTIALS", "/etc/caddy/dns-sa.json")

from google.cloud import firestore

PROJECT_ID = "unicore-junior-design"
CADDYFILE_PATH = "/etc/caddy/Caddyfile"
DOMAIN_SUFFIX = "services.cbu-unicore.com"
GCP_PROJECT = PROJECT_ID

def main():
    db = firestore.Client(project=PROJECT_ID)

    # Query running VMs that have a service_relay_port set
    vms = db.collection("virtual_machines") \
            .where("status", "==", "Running") \
            .stream()

    blocks = []
    for doc in vms:
        data = doc.to_dict()
        vm_id = data.get("vm_id", "")
        service_relay_port = data.get("service_relay_port")
        is_paused = data.get("is_paused", False)

        if not vm_id or not service_relay_port or is_paused:
            continue

        subdomain = f"{vm_id}.{DOMAIN_SUFFIX}"
        block = f"""{subdomain} {{
    reverse_proxy localhost:{service_relay_port}
    tls {{
        dns googleclouddns {{
            gcp_project {GCP_PROJECT}
        }}
    }}
}}
"""
        blocks.append(block)

    caddyfile_content = "\n".join(blocks) if blocks else "# No active service VMs\n"

    # Only reload if content changed
    try:
        with open(CADDYFILE_PATH, "r") as f:
            existing = f.read()
    except FileNotFoundError:
        existing = ""

    if existing == caddyfile_content:
        sys.exit(0)

    # Write to a temp file first so we can validate before touching the live config
    tmp_path = CADDYFILE_PATH + ".tmp"
    with open(tmp_path, "w") as f:
        f.write(caddyfile_content)

    # Validate syntax before going live — prevents a broken Caddyfile from crashing Caddy
    validate = subprocess.run(
        ["caddy", "validate", "--config", tmp_path],
        capture_output=True
    )
    if validate.returncode != 0:
        print(f"[ERROR] Caddyfile validation failed — NOT reloading:\n{validate.stderr.decode()}", file=sys.stderr)
        os.remove(tmp_path)
        sys.exit(1)

    os.replace(tmp_path, CADDYFILE_PATH)  # atomic swap

    # Reload Caddy (graceful — no downtime for existing connections)
    result = subprocess.run(["systemctl", "reload", "caddy"], capture_output=True)
    if result.returncode != 0:
        print(f"[ERROR] caddy reload failed: {result.stderr.decode()}", file=sys.stderr)
        sys.exit(1)

    print(f"[OK] Caddyfile updated with {len(blocks)} VM block(s) and Caddy reloaded.")

if __name__ == "__main__":
    main()
SCRIPT

sudo chmod +x /usr/local/bin/unicore-caddy-regen.sh
```

Test it manually:

```bash
sudo /usr/local/bin/unicore-caddy-regen.sh
cat /etc/caddy/Caddyfile
```

---

## Step 6 — Set up Caddy as a systemd service

```bash
# Create caddy system user and directories
sudo groupadd --system caddy 2>/dev/null || true
sudo useradd --system --gid caddy --create-home --home-dir /var/lib/caddy \
    --shell /usr/sbin/nologin --comment "Caddy web server" caddy 2>/dev/null || true

sudo mkdir -p /etc/caddy /var/log/caddy
sudo chown caddy:caddy /etc/caddy /var/log/caddy
sudo chmod 750 /etc/caddy

# Give Caddy access to the DNS key
sudo chown root:caddy /etc/caddy/dns-sa.json
sudo chmod 640 /etc/caddy/dns-sa.json

# Create the systemd unit file
sudo tee /etc/systemd/system/caddy.service > /dev/null << 'EOF'
[Unit]
Description=Caddy (UniCore service exposure)
Documentation=https://caddyserver.com/docs/
After=network.target network-online.target
Requires=network-online.target

[Service]
Type=notify
User=caddy
Group=caddy
ExecStart=/usr/local/bin/caddy run --environ --config /etc/caddy/Caddyfile
ExecReload=/usr/local/bin/caddy reload --config /etc/caddy/Caddyfile --force
TimeoutStopSec=5s
LimitNOFILE=1048576
PrivateTmp=true
ProtectSystem=full
AmbientCapabilities=CAP_NET_BIND_SERVICE
Environment=GOOGLE_APPLICATION_CREDENTIALS=/etc/caddy/dns-sa.json
Environment=GCP_PROJECT=unicore-junior-design

[Install]
WantedBy=multi-user.target
EOF

# Enable and start Caddy
sudo systemctl daemon-reload
sudo systemctl enable caddy
sudo systemctl start caddy
sudo systemctl status caddy
```

---

## Step 7 — Add the cron job

```bash
# Run the regen script every minute as root
echo '*/1 * * * * root /usr/local/bin/unicore-caddy-regen.sh >> /var/log/caddy/regen.log 2>&1' \
    | sudo tee /etc/cron.d/unicore-caddy-regen

sudo chmod 0644 /etc/cron.d/unicore-caddy-regen

# Restart cron to pick up the new file
sudo systemctl restart cron
```

---

## Step 8 — How VMs get `service_relay_port`

The regen script only generates a Caddy block for VMs that have `service_relay_port` set in Firestore. This field must be assigned during VM creation by the provider app.

**Model fields** (defined in [`unicore.shared/Models/VirtualMachine.cs`](../unicore.shared/Models/VirtualMachine.cs)):

| C# Property | Firestore Field | Purpose |
|-------------|----------------|---------|
| `ServicePort` | `service_port` | Port inside the Docker container the web server listens on (typically 8080) |
| `ServiceRelayPort` | `service_relay_port` | Port on the relay VM that FRP tunnels to `ServicePort` — must be in range 8000–8200 |
| `ServiceUrl` | `service_url` | Computed public URL: `https://<vm_id>.services.cbu-unicore.com` |

**Port allocation strategy** (to implement in `VmService` during VM creation):

```csharp
// Pick the next available port in 8000–8200, avoiding collisions with other active VMs
int AssignServiceRelayPort(IEnumerable<VirtualMachine> existingVms)
{
    var used = existingVms
        .Where(v => v.ServiceRelayPort.HasValue)
        .Select(v => v.ServiceRelayPort!.Value)
        .ToHashSet();

    for (int port = 8000; port <= 8200; port++)
        if (!used.Contains(port)) return port;

    throw new InvalidOperationException("No available service relay ports (8000–8200 exhausted).");
}
```

After assigning the port, set all three fields on the VM document and include the corresponding `frpc` TCP proxy config so FRP registers the tunnel on that port.

> **Note:** `service_relay_port` should only be set on VMs that expose a web service. Pure SSH-only VMs should leave it null — the regen script already skips those.

---

## Verification Checklist

After setup, run through this checklist:

```bash
# 1. Confirm wildcard DNS resolves to the relay VM
dig +short "*.services.cbu-unicore.com"
# Expected: 136.116.172.0

# 2. Confirm Caddy is running
sudo systemctl status caddy

# 3. Confirm ports 80 and 443 are listening
sudo ss -tlnp | grep -E ':80|:443'

# 4. Confirm service ports 8000–8200 are NOT exposed externally
# (No output expected from the firewall check in Step 4)

# 5. Check Caddy cert issuance (after first VM with service_relay_port is created)
sudo caddy list-certs 2>/dev/null || true
journalctl -u caddy -n 50 --no-pager

# 6. Check the regen script output
tail -f /var/log/caddy/regen.log

# 7. Manually test a running VM's service URL
# curl https://<vmid>.services.cbu-unicore.com
```

---

## Troubleshooting

| Symptom | Likely Cause | Fix |
|---------|-------------|-----|
| Cert issuance fails (`SERVFAIL` / `DNS problem`) | Service account lacks `dns.admin` on the correct project | Re-run Step 2; verify `--project` flag matches the Cloud DNS zone's project |
| `*.services.cbu-unicore.com` doesn't resolve | Wildcard record not created, or DNS delegation missing | Re-run Step 3; check NS records on parent zone |
| 502 Bad Gateway | FRP tunnel not connected yet (VM still booting) | Wait ~30s for container to download frpc and register |
| Caddyfile not updating | Cron not running, or Python script error | Check `/var/log/caddy/regen.log`; run script manually |
| `caddy reload` fails | Syntax error in generated Caddyfile | Run `caddy validate --config /etc/caddy/Caddyfile` |
| Port 443 refused | Firewall rule not applied or wrong network | Recheck Step 4; ensure relay VM has the `allow-unicore-service-https` rule |
