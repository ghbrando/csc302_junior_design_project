#!/bin/bash
# =============================================================================
# UniCore GCP Infrastructure Setup Script
# =============================================================================
# This script creates all required GCP resources for VM backup & migration:
#   - GCS bucket for volume backups
#   - Artifact Registry for container snapshots
#   - Service accounts with least-privilege permissions
#   - Secrets stored in Secret Manager
#   - Base Docker image built and pushed to registry
#
# Prerequisites:
#   - gcloud CLI installed and authenticated
#   - Docker installed and running
#   - Sufficient GCP IAM permissions (Owner or Editor)
#
# Usage:
#   chmod +x setup-gcp-resources.sh
#   ./setup-gcp-resources.sh
# =============================================================================

set -euo pipefail

# ---- Configuration ----------------------------------------------------------
PROJECT_ID="unicore-junior-design"
REGION="us-central1"
BUCKET_NAME="unicore-vm-volumes"
ARTIFACT_REPO="unicore-vm-snapshots"
PROVIDER_SA="unicore-provider-agent"
VM_SA="unicore-vm-agent"
PROVIDER_SECRET="unicore-provider-gcp-key"
VM_SECRET="unicore-vm-agent-gcp-key"
BASE_IMAGE_NAME="consumer-vm"
BASE_IMAGE_TAG="latest"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# ---- Helper functions -------------------------------------------------------
info()  { echo -e "\n\033[1;34m[INFO]\033[0m  $*"; }
ok()    { echo -e "\033[1;32m[OK]\033[0m    $*"; }
warn()  { echo -e "\033[1;33m[WARN]\033[0m  $*"; }
fail()  { echo -e "\033[1;31m[FAIL]\033[0m  $*"; exit 1; }

confirm() {
    read -rp "$1 [y/N] " response
    [[ "$response" =~ ^[Yy]$ ]] || { echo "Aborted."; exit 0; }
}

# ---- Pre-flight checks ------------------------------------------------------
info "Running pre-flight checks..."

command -v gcloud >/dev/null 2>&1 || fail "gcloud CLI not found. Install: https://cloud.google.com/sdk/docs/install"
command -v docker >/dev/null 2>&1 || fail "Docker not found. Install Docker Desktop first."

# Set project
gcloud config set project "$PROJECT_ID" 2>/dev/null
CURRENT_PROJECT=$(gcloud config get-value project 2>/dev/null)
[[ "$CURRENT_PROJECT" == "$PROJECT_ID" ]] || fail "Could not set project to $PROJECT_ID"
ok "GCP project: $PROJECT_ID"

# Enable required APIs
info "Enabling required GCP APIs..."
gcloud services enable \
    storage.googleapis.com \
    artifactregistry.googleapis.com \
    secretmanager.googleapis.com \
    iam.googleapis.com \
    --quiet
ok "APIs enabled"

echo ""
echo "============================================="
echo "  UniCore GCP Infrastructure Setup"
echo "  Project: $PROJECT_ID"
echo "  Region:  $REGION"
echo "============================================="
echo ""
confirm "Proceed with resource creation?"

# =============================================================================
# Step 1: Create GCS Bucket
# =============================================================================
info "Step 1/6: Creating GCS bucket '$BUCKET_NAME'..."

if gsutil ls -b "gs://$BUCKET_NAME" >/dev/null 2>&1; then
    warn "Bucket gs://$BUCKET_NAME already exists, skipping creation."
else
    gsutil mb -l "$REGION" -c Standard "gs://$BUCKET_NAME"
    ok "Bucket created: gs://$BUCKET_NAME"
fi

# Enable uniform bucket-level access
gsutil uniformbucketlevelaccess set on "gs://$BUCKET_NAME"
ok "Uniform bucket-level access: enabled"

# Disable versioning
gsutil versioning set off "gs://$BUCKET_NAME"
ok "Versioning: disabled"

# Set lifecycle (optional: delete objects older than 90 days)
cat > /tmp/lifecycle.json << 'EOF'
{
  "rule": [
    {
      "action": {"type": "Delete"},
      "condition": {"age": 90}
    }
  ]
}
EOF
gsutil lifecycle set /tmp/lifecycle.json "gs://$BUCKET_NAME"
rm /tmp/lifecycle.json
ok "Lifecycle policy: delete after 90 days"

ok "GCS bucket setup complete"

# =============================================================================
# Step 2: Create Artifact Registry
# =============================================================================
info "Step 2/6: Creating Artifact Registry '$ARTIFACT_REPO'..."

if gcloud artifacts repositories describe "$ARTIFACT_REPO" \
    --location="$REGION" >/dev/null 2>&1; then
    warn "Artifact Registry '$ARTIFACT_REPO' already exists, skipping creation."
