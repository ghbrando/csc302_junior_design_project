<div align="center">

# UniCore

**Distributed computing, democratized.**

*Rent your spare CPU cycles and access compute on demand, without going through the major cloud providers.*

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com/)
[![Blazor](https://img.shields.io/badge/Blazor-Server-512BD4?style=flat-square&logo=blazor)](https://dotnet.microsoft.com/apps/aspnet/web-apps/blazor)
[![GCP](https://img.shields.io/badge/Google_Cloud-Platform-4285F4?style=flat-square&logo=google-cloud)](https://cloud.google.com/)
[![Docker](https://img.shields.io/badge/Docker-Containers-2496ED?style=flat-square&logo=docker)](https://www.docker.com/)
[![Firebase](https://img.shields.io/badge/Firebase-Auth-FFCA28?style=flat-square&logo=firebase)](https://firebase.google.com/)
[![License](https://img.shields.io/badge/License-MIT-green?style=flat-square)](LICENSE)

</div>

---

## What is UniCore?

UniCore is a peer-to-peer distributed computing marketplace. Anyone with a spare machine can become a **provider**, earning revenue by renting their idle CPU and RAM. Anyone who needs compute can become a **consumer**, spinning up a Linux VM in seconds from a browser with no cloud account required.

The core problem UniCore solves is deceptively hard: *how do you let a consumer SSH into a VM running on a stranger's laptop, behind their home router, without exposing that laptop to the internet?*

The answer is **UniCore's GCP relay architecture**, a trusted central hub that brokers all traffic between consumers and providers. Providers only ever make outbound connections (like visiting a website), so their machines are never exposed. Consumers get a fully interactive browser-based terminal with no port forwarding, static IPs, or firewall rules required.

---

## Philosophy

The goal of UniCore is simple: compute should be as accessible as WiFi.

The major cloud providers have made infrastructure powerful but expensive, opaque, and centralized. UniCore takes the opposite approach:

- **Providers own their hardware.** Your machine, your rules. Configure your own resource limits and disconnect whenever you want.
- **Consumers get simplicity.** Open a browser, pick a VM, get a terminal. No AWS account or IAM policies needed.
- **Security is non-negotiable.** Provider machines are never directly reachable. All traffic flows through a hardened GCP relay with JWT authentication on every connection. Consumer workloads run in isolated Docker containers.
- **The platform stays minimal.** UniCore's hub is stateless and horizontally scalable.

---

## Architecture

UniCore is built around a three-party relay model:

```
┌─────────────────┐                                           ┌─────────────────┐
│    CONSUMER     │                                           │    PROVIDER     │
│   (Browser)     │                                           │   (Desktop App) │
└────────┬────────┘                                           └────────┬────────┘
         │                                                             │
         │  SSH (via SSH.NET)           ┌─────────────────────┐       │ Docker.DotNet
         └─────────────────────────────>│   GCP FRP Relay VM  │       │
                                        │   (frps, port 7000) │       ▼
                                        │   SSH: 2222–2300    │  Docker Container
                                        │   HTTP: 8000–8200   │  (frpc inside,
                                        └─────────────────────┘   sshd running)
                                                  ▲
                                         FRP tunnel (outbound
                                         from container → relay)

                              All shared state via Firestore
                        (real-time listeners, no message broker needed)
```

### How It Works

1. **Consumer rents a VM.** The consumer picks resource specs in the browser. UniCore's matchmaking service queries Firestore for online providers, ranks them by consistency score, and assigns the best match.

2. **Provider starts a Docker container.** The provider app receives the VM assignment via a Firestore listener. It creates a Docker container that — as part of its startup script — downloads FRP v0.61.0, writes a `frpc.toml` with the relay address and auth token, and starts `frpc`. The FRP client connects **outbound** to the GCP relay VM on port 7000 and registers a TCP proxy for SSH (port 22 → a relay port in the 2222–2300 range). The provider's machine never opens any inbound ports.

3. **Consumer gets a terminal.** Once SSH responds on the relay port, the VM is marked Running in Firestore. The consumer opens the terminal; the browser uses SSH.NET to connect to `relay-ip:relay-port` as user `consumer`. That SSH connection travels through the FRP tunnel back to `sshd` running inside the Docker container — the provider's IP is never exposed.

4. **Health monitoring.** A dedicated Heartbeat Service (Cloud Run) TCP-probes each VM's relay port every 10 seconds and updates the provider's `consistency_score` in Firestore. Providers with degraded scores appear lower in matchmaking results.

For a deep-dive, see [docs/NETWORKING_ARCHITECTURE.md](docs/NETWORKING_ARCHITECTURE.md).

---

## Features

### For Providers
- **Live dashboard** with real-time CPU, RAM, and GPU metrics and historical graphs
- **One-click VM launch:** Docker containers provisioned automatically with SSH and FRP tunnel registration
- **Resource controls:** set per-VM CPU core limits and RAM caps
- **Revenue tracking:** view session earnings and payout history based on UniCore's platform rates
- **Emergency stop:** shut down all running VMs instantly from the dashboard
- **Firestore-driven control:** pause, resume, and stop commands delivered in real time via Firestore listeners — no polling

### For Consumers
- **Browser-based terminal** powered by [xterm.js](https://xtermjs.org/) — full interactive shell, no local client needed
- **Automatic matchmaking:** specify CPU, RAM, disk, and image; the platform selects the best available provider by consistency score automatically
- **Pause & resume:** freeze a VM to stop billing without losing the session state
- **Persistent storage:** home directory synced to GCS every 5 minutes; container snapshots every 2 hours
- **Live migration:** move a running VM to a different provider with full file system and package state preserved

### Platform
- **Zero provider exposure:** outbound-only FRP connections, no open ports, no NAT traversal
- **Docker isolation:** each VM runs in its own container; consumers can't reach provider infrastructure
- **Automatic snapshots:** container state committed to GCP Artifact Registry every 2 hours; user files synced to GCS every 5 minutes
- **Live migration:** VMs can be migrated to a different provider with full state preservation (snapshot + GCS restore)
- **Secure secrets:** relay credentials stored in GCP Secret Manager, never in config files

---

## Tech Stack

| Layer | Technology |
|---|---|
| Provider App | C# .NET 10, ASP.NET Core Blazor (Server) |
| Consumer App | C# .NET 10, ASP.NET Core Blazor, SSH.NET (Renci.SshNet) |
| Heartbeat Service | ASP.NET Core Worker Service (GCP Cloud Run) |
| SSH Tunneling | FRP (Fast Reverse Proxy) v0.61.0 — GCP Compute Engine VM |
| Database | Google Cloud Firestore (NoSQL, real-time listeners) |
| Authentication | Firebase Auth (JWT tokens) |
| Containerization | Docker, Docker.DotNet SDK |
| Volume Backups | Google Cloud Storage (`unicore-vm-volumes`) |
| Container Snapshots | GCP Artifact Registry (`unicore-vm-snapshots`) |
| Secret Management | GCP Secret Manager |
| Service Exposure | Caddy reverse proxy + wildcard DNS (`*.services.cbu-unicore.com`) |

---

## Project Structure

```
csc302_junior_design_project/
├── providerunicore/          # Provider desktop application (Blazor Server, port 5133)
│   ├── Components/           # Blazor UI pages and components
│   ├── Services/             # Business logic: Docker, VMs, snapshots, migration, monitoring
│   └── Program.cs            # DI registration and app startup
├── consumerunicore/          # Consumer web application (Blazor Server, port 7073)
│   ├── Components/           # Blazor UI pages (Dashboard, WebShell terminal, Settings)
│   ├── Controllers/          # REST API endpoints (VM lifecycle, auth)
│   └── Services/             # Matchmaking, SSH connection info, VM mutations
├── unicore.shared/           # Shared class library — models + repository pattern
│   ├── Models/               # Firestore models: Provider, Consumer, VirtualMachine, MachineSpecs, Payout
│   └── Repositories/         # IFirestoreRepository<T> + FirestoreRepository<T>
├── heartbeatservice/         # ASP.NET Core Worker Service — probes VMs every 10s via TCP
├── landingpage/              # Public marketing + docs site (Blazor Server, static SSR)
└── docs/
    ├── NETWORKING_ARCHITECTURE.md   # How the FRP relay and SSH tunneling work
    ├── GCP_FRP_RELAY_SETUP.md       # FRP relay VM setup guide
    ├── GCP_INFRASTRUCTURE.md        # GCS, Artifact Registry, service accounts
    ├── SERVICE_EXPOSURE_SETUP.md    # Caddy + wildcard HTTPS subdomains
    └── MIGRATION_SETUP.md           # VM live migration setup and testing
```

### Data Architecture

UniCore uses a **generic Firestore repository pattern** for all data access:

```
Blazor Pages → Service Layer (business logic) → IFirestoreRepository<T> → Firestore
```

The repository provides type-safe CRUD, advanced querying, real-time change listeners, and paginated results — all from a single generic implementation. See [providerunicore/Repositories/README.md](providerunicore/Repositories/README.md) for the developer guide.

---

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker Desktop](https://www.docker.com/products/docker-desktop)
- A GCP project with Firestore and Firebase Authentication enabled
- `gcloud` CLI authenticated to your project
- `libnotify-bin` required for linux desktop notifications

### Local Development

```bash
# Clone the repository
git clone https://github.com/ghbrando/csc302_junior_design_project.git
cd csc302_junior_design_project

# Run the provider desktop app
cd providerunicore
dotnet watch
# Opens at http://localhost:5133

# Run the consumer web app (separate terminal)
cd consumerunicore
dotnet watch
# Opens at http://localhost:7073
```

### FRP Relay Setup

To enable SSH access for consumers, you need a GCP VM running the FRP server. Full setup instructions, including VM creation, firewall rules, and FRP client configuration, are in [docs/GCP_FRP_RELAY_SETUP.md](docs/GCP_FRP_RELAY_SETUP.md).

---

## How the Relay Works (TL;DR)

Provider machines are behind home routers, so you can't connect to them directly. UniCore solves this with FRP (Fast Reverse Proxy): the Docker container on the provider's machine makes an *outbound* TCP connection to a GCP VM running `frps`, and that tunnel is then available for consumers to SSH through.

```
Traditional (unsafe):
  Consumer ──SSH──────────────────────> Provider's home IP:22
  Problem: Provider machine exposed to internet

UniCore (safe):
  Consumer ──SSH──> GCP FRP Relay VM :2222-2300
                         │
                    FRP tunnel (outbound from container)
                         │
                    Docker Container :22 (sshd)
  Provider's IP: never revealed. Incoming ports: none required.
```

The relay port (one per VM, assigned from the 2222–2300 range) is stored in Firestore. Firestore also drives all real-time coordination — provider apps listen for VM assignments and control signals (pause/resume/stop) without any additional message broker.

---

## Security Model

- Provider machines make **outbound-only** FRP connections — no firewall rules, no port forwarding, no exposed IP
- All API and page access is authenticated with **Firebase JWT tokens** validated on every request
- Consumer workloads run in **isolated Docker containers** so consumers cannot escape to the provider's host
- Relay credentials (FRP server address and auth token) are stored in **GCP Secret Manager** and fetched at runtime
- GCP service account keys injected into containers are readable only by root (`chmod 600`); consumers cannot access them
- The heartbeat service TCP-probes each VM's relay port every 10 seconds and penalizes providers for missed responses

---

## Contributing

This project was built as part of **CSC 302: Junior Design** at California Baptist University, covering the full engineering lifecycle: requirements, architecture, ethics, teamwork, and delivery.

Pull requests, issue reports, and feedback are welcome.

**Developer Resources:**
- **[CLAUDE.md](CLAUDE.md)** — Comprehensive development guide (start here for coding)
- **[docs/NETWORKING_ARCHITECTURE.md](docs/NETWORKING_ARCHITECTURE.md)** — How the GCP relay works
- **[providerunicore/Repositories/README.md](providerunicore/Repositories/README.md)** — Data model guide

---

## Acknowledgments

Built by Jacob Pugh, Brandon Magana, Josh Baeza, Cameron Turner, and Elijah Simmonds — Spring 2026.
Powered by ASP.NET Core, Google Cloud Platform, Docker, and the open-source ecosystem.

---

<div align="center">
<sub>UniCore — compute for everyone.</sub>
</div>
