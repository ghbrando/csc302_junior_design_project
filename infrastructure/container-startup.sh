#!/bin/bash
set -e

echo "=== UniCore Container Startup ==="

# 1. Verify GCP credentials
if [ -f /tmp/gcp-key.json ]; then
    chmod 600 /tmp/gcp-key.json
    chown root:root /tmp/gcp-key.json
    export GOOGLE_APPLICATION_CREDENTIALS=/tmp/gcp-key.json
    gcloud auth activate-service-account --key-file=/tmp/gcp-key.json --quiet 2>/dev/null || true
    echo "[OK] GCP credentials configured"
else
    echo "[WARN] GCP credentials not found at /tmp/gcp-key.json - backups disabled"
fi

# 2. Ensure consumer user exists
if ! id -u consumer > /dev/null 2>&1; then
    useradd -m -s /bin/bash consumer
    echo "[OK] Consumer user created"
fi

# 3. Set up cron job for periodic GCS backup sync (every 5 minutes)
if [ -n "${GCS_BUCKET:-}" ] && [ -n "${GCS_PATH:-}" ] && [ -f /tmp/gcp-key.json ]; then
    cat > /etc/cron.d/unicore-backup << CRONEOF
GOOGLE_APPLICATION_CREDENTIALS=/tmp/gcp-key.json
*/5 * * * * root gsutil -m rsync -r /home/consumer gs://${GCS_BUCKET}/${GCS_PATH}home/ >> /var/log/unicore/backup.log 2>&1
CRONEOF
    chmod 644 /etc/cron.d/unicore-backup
    echo "[OK] Backup cron job configured (every 5 min)"
fi

# 4. Start cron daemon
service cron start 2>/dev/null || cron
echo "[OK] Cron daemon started"

# 5. Start SSH daemon
service ssh start 2>/dev/null || /usr/sbin/sshd
echo "[OK] SSH daemon started"

# 6. Start FRP relay client (if config exists)
if [ -f /etc/frpc/frpc.toml ]; then
    frpc -c /etc/frpc/frpc.toml &
    echo "[OK] FRP relay client started"
fi

echo "=== UniCore Container Ready ==="

# Keep container running
exec sleep infinity
