# Workstream 4: GCP Infrastructure Setup

**Type:** DevOps / Infrastructure (not code)
**Depends On:** Nothing (parallel with others)
**Blocks:** Workstreams 2 (credential injection), 5 (services need GCP resources), 6 (migration needs buckets)
**Estimated Scope:** Bash/gcloud scripts, ~1–2 hours execution time
**Owner:** DevOps / GCP infrastructure person

---

## 🎯 Overview

Set up the Google Cloud Platform infrastructure needed for backups and snapshots. This includes:
- GCS bucket for storing volume backups
- Artifact Registry for storing container snapshot images
- Service accounts with appropriate permissions
- Credentials stored in Secret Manager

**Why:** Without this infrastructure, the provider app and containers have nowhere to store backups.

---

## ✅ What Needs to Be Done

See [prerequisites.md](prerequisites.md) for detailed GCP setup instructions.

**Summary of tasks:**
1. Create GCS bucket `unicore-vm-volumes`
2. Create Artifact Registry repo `unicore-vm-snapshots`
3. Create service account `unicore-provider-agent` (for provider app)
4. Create service account `unicore-vm-agent` (for containers)
5. Generate keys for both, store in Secret Manager
6. Build and push base Docker image (if using pre-built approach)
7. Test access (verify provider app can authenticate, containers can write to GCS)

---

## 📋 Execution Checklist

- [ ] GCS bucket created (`unicore-vm-volumes`)
- [ ] Artifact Registry repo created (`unicore-vm-snapshots`)
- [ ] Service account `unicore-provider-agent` created + Artifact Registry writer role
- [ ] Service account `unicore-vm-agent` created + Storage admin role
- [ ] Keys generated and stored in Secret Manager (`unicore-provider-gcp-key`, `unicore-vm-agent-gcp-key`)
- [ ] Base Docker image built and pushed (if needed)
- [ ] Current secrets audited (relay IP, FRP token documented)
- [ ] Access verified (test that provider can read/write secrets)

---

## 🚀 Can Happen in Parallel

This workstream is independent and can be done while:
- Workstream 1 (data models) — being implemented
- Workstream 2 (Docker volumes) — being implemented
- Workstream 3 (consumer volume requests) — being implemented

However, it must be **complete before**:
- Workstream 5 (services use GCP credentials)
- Workstream 6 (migration uses buckets)

---

## 📞 Coordination

Before starting, confirm:
- [ ] GCP project ID and region (likely `us-central1`)
- [ ] Who has `gcloud` CLI access?
- [ ] Should keys be rotated annually? (Set calendar reminder)
- [ ] What image base should be used? (Recommend: Debian Bookworm 12)

---

**See:** [prerequisites.md](prerequisites.md) for detailed setup steps

**Status:** Ready to execute (independent)
**Owner:** DevOps / infrastructure team