else
    gcloud artifacts repositories create "$ARTIFACT_REPO" \
        --location="$REGION" \
        --repository-format=docker \
        --description="UniCore VM container snapshots" \
        --quiet
    ok "Artifact Registry created: $ARTIFACT_REPO"
fi

# Set cleanup policy: keep latest 5 versions per package
cat > /tmp/cleanup-policy.json << 'EOF'
[
  {
    "name": "keep-latest-5",
    "action": {"type": "Keep"},
    "mostRecentVersions": {
      "keepCount": 5
    }
  },
  {
    "name": "delete-old",
    "action": {"type": "Delete"},
    "condition": {
      "olderThan": "2592000s"
    }
  }
]
EOF

gcloud artifacts repositories set-cleanup-policies "$ARTIFACT_REPO" \
    --location="$REGION" \
    --policy=/tmp/cleanup-policy.json \
    --quiet 2>/dev/null || warn "Cleanup policy not set (may require newer gcloud version)"
rm /tmp/cleanup-policy.json

ok "Artifact Registry setup complete"

# =============================================================================
# Step 3: Create Service Accounts
# =============================================================================
info "Step 3/6: Creating service accounts..."

PROVIDER_SA_EMAIL="${PROVIDER_SA}@${PROJECT_ID}.iam.gserviceaccount.com"
VM_SA_EMAIL="${VM_SA}@${PROJECT_ID}.iam.gserviceaccount.com"

# -- Provider Agent SA --
if gcloud iam service-accounts describe "$PROVIDER_SA_EMAIL" >/dev/null 2>&1; then
    warn "Service account '$PROVIDER_SA' already exists, skipping creation."
else
    gcloud iam service-accounts create "$PROVIDER_SA" \
        --display-name="UniCore Provider Agent" \
        --description="Provider app - pushes container snapshots to Artifact Registry" \
        --quiet
    ok "Service account created: $PROVIDER_SA"
fi

# Grant Artifact Registry writer role on the specific repository
gcloud artifacts repositories add-iam-policy-binding "$ARTIFACT_REPO" \
    --location="$REGION" \
    --member="serviceAccount:$PROVIDER_SA_EMAIL" \
    --role="roles/artifactregistry.writer" \
    --quiet
ok "Granted roles/artifactregistry.writer to $PROVIDER_SA on $ARTIFACT_REPO"

# -- VM Agent SA --
if gcloud iam service-accounts describe "$VM_SA_EMAIL" >/dev/null 2>&1; then
    warn "Service account '$VM_SA' already exists, skipping creation."
else
    gcloud iam service-accounts create "$VM_SA" \
        --display-name="UniCore VM Agent" \
        --description="Container agent - syncs volume backups to GCS" \
        --quiet
    ok "Service account created: $VM_SA"
fi

# Grant Storage Object Admin on the specific bucket
gsutil iam ch "serviceAccount:${VM_SA_EMAIL}:roles/storage.objectAdmin" "gs://$BUCKET_NAME"
ok "Granted roles/storage.objectAdmin to $VM_SA on gs://$BUCKET_NAME"

ok "Service accounts setup complete"

# =============================================================================
# Step 4: Generate Keys & Store in Secret Manager
# =============================================================================
info "Step 4/6: Generating keys and storing in Secret Manager..."

KEY_DIR=$(mktemp -d)
trap 'rm -rf "$KEY_DIR"' EXIT

# -- Provider Agent Key --
PROVIDER_KEY_FILE="$KEY_DIR/provider-key.json"
gcloud iam service-accounts keys create "$PROVIDER_KEY_FILE" \
    --iam-account="$PROVIDER_SA_EMAIL" \
    --quiet
ok "Provider agent key generated"

# Store in Secret Manager
if gcloud secrets describe "$PROVIDER_SECRET" >/dev/null 2>&1; then
    # Secret exists, add a new version
    gcloud secrets versions add "$PROVIDER_SECRET" \
        --data-file="$PROVIDER_KEY_FILE" \
        --quiet
    ok "Updated secret: $PROVIDER_SECRET (new version)"
else
    gcloud secrets create "$PROVIDER_SECRET" \
        --data-file="$PROVIDER_KEY_FILE" \
        --replication-policy="user-managed" \
        --locations="$REGION" \
        --quiet
    ok "Created secret: $PROVIDER_SECRET"
fi

# -- VM Agent Key --
VM_KEY_FILE="$KEY_DIR/vm-agent-key.json"
gcloud iam service-accounts keys create "$VM_KEY_FILE" \
    --iam-account="$VM_SA_EMAIL" \
    --quiet
