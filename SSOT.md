# UniCore - Democratized Cloud Computing Platform

> **"The Airbnb of Cloud Computing"** - Connecting compute providers with consumers through a secure, decentralized platform.

## 📋 Table of Contents
- [Project Overview](#project-overview)
- [Problem Statement](#problem-statement)
- [Solution](#solution)
- [Architecture Overview](#architecture-overview)
- [Tech Stack](#tech-stack)
- [System Components](#system-components)
- [Data Flow](#data-flow)
- [Database Schema](#database-schema)
- [Security Model](#security-model)
- [Billing & Metering](#billing--metering)
- [Development Roadmap](#development-roadmap)
- [Getting Started](#getting-started)
- [Team Roles](#team-roles)

---

## 🎯 Project Overview

UniCore is a decentralized cloud computing platform that connects people with idle computer resources (Providers) with users who need computing power (Consumers). Think of it as Airbnb, but instead of renting homes, people rent their computer's processing power.

**Key Innovation**: Consumers interact with their VMs through a web-based terminal - no SSH configuration required for MVP!

---

## 🔴 Problem Statement

### For Providers
- **Wasted Resources**: Millions of computers sit idle, wasting electricity and potential revenue
- **No Easy Monetization**: Average users can't easily monetize their spare computing power

### For Consumers  
- **High Barrier to Entry**: Current cloud services (AWS, GCP, Azure) are complex and intimidating
- **Cost**: Enterprise cloud providers charge premium prices
- **Complexity**: Setting up and managing cloud infrastructure requires specialized knowledge

---

## ✅ Solution

UniCore provides a **simple, secure, downloadable cloud computing environment**:

### For Providers
- Download a desktop application (Windows initially)
- Share unused computing power when convenient
- Earn money passively while computer is idle
- Full control: toggle service on/off, set resource limits
- Receive notifications when VMs spin up

### For Consumers
- Access cloud computing through an intuitive web interface
- Create VMs with a few clicks (no technical knowledge required)
- Connect via web terminal (no SSH setup needed!)
- Pay-as-you-go billing model
- Data persists across VM restarts - even on different provider machines

---

## 🏗️ Architecture Overview

UniCore follows a **three-tier architecture**:

```
┌─────────────────────────────────────────────────────────────────┐
│                     UNICORE ARCHITECTURE                        │
│                   (Web Terminal Only - MVP)                     │
└─────────────────────────────────────────────────────────────────┘

┌──────────────────┐         ┌─────────────────┐         ┌───────────────────┐
│   CONSUMER       │         │    GCP HUB      │         │    PROVIDER       │
│  (Blazor Web)    │         │  (Cloud Run)    │         │  (.NET Desktop)   │
├──────────────────┤         ├─────────────────┤         ├───────────────────┤
│                  │         │                 │         │                   │
│  ┌────────────┐  │         │  SignalR Hub    │         │  SignalR Client   │
│  │ xterm.js   │  │◄────────┤  ┌───────────┐  │◄────────┤  ┌──────────────┐ │
│  │ Terminal   │  │WebSocket│  │ Terminal  │  │SignalR  │  │ Docker Mgmt  │ │
│  │            │  │         │  │ Proxy     │  │         │  │              │ │
│  └────────────┘  │         │  └───────────┘  │         │  └──────┬───────┘ │
│                  │         │                 │         │         │         │
│  [Create VM]     │         │  Firestore DB   │         │  ┌──────▼───────┐ │
│  [VM Dashboard]  │         │  (VM States)    │         │  │   Docker     │ │
│  [Billing]       │         │                 │         │  │  Container   │ │
│                  │         │  Cloud Storage  │         │  │  (Ubuntu)    │ │
└──────────────────┘         │  (Volumes)      │         │  └──────────────┘ │
                             │                 │         │                   │
                             └─────────────────┘         └───────────────────┘
```

### Key Architectural Decisions

1. **Web Terminal Only (MVP)**: No SSH for initial release - simpler, more secure, better UX
2. **Docker-Based Pseudo-VMs**: Containers configured to feel like VMs (fast, good isolation)
3. **Proxy Architecture**: All traffic flows through GCP - providers never directly exposed
4. **Persistent Storage**: VM data stored in Cloud Storage, can restart on any provider
5. **Real-Time Communication**: SignalR WebSockets for instant updates

---

## 🛠️ Tech Stack

### Provider Desktop Application
**Platform**: Windows (MVP), future: cross-platform

| Component | Technology | Purpose |
|-----------|-----------|---------|
| UI Framework | .NET 10 WPF or MAUI | Native Windows desktop interface |
| Containerization | Docker Desktop + Docker.DotNet | Spin up isolated VM containers |
| Real-Time Comm | ASP.NET Core 10 SignalR Client | Maintain persistent connection to GCP |
| Metering | System.Diagnostics | Track CPU, memory, network usage |
| Storage | Google Cloud Storage SDK | Upload/download persistent volumes |

**Key Libraries**:
- `Docker.DotNet` - Official Docker SDK for .NET
- `Microsoft.AspNetCore.SignalR.Client` - Real-time bidirectional communication
- `Google.Cloud.Storage.V1` - Cloud Storage integration

---

### Consumer Web Application  
**Platform**: Web browser (cross-platform by default)

| Component | Technology | Purpose |
|-----------|-----------|---------|
| Framework | ASP.NET Core 10 Blazor | .NET-based web framework (WebAssembly or Server mode) |
| Terminal UI | xterm.js | Web-based terminal emulator |
| Real-Time Comm | ASP.NET Core 10 SignalR Client | Receive terminal output, send input |
| Authentication | Firebase Auth | User sign-in/sign-up |
| State Management | Blazor built-in | Component state handling |

**Key Libraries**:
- `xterm.js` - Terminal emulator in browser
- `xterm-addon-fit` - Auto-resize terminal
- `Microsoft.AspNetCore.SignalR.Client` - Real-time communication
- `Blazored.LocalStorage` - Browser storage for session data

**Note**: Blazor is built into ASP.NET Core 10 - no separate framework needed!

---

### GCP Hub (Backend)
**Platform**: Google Cloud Platform (Serverless)

| Component | Technology | Purpose |
|-----------|-----------|---------|
| API Hosting | Cloud Run | Serverless ASP.NET Core 10 Web API containers |
| Real-Time Hub | ASP.NET Core 10 SignalR Hub + Redis | Proxy WebSocket traffic between consumer/provider |
| Database | Firestore | NoSQL for VM states, users, jobs queue |
| File Storage | Cloud Storage | Persistent VM volumes (Docker volumes) |
| Event Queue | Cloud Pub/Sub | Async job notifications, billing events |
| Caching | Cloud Memorystore (Redis) | SignalR backplane for scaling |

**Key Libraries**:
- `Microsoft.AspNetCore.SignalR` - SignalR server
- `Google.Cloud.Firestore` - NoSQL database
- `Google.Cloud.Storage.V1` - Object storage
- `Google.Cloud.PubSub.V1` - Event streaming

---

## 🎉 Why .NET 10?

We're using the latest **.NET 10** (released November 2025) which brings several advantages for UniCore:

### Performance Improvements
- **Native AOT Support**: Faster startup times for Cloud Run deployments
- **Improved HTTP/3**: Better real-time communication for SignalR
- **Enhanced Blazor**: Better WebAssembly performance for consumer app

### New Features We'll Use
- **Enhanced SignalR**: Improved connection handling and reconnection logic
- **Better Docker Integration**: Improved container support in ASP.NET Core 10
- **Modern C# 13**: Pattern matching, collection expressions for cleaner code
- **Blazor Improvements**: Better JavaScript interop for xterm.js integration

### Platform Support
- **Cross-platform by default**: Easy path to macOS/Linux provider support
- **ARM64 Support**: Can target Apple Silicon and ARM-based cloud instances
- **Trimmed deployments**: Smaller Docker images for faster pulls

---

## 🎨 Blazor Architecture Decision

### Blazor WebAssembly vs Blazor Server

For the **Consumer Web App**, you have two Blazor hosting models to choose from:

#### Option A: Blazor WebAssembly (Recommended for MVP)
```
Browser downloads .NET runtime → Runs entirely client-side → Like React/Angular
```

**Pros**:
- ✅ Works offline after initial load
- ✅ No server resources needed per user
- ✅ Can be hosted on static hosting (Firebase Hosting, GitHub Pages)
- ✅ Scales infinitely (it's just static files)
- ✅ Better for global audience (no server roundtrips)

**Cons**:
- ❌ Initial load time (~3-5 seconds to download .NET runtime)
- ❌ Larger initial download (~2MB for runtime)

**Best for**: UniCore MVP - Consumer app is perfect for this!

---

#### Option B: Blazor Server
```
Browser connects via SignalR → UI runs on server → Server pushes updates
```

**Pros**:
- ✅ Instant initial load (no runtime download)
- ✅ Smaller initial payload
- ✅ Full .NET API access server-side

**Cons**:
- ❌ Requires constant connection to server
- ❌ Server resources scale with users
- ❌ Higher latency (UI interactions require server roundtrip)

**Best for**: Internal dashboards, admin panels

---

### Recommendation for UniCore: **Blazor WebAssembly**

Since your consumer app already needs SignalR for terminal communication, the WebAssembly model is perfect:
- Consumer loads app once
- SignalR connection only needed when using terminal
- Can scale to thousands of users without server costs
- Static hosting on Firebase Hosting (free tier!)

---

## 🧩 System Components

### 1. Consumer Ecosystem (Demand Side)

**User Journey**:
1. Visit UniCore website → Sign up/Login
2. Click "Create VM" → Choose specs (CPU, RAM, OS)
3. GCP queues request → Matches with available provider
4. VM status updates to "Running"
5. Click "Open Terminal" → Instant web-based shell access
6. Use VM (install packages, run code, store files)
7. Close terminal → VM continues running (pay-as-you-go)
8. Click "Stop VM" → VM shuts down, data persists in cloud

**Key Features**:
- **VM Dashboard**: View all VMs (running, stopped, queued)
- **Web Terminal**: xterm.js-based shell with full Ubuntu access
- **Billing Dashboard**: Real-time usage tracking, cost estimates
- **Budget Alerts**: Notifications when approaching spending limits

---

### 2. GCP Hub (Orchestrator)

**Responsibilities**:
- **Matchmaking**: Pair consumer VM requests with available providers
- **Load Balancing**: Distribute VMs across provider pool
- **Connection Proxy**: Route all terminal traffic through secure WebSocket proxy
- **State Management**: Track VM lifecycle (queued → starting → running → stopped)
- **Billing Engine**: Aggregate usage metrics, calculate costs
- **Volume Management**: Orchestrate upload/download of persistent storage

**Key Endpoints**:
- `POST /api/vm/create` - Consumer creates VM request
- `GET /api/vm/{id}/status` - Check VM state
- `WS /terminal?vmId={id}` - WebSocket for terminal connection
- `POST /api/provider/register` - Provider registers availability
- `GET /api/provider/jobs` - Provider polls for assigned VMs

---

### 3. Provider Ecosystem (Supply Side)

**User Journey**:
1. Download UniCore Provider App (Windows installer)
2. Sign up → Verify identity → Accept TOS
3. Set preferences:
   - Max CPU allocation (e.g., 50% of total)
   - Max RAM allocation (e.g., 4GB)
   - Toggle: Accept jobs when idle vs. always
4. App sits in system tray, monitors for job assignments
5. When VM assigned:
   - Download persistent volume (if exists)
   - Spin up Docker container
   - Report to GCP: "VM is live"
6. Execute terminal commands from consumer via GCP proxy
7. Track metrics (CPU time, memory, network I/O)
8. When consumer stops VM:
   - Upload volume to Cloud Storage
   - Destroy container
   - Report usage metrics to GCP

**Key Features**:
- **Dashboard**: View active VMs, resource usage, earnings
- **Notifications**: Alert when new VM spins up
- **Resource Control**: Set limits on CPU/RAM allocation
- **Toggle Service**: Enable/disable accepting new jobs
- **Earnings Tracker**: Real-time revenue display

---

## 🔄 Data Flow

### VM Lifecycle: Creation to Termination

```
┌─────────────────────────────────────────────────────────────────────┐
│  PHASE 1: VM REQUEST                                                │
└─────────────────────────────────────────────────────────────────────┘

Consumer Web App                GCP Hub                Provider Desktop
      │                            │                          │
      ├──POST /api/vm/create──────►│                          │
      │  {os: ubuntu, cpu: 2,      │                          │
      │   ram: 4GB}                │                          │
      │                            ├─Firestore: Create VM─┐   │
      │                            │  {state: "queued",   │   │
      │                            │   specs: ...}        │   │
      │                            │◄─────────────────────┘   │
      │◄──200 OK: {vmId: "abc"}────┤                          │
      │                            │                          │

┌─────────────────────────────────────────────────────────────────────┐
│  PHASE 2: MATCHMAKING                                               │
└─────────────────────────────────────────────────────────────────────┘

      │                            │                          │
      │                            ├─Matchmaking Algorithm────┤
      │                            │  Find provider with:     │
      │                            │  - Available resources   │
      │                            │  - Accepting jobs        │
      │                            │  - Good uptime record    │
      │                            │                          │
      │                            ├──SignalR: AssignVM──────►│
      │                            │  {vmId, specs, image}    │
      │                            │                          │

┌─────────────────────────────────────────────────────────────────────┐
│  PHASE 3: VM STARTUP                                                │
└─────────────────────────────────────────────────────────────────────┘

      │                            │                          ├─Download Volume─┐
      │                            │                          │  (if exists)    │
      │                            │                          │◄────────────────┘
      │                            │                          │
      │                            │                          ├─Docker Create───┐
      │                            │                          │  Container      │
      │                            │                          │◄────────────────┘
      │                            │                          │
      │                            │◄─SignalR: VMStarted─────┤
      │                            │  {vmId, containerId}     │
      │                            │                          │
      │                            ├─Firestore: Update VM─┐   │
      │                            │  {state: "running"}  │   │
      │                            │◄─────────────────────┘   │
      │                            │                          │
      │◄──SignalR: VMReady─────────┤                          │
      │  "Your VM is ready!"       │                          │

┌─────────────────────────────────────────────────────────────────────┐
│  PHASE 4: TERMINAL SESSION                                          │
└─────────────────────────────────────────────────────────────────────┘

      ├─Click "Open Terminal"──────►│                          │
      │                            │                          │
      ├─WS Connect /terminal───────►│                          │
      │                            │                          │
      │                            ├──Consumer Connected──────►│
      │                            │  {vmId, connectionId}    │
      │                            │                          │
      │                            │                          ├─Ready to exec───┐
      │                            │                          │                │
      │                            │                          │◄───────────────┘
      │                            │                          │
      ├─WS: "ls -la"───────────────►│                          │
      │                            ├──ExecuteCommand──────────►│
      │                            │  {vmId, cmd: "ls -la"}   │
      │                            │                          │
      │                            │                          ├─docker exec─────┐
      │                            │                          │  containerId    │
      │                            │                          │  /bin/bash -c   │
      │                            │                          │  "ls -la"       │
      │                            │                          │◄────────────────┘
      │                            │                          │
      │                            │◄─SendOutput──────────────┤
      │                            │  {vmId, output: "..."}   │
      │◄─WS: output data───────────┤                          │
      │  (displays in xterm.js)    │                          │

┌─────────────────────────────────────────────────────────────────────┐
│  PHASE 5: IDLE TIMEOUT (10 minutes of no activity)                 │
└─────────────────────────────────────────────────────────────────────┘

      │                            ├─Idle Timer Triggers──────►│
      │                            │  "No commands for 10min"  │
      │                            │                          │
      │                            │                          ├─Stop Container──┐
      │                            │                          │◄────────────────┘
      │                            │                          │
      │                            │                          ├─Upload Volume───┐
      │                            │                          │  to Cloud       │
      │                            │                          │  Storage        │
      │                            │                          │◄────────────────┘
      │                            │                          │
      │                            │◄─VMStopped───────────────┤
      │                            │  {vmId, metrics}         │
      │                            │                          │
      │                            ├─Firestore: Update VM─┐   │
      │                            │  {state: "stopped"}  │   │
      │                            │◄─────────────────────┘   │
      │◄──SignalR: VMStopped───────┤                          │
      │  "VM auto-stopped"         │                          │

┌─────────────────────────────────────────────────────────────────────┐
│  PHASE 6: RESTART (Later, possibly on different provider)          │
└─────────────────────────────────────────────────────────────────────┘

      ├──POST /api/vm/{id}/start──►│                          │
      │                            │                          │
      │                            ├─Match with Provider──────►│ (could be
      │                            │  (could be different!)   │  Provider B!)
      │                            │                          │
      │                            │                          ├─Download Volume─┐
      │                            │                          │  from Cloud     │
      │                            │                          │  Storage        │
      │                            │                          │◄────────────────┘
      │                            │                          │
      │                            │                          ├─Docker Create───┐
      │                            │                          │  Mount volume   │
      │                            │                          │◄────────────────┘
      │                            │                          │
      │   (Consumer sees all their files still there!)        │
```

---

## 🗄️ Database Schema

### Firestore Collections

#### 1. `users` Collection
```json
{
  "userId": "user_abc123",
  "email": "user@example.com",
  "displayName": "John Doe",
  "role": "consumer", // or "provider" or "both"
  "createdAt": "2026-02-01T12:00:00Z",
  "billingInfo": {
    "customerId": "stripe_cus_xyz",
    "paymentMethods": ["pm_card_123"]
  }
}
```

#### 2. `providers` Collection
```json
{
  "providerId": "prov_def456",
  "userId": "user_abc123",
  "machineId": "machine_xyz789",
  "status": "online", // online, offline, maintenance
  "acceptingJobs": true,
  "specs": {
    "cpu": {
      "cores": 8,
      "model": "Intel i7-12700K",
      "allocatedCores": 4
    },
    "memory": {
      "totalGB": 32,
      "allocatedGB": 16
    },
    "storage": {
      "totalGB": 1000,
      "availableGB": 500
    }
  },
  "location": {
    "country": "USA",
    "region": "us-west"
  },
  "uptime": {
    "last30Days": 0.99,
    "totalHours": 720
  },
  "earnings": {
    "totalUSD": 156.78,
    "pendingUSD": 12.34
  },
  "lastSeen": "2026-02-11T10:30:00Z"
}
```

#### 3. `vms` Collection
```json
{
  "vmId": "vm_ghi789",
  "consumerId": "user_abc123",
  "providerId": "prov_def456", // null if queued
  "state": "running", // queued, starting, running, stopped, terminated
  "specs": {
    "os": "ubuntu:22.04",
    "vcpus": 2,
    "memoryGB": 4,
    "storageGB": 20
  },
  "createdAt": "2026-02-11T09:00:00Z",
  "startedAt": "2026-02-11T09:02:15Z",
  "stoppedAt": null,
  "volumeId": "vol_jkl012",
  "connectionInfo": {
    "terminalUrl": "wss://hub.unicore.io/terminal?vmId=vm_ghi789"
  },
  "billing": {
    "ratePerHour": 0.05,
    "estimatedCost": 0.42,
    "actualCost": null // calculated on stop
  }
}
```

#### 4. `volumes` Collection (persistent storage metadata)
```json
{
  "volumeId": "vol_jkl012",
  "vmId": "vm_ghi789",
  "sizeGB": 8.5,
  "cloudStoragePath": "gs://unicore-volumes/vol_jkl012/volume.tar.gz",
  "lastSnapshot": "2026-02-11T10:00:00Z",
  "status": "synced" // syncing, synced, dirty
}
```

#### 5. `jobs_queue` Collection
```json
{
  "jobId": "job_mno345",
  "type": "vm_create",
  "vmId": "vm_ghi789",
  "status": "pending", // pending, assigned, completed, failed
  "priority": 1,
  "createdAt": "2026-02-11T09:00:00Z",
  "assignedTo": null, // providerId when assigned
  "assignedAt": null
}
```

#### 6. `billing_records` Collection
```json
{
  "recordId": "bill_pqr678",
  "vmId": "vm_ghi789",
  "consumerId": "user_abc123",
  "providerId": "prov_def456",
  "startTime": "2026-02-11T09:02:15Z",
  "endTime": "2026-02-11T12:30:45Z",
  "durationMinutes": 208,
  "metrics": {
    "avgCpuPercent": 45.2,
    "avgMemoryMB": 2048,
    "networkInGB": 1.2,
    "networkOutGB": 0.8
  },
  "costs": {
    "compute": 0.35,
    "network": 0.05,
    "storage": 0.02,
    "total": 0.42
  },
  "providerShare": 0.30, // 70% to provider
  "platformFee": 0.12, // 30% to UniCore
  "status": "charged" // pending, charged, disputed
}
```

---

## 🔐 Security Model

### Multi-Layer Security Architecture

#### Layer 1: Provider Machine Isolation
**Docker Container Security**:
- Containers run with **dropped capabilities** (no CAP_SYS_ADMIN, etc.)
- **AppArmor/SELinux** profiles enforced
- **Read-only filesystem** except mounted volumes
- **Network isolation**: Containers cannot access host network directly
- **Resource limits**: CPU/memory cgroups prevent resource exhaustion

**Volume Isolation**:
- Each VM gets dedicated Docker volume
- Volumes stored in isolated directory: `/var/unicore/volumes/{vmId}/`
- Provider's filesystem inaccessible from container

#### Layer 2: Network Security
**No Direct Exposure**:
- Provider machines NEVER directly exposed to internet
- All traffic proxied through GCP SignalR hub
- Providers connect outbound to GCP (WebSocket)
- Consumers connect to GCP, never to providers

**TLS Everywhere**:
- WebSocket connections: `wss://` (TLS encrypted)
- API calls: HTTPS only
- SignalR enforces TLS 1.3

#### Layer 3: Authentication & Authorization
**Provider App**:
- Device registration with unique machine ID
- JWT tokens for API authentication
- SignalR connection requires valid token
- Refresh tokens for long-lived connections

**Consumer App**:
- Firebase Authentication (email/password, Google OAuth)
- JWT tokens passed to GCP APIs
- VM access control: consumers can only access their own VMs

#### Layer 4: Data Security
**At Rest**:
- Cloud Storage buckets: server-side encryption (Google-managed keys)
- Firestore: encrypted by default
- Local volumes on provider: optional encryption (post-MVP)

**In Transit**:
- All WebSocket traffic: TLS 1.3
- Volume uploads/downloads: HTTPS

#### Layer 5: Monitoring & Abuse Prevention
**Provider Monitoring**:
- Anomaly detection: unusual CPU/network patterns
- Reputation system: track provider uptime, successful jobs
- Automatic suspension for suspicious activity

**Consumer Monitoring**:
- Rate limiting: API requests, VM creation
- Budget alerts: prevent runaway costs
- Terms of Service enforcement

---

### Security Disclaimers (TOS)

**For Providers**:
> UniCore uses Docker containerization to isolate virtual machines from your system. While Docker provides strong isolation, it is not equivalent to full hardware virtualization. By participating as a provider, you accept that:
> - VMs run in containers that share your machine's kernel
> - There is a theoretical (but low) risk of container escape
> - You should not store sensitive personal data on machines running UniCore
> - UniCore monitors container activity for security threats

**For Consumers**:
> Your virtual machines run on community-provided hardware. While we enforce security policies:
> - VMs are containers, not true virtual machines
> - Data persistence is best-effort (always backup critical data)
> - Provider machines may go offline unexpectedly
> - Network performance depends on provider's connection

---

## 💰 Billing & Metering

### Metering Strategy

#### Provider-Side Metrics Collection
Provider desktop app tracks **per-second** metrics using Docker stats API:

**Metrics Tracked**:
- **CPU Time**: Nanoseconds of CPU time consumed
- **Memory Usage**: Average MB used
- **Network I/O**: Bytes in/out
- **Storage**: GB of persistent volume
- **Uptime**: Seconds VM was running

**Reporting Frequency**: Every 60 seconds to GCP

#### GCP Aggregation
```
Hourly Cost = (CPU Hours × CPU Rate) + 
              (Memory GB-Hours × Memory Rate) + 
              (Network GB × Network Rate) +
              (Storage GB × Storage Rate)
```

**Example Pricing** (MVP - subject to change):
- CPU: $0.02 per vCPU-hour
- Memory: $0.005 per GB-hour
- Network: $0.10 per GB transferred
- Storage: $0.01 per GB-month

#### Revenue Split
- **Provider**: 70% of revenue
- **UniCore Platform**: 30% (covers GCP costs, development, profit)

### Payment Flow

```
Consumer pays → Stripe → UniCore Platform Account
                              ↓ (70%)
                         Provider Payout
```

**Provider Payouts**:
- Minimum threshold: $10
- Frequency: Weekly (automated)
- Methods: Bank transfer, PayPal

---

## 🗓️ Development Roadmap

### Sprint 2-3: Provider Ecosystem (Weeks 1-4)
**Goal**: Get provider desktop app running VMs

#### Sprint 2 Tasks (Week 1-2):
- [ ] **Provider Desktop App Setup**
  - Create .NET 10 WPF project
  - Design main window UI (dashboard, settings)
  - Implement system tray integration
  
- [ ] **Provider Authentication**
  - Firebase Auth integration
  - Login/Signup UI
  - Machine ID generation and registration
  
- [ ] **Docker Integration**
  - Install Docker.DotNet package
  - Implement container creation/start/stop
  - Test with simple Ubuntu container
  
- [ ] **GCP Connection**
  - SignalR client setup
  - Persistent connection to GCP hub
  - Heartbeat/reconnection logic

**Deliverables**: Provider can log in, connect to GCP, and spin up a basic Docker container

---

#### Sprint 3 Tasks (Week 3-4):
- [ ] **VM Lifecycle Management**
  - Receive VM assignment from GCP
  - Download/upload volumes to Cloud Storage
  - Execute commands via `docker exec`
  - Stream output back to GCP
  
- [ ] **Metering & Monitoring**
  - Collect Docker stats (CPU, memory, network)
  - Send metrics to GCP every 60 seconds
  - Display resource usage in UI
  
- [ ] **Provider Dashboard**
  - Show active VMs
  - Display real-time resource usage
  - Show earnings (mock data for now)
  
- [ ] **Settings & Controls**
  - Toggle accepting jobs
  - Set resource allocation limits
  - TOS acceptance flow

**Deliverables**: Provider can run consumer VMs, execute commands, and track usage

---

### Sprint 4-5: Consumer Ecosystem (Weeks 5-8)

#### Sprint 4 Tasks (Week 5-6):
- [ ] **Consumer Web App Setup**
  - Create ASP.NET Core 10 Blazor WebAssembly project
  - Configure project structure (Pages, Components, Services)
  - Design landing page
  - Implement authentication (Firebase)
  
- [ ] **VM Creation UI**
  - "Create VM" Blazor component (OS, CPU, RAM selection)
  - Submit request to GCP API via HttpClient
  - Display VM in dashboard (queued state)
  
- [ ] **VM Dashboard**
  - Blazor components for VM list (running, stopped, queued)
  - Show VM details (specs, status, cost)
  - Start/Stop VM buttons with loading states
  
- [ ] **Web Terminal Integration**
  - Install xterm.js via npm/CDN
  - Create Blazor component wrapping xterm.js
  - JavaScript interop for terminal I/O
  - Connect to GCP SignalR hub
  - Send/receive terminal I/O

**Deliverables**: Consumer can create VM, see it in dashboard, and open web terminal

---

#### Sprint 5 Tasks (Week 7-8):
- [ ] **Billing Dashboard**
  - Show current usage and costs
  - Display billing history
  - Budget alerts setup
  
- [ ] **VM State Management**
  - Handle idle timeout (10 min warning)
  - Auto-stop after timeout
  - Persist data across restarts
  
- [ ] **Async VM Operations**
  - Consumer can close browser, VM keeps running
  - Background notifications for state changes
  
- [ ] **Polish & Error Handling**
  - Loading states
  - Error messages
  - Retry logic for failures

**Deliverables**: Full consumer experience - create, use, monitor, and pay for VMs

---

### Sprint 6: Landing Page & Final Polish (Week 9-10)
- [ ] **Public Landing Page**
  - Marketing site
  - Download links for provider app
  - Sign up CTA for consumers
  
- [ ] **Documentation**
  - User guides (consumer & provider)
  - FAQ
  - Troubleshooting
  
- [ ] **Testing & Bug Fixes**
  - End-to-end testing
  - Load testing
  - Security audit
  
- [ ] **Deployment**
  - Deploy GCP hub to Cloud Run
  - Deploy consumer app (Firebase Hosting or Cloud Run)
  - Provider app installer (Windows)

**Deliverables**: Fully functional MVP ready for beta users

---

## 🚀 Getting Started

### Prerequisites
- **.NET 10 SDK**: Download from [dotnet.microsoft.com](https://dotnet.microsoft.com)
- **Docker Desktop**: Download from [docker.com](https://docker.com)
- **Visual Studio 2022** or **VS Code**
- **Git**: Version control
- **Google Cloud Account**: For GCP services (free tier available)

### Repository Structure
```
csc302_junior_design_project/
├── consumerunicore/              # ASP.NET Core 10 Blazor WebAssembly consumer app
│   ├── Client/                   # Blazor WebAssembly client (runs in browser)
│   ├── Server/                   # Optional API server (for development)
│   └── Shared/                   # Shared models/DTOs
├── providerunicore/              # .NET 10 WPF provider desktop app
├── unicore-hub/                  # ASP.NET Core 10 Web API for GCP (to be created)
│   ├── Hubs/                     # SignalR hub classes
│   ├── Controllers/              # REST API controllers
│   ├── Services/                 # Business logic (matchmaking, billing, etc.)
│   └── Models/                   # Data models
├── docs/                         # Documentation
│   ├── architecture.md
│   ├── api-reference.md
│   └── deployment-guide.md
└── README.md                     # This file
```

**Note**: Your current `consumerunicore` and `providerunicore` folders already exist. The structure above shows the recommended organization as you build out the projects.

### Initial Setup

#### 1. Clone Repository
```bash
git clone https://github.com/ghbrando/csc302_junior_design_project.git
cd csc302_junior_design_project
```

#### 2. GCP Setup
- Create a Google Cloud project
- Enable APIs: Firestore, Cloud Storage, Cloud Run, Pub/Sub
- Create service account for local development
- Download credentials JSON

#### 3. Firebase Setup
- Create Firebase project (link to GCP project)
- Enable Authentication (Email/Password, Google OAuth)
- Get Firebase config (apiKey, authDomain, etc.)

#### 4. Provider App Setup
```bash
cd providerunicore
dotnet restore
# Add appsettings.json with GCP/Firebase credentials
dotnet run
```

#### 5. Consumer App Setup (Blazor WebAssembly)
```bash
cd consumerunicore
dotnet restore
# Add wwwroot/appsettings.json with Firebase config
# Install JavaScript dependencies for xterm.js
dotnet run
# Access at https://localhost:7073
```

**Note**: Blazor WebAssembly projects include a server project for development hosting. In production, you'll deploy the compiled WebAssembly files to static hosting.

---

## 👥 Team Roles

| Name | Role | Responsibilities |
|------|------|-----------------|
| **Brandon Magana** | Tech Lead | Architecture decisions, code reviews, Sprint planning |
| **Jacob Pugh** | Product Lead | Requirements, UX/UI, stakeholder communication |
| **Cameron Turner** | Backend Developer | GCP Hub, SignalR, Docker integration |
| **Elijah Simmonds** | Frontend Developer | Consumer web app, Blazor components |
| **Joshua Baeza** | Infrastructure | Provider desktop app, Docker, monitoring |

### Communication
- **Daily Standups**: 10am via Discord
- **Sprint Planning**: Mondays 2pm
- **Code Reviews**: GitHub Pull Requests (minimum 1 approval required)
- **Documentation**: Update this README as architecture evolves

---

## 📚 Additional Resources

### Learning Materials
- **SignalR Tutorial**: [docs.microsoft.com/signalr](https://docs.microsoft.com/aspnet/core/signalr)
- **Docker.DotNet**: [github.com/dotnet/Docker.DotNet](https://github.com/dotnet/Docker.DotNet)
- **Blazor Docs**: [docs.microsoft.com/blazor](https://docs.microsoft.com/aspnet/core/blazor)
- **xterm.js**: [xtermjs.org](https://xtermjs.org)
- **Firestore**: [firebase.google.com/docs/firestore](https://firebase.google.com/docs/firestore)

### Design Assets
- **Figma Prototype**: [https://ripen-utter-34698889.figma.site/](https://ripen-utter-34698889.figma.site/)
- **Architecture Diagrams**: See `/docs/architecture.md`

---

## 🎯 Success Metrics (Post-MVP)

**Technical**:
- VM startup time < 30 seconds
- Terminal latency < 200ms
- Provider uptime > 95%
- Data persistence success rate > 99.9%

**Business**:
- 100 active providers in first 3 months
- 500 active consumers in first 3 months
- Average provider earnings: $50/month
- Platform revenue: $5,000/month

---

## ⚖️ License

**Proprietary** - All rights reserved by UniCore Team (CSC302 Group #4)

This project is for academic purposes (EGR302 Junior Design Project). Not licensed for commercial use without team approval.

---

## 📞 Contact

**Team Email**: unicore.team@example.com  
**GitHub**: [github.com/ghbrando/csc302_junior_design_project](https://github.com/ghbrando/csc302_junior_design_project)  
**Figma**: [ripen-utter-34698889.figma.site](https://ripen-utter-34698889.figma.site/)

---

**Last Updated**: February 11, 2026  
**Version**: 1.0.0 (MVP Architecture)  
**Next Review**: Start of Sprint 2 (February 12, 2026)
