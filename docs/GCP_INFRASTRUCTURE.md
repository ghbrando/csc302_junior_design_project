# GCP Infrastructure for VM Backup & Migration

## Overview

This documents the GCP resources set up to support volume backups and container snapshots for UniCore VMs. All resources are in `us-central1`.

## Resources

### 1. GCS Bucket — `unicore-vm-volumes`

Stores consumer VM file backups. Each container syncs `/home/consumer` to this bucket every 5 minutes via a cron job.

- **Location:** us-central1
- **Storage class:** Standard
- **Versioning:** Disabled
- **Uniform bucket-level access:** Enabled
- **Lifecycle:** Objects deleted after 90 days
- **Path structure:** `consumers/{consumerUid}/{vmId}/home/`

If a VM moves or restarts, consumer files are restored from here.

### 2. Artifact Registry — `unicore-vm-snapshots`

Stores Docker images created from container snapshots (installed packages, configs, system state).

- **Location:** us-central1
- **Format:** Docker
- **Cleanup policy:** Keep latest 1 image per VM

### 3. Service Accounts

Two service accounts with least-privilege permissions:

| Service Account | Purpose | Role |
|----------------|---------|------|
| `unicore-provider-agent` | Provider app pushes container snapshots | `roles/artifactregistry.writer` on `unicore-vm-snapshots` |
| `unicore-vm-agent` | Containers sync backups to GCS | `roles/storage.objectAdmin` on `unicore-vm-volumes` |

### 4. Secrets in Secret Manager

| Secret Name | Contains | Used By |
|------------|----------|---------|
| `unicore-provider-gcp-key` | Provider agent SA JSON key | Provider app (loaded at startup) |
| `unicore-vm-agent-gcp-key` | VM agent SA JSON key | Injected into containers by provider app |

### 5. Base Docker Image — `consumer-vm:latest`

Every consumer VM starts from this image. Located at:
```
us-central1-docker.pkg.dev/unicore-junior-design/unicore-vm-snapshots/consumer-vm:latest
```

**Pre-installed tools:**
- `google-cloud-cli` — for `gsutil rsync` backups to GCS
- `openssh-server` — consumer SSH access
- `cron` — runs backup sync every 5 minutes
- `sudo` — consumers can install packages but cannot access GCP keys or FRP config
- `vim`, `curl`, `wget`, `net-tools`, `procps`

## How It Connects to the App

1. **Provider app** loads `unicore-provider-gcp-key` from Secret Manager at startup
2. When spinning up a container, the provider app injects `unicore-vm-agent-gcp-key` into the container
3. The container startup script activates the GCP credentials and sets up a cron job
4. Every 5 minutes, `gsutil rsync` backs up `/home/consumer` to the GCS bucket
5. When snapshotting, the provider commits the container to a Docker image and pushes it to Artifact Registry

**SSH and FRP are unchanged** — the existing relay flow works exactly the same.

## Security

- Service account keys are stored in Secret Manager, never in code or git
- GCP key file inside containers has permissions `600` (root only) — consumers cannot read it
- FRP config is also protected from consumer access
- Bucket uses uniform bucket-level access (no object-level ACLs)
- Each service account has only the minimum permissions needed
