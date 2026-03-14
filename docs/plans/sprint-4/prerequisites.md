# Prerequisites: GCP Infrastructure & Setup

**Status:** Planning (not implemented yet)
**Owner:** DevOps / GCP Team
**Timeline:** Complete before Workstream 2 begins

---

## 📋 What Must Exist Before Implementation Starts

This is not code work — it's infrastructure setup. **Nothing in Workstreams 1–7 can begin until this is complete.**

---

## 🎯 Required GCP Resources

### 1. **GCS Bucket for Volume Backups**

**Purpose:** Store incremental backups of `/home/consumer` directory

**Requirements:**
- Bucket name: `unicore-vm-volumes`
- Location: `us-central1` (same as provider VMs)
- Storage class: Standard
- Versioning: Disabled (keep only latest)
- Uniform bucket-level access: Enabled
- Path structure: `consumers/{consumerUid}/{vmId}/home/`

**Verification:**
```bash
gsutil ls gs://unicore-vm-volumes/
# Should return empty or list any test paths created
```

---

### 2. **Artifact Registry for Container Snapshots**

**Purpose:** Store Docker container images (OS + installed packages + configs)

**Requirements:**
- Repository name: `unicore-vm-snapshots`
- Location: `us-central1`
- Repository format: Docker
- Cleanup policy: Keep latest 5 images per VM (optional; can delete old images to save cost)

**Verification:**
```bash
gcloud artifacts repositories describe unicore-vm-snapshots \
  --location=us-central1
# Should show repository details
```

---

### 3. **Service Account #1: Provider Agent**

**Purpose:** Provider app pushes container images to Artifact Registry

**Name:** `unicore-provider-agent`

**Permissions:**
- Role: `roles/artifactregistry.writer` on `unicore-vm-snapshots` repository

**Service Account Key:**
- Store in GCP Secret Manager as: `unicore-provider-gcp-key`
- Format: JSON (standard service account key)
- Used by: Provider app at startup (Program.cs)

**Verification:**
```bash
gcloud iam service-accounts describe unicore-provider-agent@PROJECT_ID.iam.gserviceaccount.com
gcloud secrets describe unicore-provider-gcp-key
```

---

### 4. **Service Account #2: VM Agent**

**Purpose:** Containers (running inside provider VMs) authenticate to GCS for backup syncing

**Name:** `unicore-vm-agent`

**Permissions:**
- Role: `roles/storage.objectAdmin` on `unicore-vm-volumes` bucket
- Scoping: Each container gets a unique key that can only access its own path (`consumers/{uid}/{vmId}/*`)
  - Optional: Use GCP IAM Conditions to enforce path-level scoping (advanced)
  - Simpler: Use different keys per container (current design)

**Service Account Key:**
- Store in GCP Secret Manager as: `unicore-vm-agent-gcp-key`
- Format: JSON (standard service account key)
- Used by: Injected into container at startup (by DockerService)

**Verification:**
```bash
gcloud iam service-accounts describe unicore-vm-agent@PROJECT_ID.iam.gserviceaccount.com
gcloud secrets describe unicore-vm-agent-gcp-key
```

---

## 🔑 Secret Manager Setup

**What should already exist (from current setup):**
- Relay IP address (e.g., `FRP_RELAY_SERVER_ADDR`)
- FRP relay authentication token (e.g., `FRP_RELAY_AUTH_TOKEN`)

**What needs to be added:**
- `unicore-provider-gcp-key` ← Service account key for provider app
- `unicore-vm-agent-gcp-key` ← Service account key for containers

**Verification:**
```bash
gcloud secrets list
# Should include all 4+ secrets
```

---

## 🐳 Docker Image Preparation

**Recommended Approach:** Hybrid (pre-built base image + runtime startup script)

### Option A: Pre-Built Base Image (Recommended)

**Purpose:** Pre-installed common tools (faster startup, reproducible)