ok "VM agent key generated"

if gcloud secrets describe "$VM_SECRET" >/dev/null 2>&1; then
    gcloud secrets versions add "$VM_SECRET" \
        --data-file="$VM_KEY_FILE" \
        --quiet
    ok "Updated secret: $VM_SECRET (new version)"
else
    gcloud secrets create "$VM_SECRET" \
        --data-file="$VM_KEY_FILE" \
        --replication-policy="user-managed" \
        --locations="$REGION" \
        --quiet
    ok "Created secret: $VM_SECRET"
fi

# Keys are cleaned up automatically via trap

ok "Secrets stored in Secret Manager"

# =============================================================================
# Step 5: Grant Secret Access to Cloud Run Service Account
# =============================================================================
info "Step 5/6: Granting secret access permissions..."

# Get the default compute service account (used by Cloud Run)
PROJECT_NUMBER=$(gcloud projects describe "$PROJECT_ID" --format="value(projectNumber)")
COMPUTE_SA="${PROJECT_NUMBER}-compute@developer.gserviceaccount.com"

# Grant Secret Manager accessor to the compute SA for both secrets
for SECRET in "$PROVIDER_SECRET" "$VM_SECRET"; do
    gcloud secrets add-iam-policy-binding "$SECRET" \
        --member="serviceAccount:$COMPUTE_SA" \
        --role="roles/secretmanager.secretAccessor" \
        --quiet
    ok "Granted secretAccessor on $SECRET to compute SA"
done

# Also grant to the provider SA (so provider app can read its own key)
gcloud secrets add-iam-policy-binding "$PROVIDER_SECRET" \
    --member="serviceAccount:$PROVIDER_SA_EMAIL" \
    --role="roles/secretmanager.secretAccessor" \
    --quiet
ok "Granted secretAccessor on $PROVIDER_SECRET to provider SA"

# Grant provider SA access to VM agent secret (provider injects into containers)
gcloud secrets add-iam-policy-binding "$VM_SECRET" \
    --member="serviceAccount:$PROVIDER_SA_EMAIL" \
    --role="roles/secretmanager.secretAccessor" \
    --quiet
ok "Granted secretAccessor on $VM_SECRET to provider SA"

ok "Secret access permissions configured"

# =============================================================================
# Step 6: Build & Push Base Docker Image
# =============================================================================
info "Step 6/6: Building and pushing base Docker image..."

DOCKERFILE_PATH="$SCRIPT_DIR/Dockerfile.consumer-vm"
FULL_IMAGE="$REGION-docker.pkg.dev/$PROJECT_ID/$ARTIFACT_REPO/$BASE_IMAGE_NAME:$BASE_IMAGE_TAG"

if [ ! -f "$DOCKERFILE_PATH" ]; then
    fail "Dockerfile not found at $DOCKERFILE_PATH"
fi

# Configure Docker for Artifact Registry
gcloud auth configure-docker "$REGION-docker.pkg.dev" --quiet
ok "Docker configured for Artifact Registry"

# Build the image
docker build -t "$BASE_IMAGE_NAME:$BASE_IMAGE_TAG" -f "$DOCKERFILE_PATH" "$SCRIPT_DIR"
ok "Image built: $BASE_IMAGE_NAME:$BASE_IMAGE_TAG"

# Tag for Artifact Registry
docker tag "$BASE_IMAGE_NAME:$BASE_IMAGE_TAG" "$FULL_IMAGE"
ok "Image tagged: $FULL_IMAGE"

# Push to Artifact Registry
docker push "$FULL_IMAGE"
ok "Image pushed to Artifact Registry"

# =============================================================================
# Summary
# =============================================================================
echo ""
echo "============================================="
echo "  Setup Complete!"
echo "============================================="
echo ""
echo "Resources created:"
echo "  GCS Bucket:         gs://$BUCKET_NAME"
echo "  Artifact Registry:  $REGION-docker.pkg.dev/$PROJECT_ID/$ARTIFACT_REPO"
echo "  Provider SA:        $PROVIDER_SA_EMAIL"
echo "  VM Agent SA:        $VM_SA_EMAIL"
echo "  Provider Secret:    $PROVIDER_SECRET"
echo "  VM Agent Secret:    $VM_SECRET"
echo "  Base Image:         $FULL_IMAGE"
echo ""
echo "Next steps:"
echo "  1. Run ./verify-gcp-resources.sh to validate everything"
echo "  2. Update providerunicore/Program.cs to load secrets"
echo "  3. Proceed with Workstream 2 (Docker volumes & snapshots)"
echo ""
