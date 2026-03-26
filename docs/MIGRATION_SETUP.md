# VM Migration Setup Guide

How to set up and test the provider-initiated VM migration feature.

---

## Overview

Providers can migrate running VMs to another provider's machine. This is useful when a provider wants to free up resources or go offline. The migration preserves the full container state (installed packages, configs) and user files.

### How It Works

1. Provider clicks **Migrate** on a running VM in their dashboard
2. A confirmation modal shows VM details and warns about downtime
3. On confirm, the system **auto-selects** the best available target provider (online, highest consistency score)
4. A `vm_migration_requests` document is created in Firestore with status `pending`
5. The **target provider's** dashboard listener picks up the request and runs the migration state machine:

| Step | Action |
|------|--------|
| 1 | Mark request as `restoring` |
| 2 | Mark old VM as `Restoring` |
| 2.5 | **Force a fresh snapshot** (`docker commit` + push to Artifact Registry) |
| 2.6 | **Force a GCS backup** of `/home/consumer` (user files) |
| 3 | Pull snapshot image on target provider |
| 4 | Create new Docker volume |
| 5 | Restore user data from GCS |
| 6 | Allocate relay port |
| 6.5 | **Stop old container** (resources freed, name released) |
| 7 | Start new container from snapshot |
| 8 | Create new VM document in Firestore |
| 9 | Mark old VM as `Migrated` |
| 10 | Start monitoring new VM |
| 11 | Mark migration request as `Completed` |
| 12 | Delete old VM document from Firestore |

The consumer sees their VM disappear and reappear with the same name, all data intact.

---

## Prerequisites

### 1. GCS Bucket Permissions

The provider service account needs access to the `unicore-vm-volumes` bucket for backup/restore. Without this, backups and migration data transfers will fail with a `storage.buckets.get` permission error.

Run these commands (once per project, not per developer):

```powershell
# Grant object read/write access
gcloud storage buckets add-iam-policy-binding gs://unicore-vm-volumes `
  --member="serviceAccount:unicore-provider-agent@unicore-junior-design.iam.gserviceaccount.com" `
  --role="roles/storage.objectAdmin"

# Grant bucket-level read access (required for GetBucketAsync check)
gcloud storage buckets add-iam-policy-binding gs://unicore-vm-volumes `
  --member="serviceAccount:unicore-provider-agent@unicore-junior-design.iam.gserviceaccount.com" `
  --role="roles/storage.legacyBucketReader"
```

### 2. Secret Manager

The GCP service account key must be stored in Secret Manager as `unicore-provider-gcp-key`. The provider app loads this at startup. If you see `[WARNING] GCP key not loaded from Secret Manager` in the logs, snapshots and GCS sync will not work.

### 3. Artifact Registry

The snapshot images are pushed to `us-central1-docker.pkg.dev/unicore-junior-design/unicore-vm-snapshots`. The service account needs Artifact Registry Writer access for push and Reader access for pull.

---

## Testing Locally

Migration requires **two provider instances** running as different accounts (source and target).

### Terminal 1 — Source Provider (Provider A)

```powershell
cd providerunicore
dotnet watch -- --urls "http://localhost:5134"
```

Log in as Provider A. Create a VM and install some packages to verify state preservation.

### Terminal 2 — Target Provider (Provider B)

```powershell
cd providerunicore
dotnet watch
# Runs on default port (5133)
```

Log in as Provider B. Make sure their node status is **Online** in the dashboard.

### Terminal 3 — Consumer

```powershell
cd consumerunicore
dotnet watch
```

Log in as a consumer to observe the VM before and after migration.

### Run the Migration

1. On **Provider A's** dashboard, find the running VM and click **Migrate**
2. Confirm in the modal
3. Watch Provider B's terminal logs for the migration steps
4. The consumer should see the VM reappear with the same name and data

### What to Verify

- Packages installed before migration are still present after
- User files in `/home/consumer` are preserved
- The old VM is removed from the consumer's stopped instances list
- The new VM shows up as `Running` on Provider B's dashboard

---

## Troubleshooting

| Error | Cause | Fix |
|-------|-------|-----|
| `storage.buckets.get denied` | Missing GCS bucket permissions | Run the `gcloud storage buckets add-iam-policy-binding` commands above |
| `No available target providers found` | No other provider is online | Start a second provider instance and set node status to Online |
| `Container name already in use` | Self-migration name conflict (same Docker daemon) | This is handled — old container is stopped at Step 6.5 before new one starts |
| `GCP key not loaded from Secret Manager` | Missing or inaccessible secret | Ensure `unicore-provider-gcp-key` exists in Secret Manager and your account can access it |
| `Push to registry failed` | Artifact Registry permissions | Grant the service account `roles/artifactregistry.writer` on the repository |
