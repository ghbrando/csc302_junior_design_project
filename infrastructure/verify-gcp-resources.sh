#!/bin/bash
# =============================================================================
# UniCore GCP Resource Verification Script
# =============================================================================
# Run this after setup-gcp-resources.sh to verify everything was created.
# =============================================================================

set -uo pipefail

PROJECT_ID="unicore-junior-design"
REGION="us-central1"
BUCKET_NAME="unicore-vm-volumes"
ARTIFACT_REPO="unicore-vm-snapshots"
PROVIDER_SA="unicore-provider-agent"
VM_SA="unicore-vm-agent"
PROVIDER_SECRET="unicore-provider-gcp-key"
VM_SECRET="unicore-vm-agent-gcp-key"
BASE_IMAGE="consumer-vm"

PASS=0
FAIL=0

check() {
    local desc="$1"
    shift
    if "$@" >/dev/null 2>&1; then
        echo -e "\033[1;32m[PASS]\033[0m $desc"
        ((PASS++))
    else
        echo -e "\033[1;31m[FAIL]\033[0m $desc"
        ((FAIL++))
    fi
}

echo "============================================="
echo "  UniCore GCP Resource Verification"
echo "  Project: $PROJECT_ID"
echo "============================================="
echo ""

gcloud config set project "$PROJECT_ID" 2>/dev/null

# GCS Bucket
echo "--- GCS Bucket ---"
check "Bucket gs://$BUCKET_NAME exists" \
    gsutil ls -b "gs://$BUCKET_NAME"

check "Uniform bucket-level access enabled" \
    bash -c "gsutil uniformbucketlevelaccess get gs://$BUCKET_NAME 2>/dev/null | grep -q 'Enabled: True'"

check "Versioning disabled" \
    bash -c "gsutil versioning get gs://$BUCKET_NAME 2>/dev/null | grep -q 'Suspended'"

# Test write access
echo "test" | gsutil cp - "gs://$BUCKET_NAME/verify-test.txt" 2>/dev/null
check "Write access to bucket" \
    gsutil ls "gs://$BUCKET_NAME/verify-test.txt"
gsutil rm "gs://$BUCKET_NAME/verify-test.txt" 2>/dev/null || true

echo ""

# Artifact Registry
echo "--- Artifact Registry ---"
check "Repository $ARTIFACT_REPO exists" \
    gcloud artifacts repositories describe "$ARTIFACT_REPO" --location="$REGION"

check "Repository format is Docker" \
    bash -c "gcloud artifacts repositories describe $ARTIFACT_REPO --location=$REGION --format='value(format)' 2>/dev/null | grep -qi 'DOCKER'"

check "Base image $BASE_IMAGE exists in registry" \
    gcloud artifacts docker images list \
        "$REGION-docker.pkg.dev/$PROJECT_ID/$ARTIFACT_REPO/$BASE_IMAGE" \
        --limit=1

echo ""

# Service Accounts
echo "--- Service Accounts ---"
PROVIDER_SA_EMAIL="${PROVIDER_SA}@${PROJECT_ID}.iam.gserviceaccount.com"
VM_SA_EMAIL="${VM_SA}@${PROJECT_ID}.iam.gserviceaccount.com"

check "Provider SA ($PROVIDER_SA) exists" \
    gcloud iam service-accounts describe "$PROVIDER_SA_EMAIL"

check "VM Agent SA ($VM_SA) exists" \
    gcloud iam service-accounts describe "$VM_SA_EMAIL"

check "Provider SA has Artifact Registry writer role" \
    bash -c "gcloud artifacts repositories get-iam-policy $ARTIFACT_REPO --location=$REGION 2>/dev/null | grep -q '$PROVIDER_SA_EMAIL'"

check "VM Agent SA has Storage Object Admin on bucket" \
    bash -c "gsutil iam get gs://$BUCKET_NAME 2>/dev/null | grep -q '$VM_SA_EMAIL'"

echo ""

# Secrets
echo "--- Secret Manager ---"
check "Secret $PROVIDER_SECRET exists" \
    gcloud secrets describe "$PROVIDER_SECRET"

check "Secret $VM_SECRET exists" \
    gcloud secrets describe "$VM_SECRET"

check "Provider secret has active version" \
    gcloud secrets versions list "$PROVIDER_SECRET" --filter="state=ENABLED" --limit=1

check "VM agent secret has active version" \
    gcloud secrets versions list "$VM_SECRET" --filter="state=ENABLED" --limit=1

check "Provider secret is readable" \
    gcloud secrets versions access latest --secret="$PROVIDER_SECRET"

check "VM agent secret is readable" \
    gcloud secrets versions access latest --secret="$VM_SECRET"

echo ""

# Summary
echo "============================================="
echo "  Results: $PASS passed, $FAIL failed"
echo "============================================="

if [ "$FAIL" -gt 0 ]; then
    echo ""
    echo "Some checks failed. Re-run setup-gcp-resources.sh or fix manually."
    exit 1
else
    echo ""
    echo "All checks passed! Infrastructure is ready."
    echo ""
    echo "Security checklist (manual review):"
    echo "  [ ] Bucket has no public access"
    echo "  [ ] Service account keys are NOT in git"
    echo "  [ ] Consumer user cannot read /tmp/gcp-key.json (perms 600, owner root)"
    echo "  [ ] FRP config is not readable by consumer user"
    echo "  [ ] Key rotation reminder set (annual)"
    exit 0
fi