**Contents (Dockerfile):**
```dockerfile
FROM debian:bookworm

# Install required tools
RUN apt-get update && apt-get install -y \
    google-cloud-cli \
    openssh-server \
    sudo \
    cron \
    vim \
    curl \
    net-tools \
    ca-certificates

# Create directories for config/logs
RUN mkdir -p /etc/frpc /var/log/unicore

# Set up SSH to accept root login (for initial setup)
RUN sed -i 's/#PermitRootLogin prohibit-password/PermitRootLogin yes/' /etc/ssh/sshd_config

CMD ["/bin/bash"]
```

**Build & Push:**
```bash
docker build -t consumer-vm:latest -f Dockerfile .
docker tag consumer-vm:latest \
  us-central1-docker.pkg.dev/PROJECT_ID/unicore-images/consumer-vm:latest
docker push us-central1-docker.pkg.dev/PROJECT_ID/unicore-images/consumer-vm:latest
```

**Registry Path:** `us-central1-docker.pkg.dev/PROJECT_ID/unicore-images/consumer-vm:latest`

---

### Option B: Startup Script Approach

If using standard images without pre-built base:
- Provider app injects startup script at container creation time
- Script runs as root before consumer can log in
- Installs tools, sets up security, injects credentials

**File location (in repo):** `providerunicore/Assets/container-startup.sh`

---

## ⚙️ Current Secret Manager Audit

**Action Required:** Document what's already in Secret Manager

**Questions to answer:**
- [ ] What is the exact secret name for relay IP? (e.g., `FRP_RELAY_SERVER_ADDR`?)
- [ ] What is the exact secret name for FRP token? (e.g., `FRP_RELAY_AUTH_TOKEN`?)
- [ ] Are these stored as GitHub secrets, GCP Secret Manager, or both?
- [ ] Are they version-controlled in appsettings.json or loaded at runtime?

---

## 🔐 Security Checklist

- [ ] GCS bucket has uniform bucket-level access enabled (no object-level ACLs)
- [ ] VM agent key can only write to its own path (via IAM role scope or per-container key)
- [ ] Service account keys are rotated annually (set a calendar reminder)
- [ ] Keys are NOT checked into git (use Secret Manager only)
- [ ] Container startup script sets file permissions on injected GCP key to `600` (read-only by root)
- [ ] Consumer user cannot read `/tmp/gcp-key.json` or FRP config files

---

## 📝 Handoff Checklist

Before Workstreams 1–7 begin, this must be complete:

- [ ] GCS bucket `unicore-vm-volumes` exists and is accessible
- [ ] Artifact Registry `unicore-vm-snapshots` exists and is accessible
- [ ] Service account `unicore-provider-agent` created with Artifact Registry writer role
- [ ] Service account `unicore-vm-agent` created with Storage admin role
- [ ] `unicore-provider-gcp-key` secret stored in Secret Manager
- [ ] `unicore-vm-agent-gcp-key` secret stored in Secret Manager
- [ ] Base Docker image built and pushed to registry (if using pre-built approach)
- [ ] Current secrets in Secret Manager documented (relay IP, FRP token)
- [ ] Security checklist reviewed and approved

---

## 🚀 Execution Steps (High-Level)

1. **Create GCS bucket** (5 min)
2. **Create Artifact Registry repo** (5 min)
3. **Create two service accounts** (10 min)
4. **Generate keys, store in Secret Manager** (10 min)
5. **Build & push Docker base image** (15 min, includes build time)
6. **Audit current secrets, document** (15 min)
7. **Test access** (verify app can read secrets, containers can write to GCS) (20 min)

**Total time:** ~1 hour of DevOps effort + build time

---

## 🔗 References

- [GCP Artifact Registry Docs](https://cloud.google.com/artifact-registry/docs)
- [GCP Secret Manager Docs](https://cloud.google.com/secret-manager/docs)
- [GCP Cloud Storage Docs](https://cloud.google.com/storage/docs)
- [Workload Identity Federation](https://cloud.google.com/iam/docs/workload-identity-federation) (optional for GitHub CI/CD)

---

**Status:** Blocking all development work
**Priority:** Critical path
**Owner:** DevOps team
