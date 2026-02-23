# SSH Access to Consumer VMs (PoC)

Quick guide to SSH into consumer VMs spawned from the provider dashboard.

## Overview

When you launch a VM via the "Launch VM" button, DockerService automatically:
1. Installs OpenSSH server in the container
2. Creates a `consumer` user with password `consumer123`
3. Maps SSH port 22 to a random host port (e.g., 55004)
4. Starts the SSH daemon

This works with any standard Linux image (Debian, Ubuntu, Alpine, etc.).

## Quick Start

### 1. Launch a VM

- Open provider dashboard: http://localhost:5133
- Click "LAUNCH VM"
- Select an image (Debian 12 is recommended)
- Click "Launch"

### 2. Find the SSH Port

```bash
docker ps
```

Look for your container's port mapping, e.g.:
```
PORTS
0.0.0.0:55004->22/tcp   vm-abc12345
```

### 3. SSH In

```bash
ssh -p 55004 consumer@localhost
```

Password: `consumer123`

When prompted about the host key (first time only), type `yes`.

### 4. Done!

```bash
$ whoami
consumer
$ hostname
(container id)
$ uname -a 
(kernel and arch info)
$ cat /etc/os-release
(distribution/version)
$ exit
```

## Details

| Item | Value |
|------|-------|
| Username | `consumer` |
| Password | `consumer123` |
| SSH Port (container) | 22 |
| Host Port | Random (check `docker ps`) |
| Host IP | localhost (127.0.0.1) |

## How It Works

The SSH installation happens in `DockerService.StartContainerAsync()` via a startup command that:

```bash
apt-get update && \
apt-get install -y openssh-server && \
mkdir -p /run/sshd && \
useradd -m -s /bin/bash consumer && \
echo 'consumer:consumer123' | chpasswd && \
sed -i 's/#PermitRootLogin prohibit-password/PermitRootLogin yes/' /etc/ssh/sshd_config && \
/usr/sbin/sshd -D
```

This runs automatically when the container starts, with no custom Dockerfile needed.

## Multiple VMs

Each VM gets its own random port:

```
VM 1 → Port 55000 → SSH
VM 2 → Port 55001 → SSH
VM 3 → Port 55002 → SSH
```

Run `docker ps` to see all active ports.

## Troubleshooting

### Connection Refused
- Container not running? Check: `docker ps`
- Wrong port? Check output from `docker ps`
- Container just started? Wait 2-3 seconds

### Authentication Failed
- Check credentials: `consumer` / `consumer123` (no extra spaces)
- Correct port from `docker ps`?

### Host Key Warning (First Time Only)
```
Are you sure you want to continue connecting (yes/no)?
```
Type `yes` and press Enter. This is normal — SSH saves the fingerprint locally.

### Host Key Changed Warning (Same Port, Different Container)
```bash
ssh-keygen -R [localhost]:55004
```
Then SSH in again.

## Notes

- This is a **PoC** — password auth only (for now)
- Future: Switch to key-based authentication
- Images installed on first launch, so first connection may take 5-10 seconds
- SSH runs in foreground mode (`sshd -D`), so container keeps running as long as sshd is active

## Example

```bash
# Terminal 1: Provider dashboard
cd providerunicore
dotnet watch
# Open http://localhost:5133

# Terminal 2: Launch a VM and SSH in
docker ps                          # After clicking Launch Button
ssh -p 55004 consumer@localhost    # Use port from docker ps output
# Password: consumer123

# Now you're inside the container
consumer@a1b2c3d4:~$ ls -la
consumer@a1b2c3d4:~$ cat /etc/os-release
consumer@a1b2c3d4:~$ exit
```

---

**Status**: ✅ Working PoC  
**Next Steps**: Dashboard UI integration, key-based auth, web terminal
