#!/bin/bash
set -e

echo "=== UniCore Container Startup ==="

# ── 1. Set consumer password (required) ──────────────────────────────────────
if [ -n "${CONSUMER_PASSWORD:-}" ]; then
    echo "consumer:${CONSUMER_PASSWORD}" | chpasswd
    echo "[OK] Consumer password set"
else
    echo "[WARN] CONSUMER_PASSWORD not set — SSH login will fail"
fi

# Ensure consumer owns their home directory (volume mount may reset ownership)
chown -R consumer:consumer /home/consumer

# ── 2. GCP credentials ───────────────────────────────────────────────────────
if [ -n "${GCP_KEY_BASE64:-}" ]; then
    echo "${GCP_KEY_BASE64}" | base64 -d > /tmp/gcp-key.json
    chmod 600 /tmp/gcp-key.json
    chown root:root /tmp/gcp-key.json
    export GOOGLE_APPLICATION_CREDENTIALS=/tmp/gcp-key.json
    gcloud auth activate-service-account --key-file=/tmp/gcp-key.json --quiet 2>/dev/null || true
    echo "[OK] GCP credentials configured"
else
    echo "[WARN] GCP_KEY_BASE64 not set — backups disabled"
fi

# ── 3. Backup cron job (GCS sync every 5 minutes) ────────────────────────────
if [ -n "${GCS_PATH:-}" ] && [ -f /tmp/gcp-key.json ]; then
    # Write the Python backup script with the dynamic GCS path
    cat > /usr/local/bin/unicore-backup.py << 'PYEOF'
#!/usr/bin/env python3
import os, sys, logging
from pathlib import Path
from google.cloud import storage
from google.oauth2 import service_account

log_dir = Path('/var/log/unicore')
log_dir.mkdir(parents=True, exist_ok=True)
logging.basicConfig(level=logging.INFO, format='[%(asctime)s] %(levelname)s: %(message)s',
    handlers=[logging.FileHandler(log_dir / 'backup.log'), logging.StreamHandler(sys.stdout)])
logger = logging.getLogger(__name__)

try:
    creds = service_account.Credentials.from_service_account_file('/tmp/gcp-key.json')
    client = storage.Client(credentials=creds, project=creds.project_id)
    bucket = client.bucket('unicore-vm-volumes')
    local_dir = Path('/home/consumer')
    if not local_dir.exists():
        sys.exit(0)
    gcs_path = os.environ.get('GCS_PATH', '')
    synced = 0
    for local_file in local_dir.rglob('*'):
        if local_file.is_file():
            blob_path = gcs_path + local_file.relative_to(local_dir.parent).as_posix()
            try:
                bucket.blob(blob_path).upload_from_filename(str(local_file))
                synced += 1
            except Exception as e:
                logger.warning(f'Failed to upload: {e}')
    logger.info(f'Synced {synced} files to gs://unicore-vm-volumes')
except Exception as e:
    logger.error(f'Sync failed: {e}')
    sys.exit(1)
PYEOF
    chmod 755 /usr/local/bin/unicore-backup.py

    # Cron needs the env var available; write it into the cron environment
    cat > /etc/cron.d/unicore-backup << CRONEOF
GOOGLE_APPLICATION_CREDENTIALS=/tmp/gcp-key.json
GCS_PATH=${GCS_PATH}
*/5 * * * * root /usr/local/bin/unicore-backup.py >> /var/log/unicore/backup.log 2>&1
CRONEOF
    chmod 644 /etc/cron.d/unicore-backup
    echo "[OK] Backup cron job configured (every 5 min)"
fi

# ── 4. Start cron daemon ─────────────────────────────────────────────────────
service cron start 2>/dev/null || cron
echo "[OK] Cron daemon started"

# ── 5. Start SSH daemon ──────────────────────────────────────────────────────
service ssh start 2>/dev/null || /usr/sbin/sshd
echo "[OK] SSH daemon started"

# ── 6. FRP relay client ──────────────────────────────────────────────────────
# Build frpc.toml from environment variables and start the relay.
# Required: FRP_SERVER_ADDR, FRP_SERVER_PORT, FRP_AUTH_TOKEN,
#           FRP_PROXY_NAME, FRP_REMOTE_PORT
# Optional: FRP_SERVICE_PROXY_NAME, FRP_SERVICE_REMOTE_PORT (HTTP service tunnel)
if [ -n "${FRP_SERVER_ADDR:-}" ] && [ -n "${FRP_PROXY_NAME:-}" ]; then
    cat > /etc/frpc/frpc.toml << FRPEOF
serverAddr = "${FRP_SERVER_ADDR}"
serverPort = ${FRP_SERVER_PORT:-7000}
auth.token = "${FRP_AUTH_TOKEN:-}"
loginFailExit = false

[[proxies]]
name = "${FRP_PROXY_NAME}"
type = "tcp"
localIP = "127.0.0.1"
localPort = 22
remotePort = ${FRP_REMOTE_PORT}
FRPEOF

    # Append service proxy block if configured
    if [ -n "${FRP_SERVICE_PROXY_NAME:-}" ] && [ -n "${FRP_SERVICE_REMOTE_PORT:-}" ]; then
        cat >> /etc/frpc/frpc.toml << FRPEOF

[[proxies]]
name = "${FRP_SERVICE_PROXY_NAME}"
type = "tcp"
localIP = "127.0.0.1"
localPort = 8080
remotePort = ${FRP_SERVICE_REMOTE_PORT}
FRPEOF
    fi

    frpc -c /etc/frpc/frpc.toml > /tmp/frpc.log 2>&1 &
    echo "[OK] FRP relay client started"
else
    echo "[WARN] FRP_SERVER_ADDR or FRP_PROXY_NAME not set — relay disabled"
fi

echo "=== UniCore Container Ready ==="

# Keep container running
exec sleep infinity
