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
┌─────────────────┐          ┌─────────────────────┐          ┌─────────────────┐
│    CONSUMER     │          │     GCP RELAY HUB   │          │    PROVIDER     │
│   (Browser)     │          │     (Cloud Run)     │          │   (Desktop App) │
└────────┬────────┘          └──────────┬──────────┘          └────────┬────────┘
         │                              │                               │
         │  WebSocket (inbound)         │       WebSocket (outbound)    │
         └─────────────────────────────>│<─────────────────────────────┘
                                        │
                              SignalR Hub routes
                              all I/O between the
                              two persistent connections
                                        │
                               Redis Backplane
                              (multi-instance state)
```

### How It Works

1. **Provider connects first.** When the provider desktop app launches, it establishes a persistent outbound WebSocket to the GCP hub and registers its available VMs. This connection stays open with no incoming ports needed and no router configuration required.

2. **Consumer requests a terminal.** From the browser, the consumer selects a VM and opens a terminal. The hub validates their JWT, looks up which provider is hosting that VM, and bridges the two connections.

3. **All I/O flows through the hub.** Keystrokes from xterm.js in the browser → GCP → provider → Docker container → output back the same path. The provider's IP address is never revealed to the consumer.

4. **Provider disconnects gracefully.** If a provider goes offline, SignalR detects the missed heartbeat within 30 seconds, marks their VMs offline in Firestore, and notifies affected consumers.

For a deep-dive, see [docs/NETWORKING_ARCHITECTURE.md](docs/NETWORKING_ARCHITECTURE.md).

---

## Features

### For Providers
- **Live dashboard** with real-time CPU, RAM, and GPU metrics and historical graphs
- **One-click VM launch:** Docker containers provisioned automatically with SSH and FRP tunnel registration
- **Resource controls:** set per-VM CPU core limits and RAM caps
- **Revenue tracking:** view session earnings and payout history based on UniCore's platform rates
- **Emergency stop:** shut down all running VMs instantly from the dashboard
- **Automatic reconnection:** persistent SignalR connection with heartbeat-based health checks

### For Consumers
- **Browser-based terminal** powered by [xterm.js](https://xtermjs.org/) — full interactive shell, no local client needed
- **VM marketplace:** browse available VMs filtered by provider, specs, and price
- **Persistent sessions:** close the tab and reconnect; the VM keeps running

### Platform
- **Zero provider exposure:** outbound-only connections, no open ports, no NAT traversal
- **Docker isolation:** each VM runs in its own container; consumers can't reach provider infrastructure
- **Horizontal scaling:** Redis backplane coordinates SignalR routing across multiple Cloud Run instances
- **Secure secrets:** relay credentials stored in GCP Secret Manager, never in config files
- **FRP SSH tunneling:** consumer SSH access routed through GCP relay VM with token-based auth

---

## Tech Stack

| Layer | Technology |
|---|---|
| Provider App | C# .NET 10, ASP.NET Core Blazor (Server) |
| Consumer App | C# .NET 10, ASP.NET Core Blazor, xterm.js |
| Real-Time Relay | ASP.NET Core SignalR, Redis Backplane |
| Cloud Hosting | GCP Cloud Run (serverless containers) |
| Database | Google Cloud Firestore (NoSQL, real-time listeners) |
| Authentication | Firebase Auth (JWT tokens) |
| Containerization | Docker, Docker.DotNet SDK |
| SSH Tunneling | FRP (Fast Reverse Proxy) v0.61.0 |
| Secret Management | GCP Secret Manager |
| Infrastructure | GCP Firewall Rules, GCP Compute Engine (FRP relay VM) |

---

## Project Structure

```
csc302_junior_design_project/
├── providerunicore/          # Provider desktop application (Blazor Server)
│   ├── Components/           # Blazor UI pages and components
│   ├── Models/               # Firestore data models (Provider, VirtualMachine, Payout)
│   ├── Repositories/         # Generic Firestore repository pattern
│   ├── Services/             # Business logic (Docker, Auth, VMs, Payouts, Monitoring)
│   └── Program.cs            # DI registration and app startup
├── consumerunicore/          # Consumer web application (Blazor)
├── docs/
│   ├── NETWORKING_ARCHITECTURE.md   # Deep-dive: GCP proxy architecture
│   ├── GCP_FRP_RELAY_SETUP.md       # FRP relay VM setup guide
│   └── SSH_PoC.md                   # SSH access proof of concept
└── README.md
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
git clone https://github.com/your-org/csc302_junior_design_project.git
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

## How the Proxy Works (TL;DR)

Provider machines are behind home routers, so you can't connect to them directly. UniCore solves this the same way corporate VPNs do: the protected machine makes an *outbound* connection to a trusted server, and that server brokers all traffic.

```
Traditional (unsafe):
  Consumer ──SSH──────────────────────────> Provider's home IP
  Problem: Provider machine exposed to internet

UniCore (safe):
  Consumer ──WebSocket──> GCP Hub ──SignalR──> Provider desktop app
                                               Provider app ──exec──> Docker container
  Provider's IP: never revealed. Incoming ports: none required.
```

The hub uses SignalR's connection routing to map every consumer terminal session to the right provider connection by VM ID. Redis ensures this routing table is shared across all Cloud Run instances, so the system scales horizontally without any sticky sessions.

---

## Security Model

- Provider machines make **outbound-only** connections with no firewall rules, no port forwarding, and no exposed IP
- All connections are authenticated with **Firebase JWT tokens** validated at the hub
- Consumer workloads run in **isolated Docker containers** so consumers cannot escape to the provider's host
- Relay credentials (FRP server address and auth token) are stored in **GCP Secret Manager** and fetched at runtime
- SignalR heartbeats (30-second interval) detect dead connections and clean up routing state automatically

---

## Contributing

This project was built as part of **EGR 302: Team Design Project** at California Baptist University, covering the full engineering lifecycle: requirements, architecture, ethics, teamwork, and delivery.

Pull requests, issue reports, and feedback are welcome. For architecture questions, start with [docs/NETWORKING_ARCHITECTURE.md](docs/NETWORKING_ARCHITECTURE.md). For data model questions, see [providerunicore/Repositories/README.md](providerunicore/Repositories/README.md).

---

## Acknowledgments

Built by the UniCore team, Spring 2026.
Powered by ASP.NET Core, Google Cloud Platform, Docker, and the open-source ecosystem.

---

<div align="center">
<sub>UniCore — compute for everyone.</sub>
</div>
