# UniCore Networking Architecture: The GCP Proxy Explained

## 🎯 The Core Problem

**Challenge**: Consumer needs to interact with a VM running on Provider's machine, but:
- Provider machines are typically behind home routers/NAT
- Can't expose provider machines directly to internet (security risk)
- Provider may have firewall blocking incoming connections
- Dynamic IPs make direct addressing impossible

**Solution**: All traffic flows through GCP as a trusted proxy/relay

---

## 🔄 How The Proxy Works

### Architecture Overview

```
┌─────────────────┐                  ┌─────────────────┐                  ┌─────────────────┐
│   CONSUMER      │                  │    GCP PROXY    │                  │    PROVIDER     │
│   (Browser)     │                  │   (Cloud Run)   │                  │   (Desktop)     │
└─────────────────┘                  └─────────────────┘                  └─────────────────┘
        │                                     │                                     │
        │                                     │                                     │
   INBOUND CONNECTION                    CENTRAL HUB                      OUTBOUND CONNECTION
   (Consumer initiates)              (Brokers all traffic)              (Provider initiates)
        │                                     │                                     │
        ▼                                     ▼                                     ▼
  WebSocket (wss://)                    SignalR Hub                         SignalR Client
  To: hub.unicore.io                  Maintains routing                  To: hub.unicore.io
                                          table                          Persistent connection
```

---

## 🔌 Connection Establishment (Step-by-Step)

### Phase 1: Provider Connects to GCP (Always First!)

The provider desktop app establishes a **persistent outbound connection** when it starts:

```
1. Provider Desktop App Starts
   └─> Connects to: wss://hub.unicore.io/provider
       ├─> Authentication: JWT token with provider ID
       ├─> SignalR protocol negotiation
       └─> Connection established

2. GCP Hub Receives Connection
   └─> Registers provider in connection table:
       {
         "providerId": "prov_abc123",
         "connectionId": "conn_xyz789",
         "status": "connected",
         "lastSeen": "2026-02-12T10:00:00Z"
       }

3. Provider Connection Stays Open
   └─> Heartbeat every 30 seconds to keep alive
   └─> Provider can now receive messages from GCP
```

**Key Point**: This is an **outbound** connection from provider → GCP. Provider's firewall allows this (just like browsing the web). No ports need to be opened on provider's router.

---

### Phase 2: Consumer Requests Terminal Connection

When consumer clicks "Open Terminal" in web app:

```
1. Consumer Web App Makes Request
   ├─> POST /api/vm/{vmId}/connect
   ├─> Authentication: Consumer's Firebase JWT
   └─> GCP validates consumer owns this VM

2. GCP Hub Looks Up VM Assignment
   └─> Query Firestore:
       {
         "vmId": "vm_123",
         "providerId": "prov_abc123",  ← Which provider has this VM?
         "state": "running",
         "containerId": "docker_container_456"
       }

3. GCP Hub Establishes Consumer WebSocket
   ├─> Consumer connects: wss://hub.unicore.io/terminal?vmId=vm_123
   ├─> GCP validates: Is this consumer allowed to access vm_123?
   └─> Connection established and mapped:
       {
         "vmId": "vm_123",
         "consumerConnectionId": "conn_consumer_111",
         "providerConnectionId": "conn_xyz789"  ← Already connected!
       }

4. GCP Now Has BOTH Connections
   └─> Consumer ←→ GCP ←→ Provider
       ├─> Consumer connection: INBOUND to GCP
       ├─> Provider connection: OUTBOUND from provider (already open!)
       └─> GCP acts as relay between them
```

---

## 📡 Data Flow: Terminal I/O Through Proxy

### Scenario: Consumer Types "ls -la" in Terminal

```
┌─────────────────────────────────────────────────────────────────────────┐
│  STEP 1: Consumer Input                                                 │
└─────────────────────────────────────────────────────────────────────────┘

Consumer's Browser (xterm.js)
    ↓
    User types: "ls -la" + Enter
    ↓
JavaScript captures keystrokes
    ↓
    Sends via WebSocket:
    {
      "type": "input",
      "vmId": "vm_123",
      "data": "ls -la\n"
    }
    ↓
    ────────────────→  GCP Hub (SignalR)


┌─────────────────────────────────────────────────────────────────────────┐
│  STEP 2: GCP Proxy Routes to Provider                                   │
└─────────────────────────────────────────────────────────────────────────┘

GCP Hub receives WebSocket message
    ↓
Looks up routing table:
    vmId: vm_123 → providerConnectionId: conn_xyz789
    ↓
Forwards via SignalR to Provider:
    {
      "method": "ExecuteCommand",
      "vmId": "vm_123",
      "command": "ls -la\n"
    }
    ↓
    ────────────────→  Provider Desktop App


┌─────────────────────────────────────────────────────────────────────────┐
│  STEP 3: Provider Executes in Docker                                    │
└─────────────────────────────────────────────────────────────────────────┘

Provider Desktop App receives SignalR message
    ↓
Executes in Docker container:
    docker exec <container_id> /bin/bash -c "ls -la"
    ↓
Captures output:
    "total 48
     drwxr-xr-x 5 consumer consumer 4096 Feb 12 10:00 .
     drwxr-xr-x 3 root     root     4096 Feb 12 09:00 ..
     -rw-r--r-- 1 consumer consumer  220 Feb 12 09:00 .bash_logout
     ..."
    ↓
Sends output back via SignalR:
    {
      "method": "SendOutput",
      "vmId": "vm_123",
      "output": "total 48\ndrwxr-xr-x..."
    }
    ↓
    ────────────────→  GCP Hub


┌─────────────────────────────────────────────────────────────────────────┐
│  STEP 4: GCP Proxy Routes Back to Consumer                              │
└─────────────────────────────────────────────────────────────────────────┘

GCP Hub receives output from provider
    ↓
Looks up routing table:
    vmId: vm_123 → consumerConnectionId: conn_consumer_111
    ↓
Forwards via WebSocket to Consumer:
    {
      "type": "output",
      "data": "total 48\ndrwxr-xr-x..."
    }
    ↓
    ────────────────→  Consumer's Browser


┌─────────────────────────────────────────────────────────────────────────┐
│  STEP 5: Consumer Displays Output                                       │
└─────────────────────────────────────────────────────────────────────────┘

Consumer's Browser receives WebSocket message
    ↓
JavaScript passes to xterm.js:
    terminal.write("total 48\ndrwxr-xr-x...")
    ↓
User sees output in terminal! ✅
```

**Total Latency**: ~100-300ms (depends on provider's internet speed)

---

## 🔐 Security: How This Protects Providers

### What the Provider Machine NEVER Does:
❌ Never listens on any port  
❌ Never accepts incoming connections  
❌ Never exposes IP address to consumers  
❌ Never opens router ports (no port forwarding)  
❌ Never bypasses firewall

### What the Provider Machine DOES:
✅ Makes outbound connection to GCP (like visiting a website)  
✅ Receives messages through existing connection (like WebSocket chat)  
✅ Executes commands in isolated Docker containers  
✅ Sends responses back through same connection

### Analogy:
```
Traditional (unsafe):
Provider = Server listening on port 22
Consumer = Client connects directly to provider's IP
Problem: Provider exposed to internet attacks

UniCore (safe):
Provider = Employee checking company email
GCP = Company email server
Consumer = Another employee sending email
Result: Employees never directly exposed, all via trusted server
```

---

## 🌐 SignalR: The Technology Behind the Proxy

### What is SignalR?

SignalR is Microsoft's real-time web framework that:
- Establishes **persistent bidirectional connections** (WebSocket, Server-Sent Events, Long Polling)
- Handles **connection lifetime** (reconnection, heartbeats, timeouts)
- Provides **typed RPC** (Remote Procedure Calls) over the connection
- Supports **connection groups** for routing (like "all connections for vm_123")

### Why SignalR is Perfect for UniCore:

1. **Persistent Connections**
   - Provider connects once → stays connected for hours/days
   - Consumer connects per-session → stays connected during terminal use
   
2. **Bidirectional Messaging**
   - GCP can send to Provider: "ExecuteCommand"
   - Provider can send to GCP: "SendOutput"
   - No polling needed - instant delivery
   
3. **Connection Management**
   - Automatic reconnection if network drops
   - Heartbeat to detect dead connections
   - Graceful degradation (WebSocket → SSE → Long Polling)

4. **Scalability with Redis Backplane**
   - Multiple GCP Hub instances (Cloud Run scales up)
   - Redis coordinates routing across instances
   - Provider on Hub-1, Consumer on Hub-2 → works transparently

---

## 🚀 SignalR Hub Implementation (Conceptual)

### GCP Hub Code Structure

```csharp
// Central SignalR Hub running on GCP Cloud Run
public class UniCoreHub : Hub
{
    // Routing table: vmId → { providerConnectionId, consumerConnectionIds[] }
    private readonly IConnectionManager _connections;
    
    // ────────────────────────────────────────────────────────────
    // PROVIDER METHODS (called by provider desktop app)
    // ────────────────────────────────────────────────────────────
    
    public async Task ProviderConnect(string providerId, string machineSpecs)
    {
        // Provider desktop app connects
        var connectionId = Context.ConnectionId;
        
        // Register in routing table
        await _connections.RegisterProvider(providerId, connectionId, machineSpecs);
        
        Console.WriteLine($"Provider {providerId} connected with ID {connectionId}");
    }
    
    public async Task SendOutput(string vmId, string output)
    {
        // Provider sends terminal output → route to consumer
        
        // Look up which consumer(s) are watching this VM
        var consumerConnectionIds = await _connections.GetConsumersForVM(vmId);
        
        // Send output to all connected consumers for this VM
        await Clients.Clients(consumerConnectionIds).SendAsync("ReceiveOutput", output);
    }
    
    public async Task VMStarted(string vmId, string containerId)
    {
        // Provider reports VM successfully started
        await UpdateVMState(vmId, "running");
        
        // Notify consumer that VM is ready
        var consumerIds = await _connections.GetConsumersForVM(vmId);
        await Clients.Clients(consumerIds).SendAsync("VMReady", vmId);
    }
    
    // ────────────────────────────────────────────────────────────
    // CONSUMER METHODS (called by consumer web app)
    // ────────────────────────────────────────────────────────────
    
    public async Task JoinTerminal(string vmId)
    {
        // Consumer wants to connect to their VM's terminal
        var connectionId = Context.ConnectionId;
        
        // Validate: Does this consumer own this VM?
        var userId = Context.User?.FindFirst("sub")?.Value;
        var vm = await _firestore.GetVMAsync(vmId);
        
        if (vm.ConsumerId != userId)
        {
            throw new UnauthorizedAccessException("Not your VM!");
        }
        
        // Register consumer connection for this VM
        await _connections.RegisterConsumerForVM(vmId, connectionId);
        
        Console.WriteLine($"Consumer {userId} connected to VM {vmId}");
    }
    
    public async Task SendInput(string vmId, string input)
    {
        // Consumer sends terminal input → route to provider
        
        // Look up which provider is hosting this VM
        var providerId = await _connections.GetProviderForVM(vmId);
        var providerConnectionId = await _connections.GetProviderConnection(providerId);
        
        // Send command to provider
        await Clients.Client(providerConnectionId).SendAsync("ExecuteCommand", vmId, input);
    }
    
    // ────────────────────────────────────────────────────────────
    // CONNECTION LIFECYCLE
    // ────────────────────────────────────────────────────────────
    
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var connectionId = Context.ConnectionId;
        
        // Is this a provider disconnecting?
        if (await _connections.IsProvider(connectionId))
        {
            var providerId = await _connections.GetProviderId(connectionId);
            
            // Mark all VMs on this provider as "offline"
            await HandleProviderDisconnect(providerId);
            
            Console.WriteLine($"Provider {providerId} disconnected");
        }
        
        // Is this a consumer disconnecting?
        if (await _connections.IsConsumer(connectionId))
        {
            // Just remove from routing table (VM keeps running)
            await _connections.UnregisterConsumer(connectionId);
        }
        
        await base.OnDisconnectedAsync(exception);
    }
}
```

---

## 📊 Connection State Management

### In-Memory Routing Table (Redis)

```json
{
  "providers": {
    "prov_abc123": {
      "connectionId": "conn_xyz789",
      "status": "online",
      "vmsHosting": ["vm_123", "vm_456"],
      "lastHeartbeat": "2026-02-12T10:30:00Z"
    }
  },
  
  "consumers": {
    "vm_123": [
      {
        "userId": "user_111",
        "connectionId": "conn_consumer_111",
        "connectedAt": "2026-02-12T10:25:00Z"
      }
    ],
    "vm_456": [
      {
        "userId": "user_222",
        "connectionId": "conn_consumer_222",
        "connectedAt": "2026-02-12T10:28:00Z"
      }
    ]
  },
  
  "vmToProvider": {
    "vm_123": "prov_abc123",
    "vm_456": "prov_abc123"
  }
}
```

### Why Redis?
- **Fast lookups**: Routing decisions in <1ms
- **Shared state**: Multiple Cloud Run instances see same data
- **Pub/Sub**: SignalR backplane for scaling
- **Automatic expiry**: Dead connections cleaned up

---

## 🔄 Failure Scenarios & Handling

### Scenario 1: Provider Goes Offline Mid-Session

```
1. Provider's internet cuts out
   ↓
2. SignalR detects disconnection (missed heartbeats)
   ↓
3. GCP Hub triggers OnDisconnectedAsync()
   ↓
4. Update Firestore: All VMs on this provider → state: "offline"
   ↓
5. Notify connected consumers:
   "Your VM went offline. It will be restarted on another provider."
   ↓
6. Add VMs to job queue for reassignment
   ↓
7. Match with new provider → download volumes → restart VMs
```

**Consumer Experience**: Brief interruption (~30-60 seconds), then VM resumes

---

### Scenario 2: Consumer's Browser Closes

```
1. Consumer closes browser tab
   ↓
2. WebSocket disconnects
   ↓
3. GCP Hub removes consumer from routing table
   ↓
4. VM keeps running! (idle timeout will stop it after 10 min if no activity)
   ↓
5. Consumer can reconnect later:
   - Opens terminal again → reconnects to same VM
   - Sees previous state (persistent volume)
```

**Provider Experience**: No change, VM keeps running until stopped or idle

---

### Scenario 3: GCP Hub Restarts (Cloud Run auto-scales)

```
1. Cloud Run spins up new instance (traffic spike)
   ↓
2. Existing connections:
   - Providers: Auto-reconnect to new instance (SignalR reconnection)
   - Consumers: Auto-reconnect to new instance
   ↓
3. Redis backplane ensures routing table is shared:
   - New instance reads routing from Redis
   - Connections seamlessly migrate
   ↓
4. Zero downtime! ✅
```

---

## 🌍 Global Scaling Considerations

### Multi-Region Deployment (Post-MVP)

```
                        ┌─────────────────────────┐
                        │   Global Load Balancer  │
                        │   (Cloud Load Balancing)│
                        └────────────┬────────────┘
                                     │
                ┌────────────────────┼────────────────────┐
                ▼                    ▼                    ▼
        ┌───────────────┐    ┌───────────────┐    ┌───────────────┐
        │  us-west1     │    │  us-east1     │    │  europe-west1 │
        │  Cloud Run    │    │  Cloud Run    │    │  Cloud Run    │
        └───────┬───────┘    └───────┬───────┘    └───────┬───────┘
                │                    │                    │
                └────────────────────┼────────────────────┘
                                     │
                              ┌──────▼──────┐
                              │    Redis    │
                              │  (Global)   │
                              └─────────────┘
```

**Benefits**:
- Lower latency (consumer connects to nearest region)
- Higher availability (multi-region redundancy)
- Provider connects to nearest hub

---

## 📈 Performance Optimization

### Connection Pooling
```
Provider has 10 VMs running:
- Old approach: 10 separate connections (10x overhead)
- UniCore approach: 1 connection, multiplexed traffic
  └─> All 10 VMs share same SignalR connection
  └─> Messages tagged with vmId for routing
```

### Compression
```
Terminal output can be large (e.g., ls -la of huge directory)
- Enable WebSocket compression (permessage-deflate)
- Typical savings: 60-80% for text data
```

### Batching
```
High-frequency terminal output (e.g., cat large_file.txt):
- Buffer output for 50ms
- Send in batches instead of character-by-character
- Reduces message count by 90%+
```

---

## 🔬 Testing the Proxy Locally

### Development Setup

```bash
# Terminal 1: Start GCP Hub locally
cd unicore-hub
dotnet run
# Listening on: https://localhost:5001

# Terminal 2: Start Provider Desktop App
cd providerunicore
dotnet run
# Connected to: https://localhost:5001/provider

# Terminal 3: Start Consumer Web App
cd consumerunicore
dotnet run
# App running: https://localhost:7073
# Connects to: https://localhost:5001/terminal
```

### Verify Connection Flow:

1. Provider app logs: "✅ Connected to hub"
2. Create VM in consumer web app
3. Provider app logs: "📦 Received VM assignment: vm_123"
4. Consumer clicks "Open Terminal"
5. Provider app logs: "🔌 Consumer connected to vm_123"
6. Type in terminal → see output!

---

## 🎯 Summary: Why This Architecture Works

### ✅ Security
- Provider machines never exposed to internet
- All traffic through trusted GCP proxy
- JWT authentication on both sides
- Docker isolation for consumer code

### ✅ Simplicity
- No NAT traversal needed
- No port forwarding required
- Works behind corporate firewalls
- Provider = simple outbound connection (like browsing)

### ✅ Scalability
- SignalR + Redis = horizontal scaling
- Cloud Run = auto-scales to demand
- Stateless hub = easy to replicate

### ✅ Reliability
- Automatic reconnection
- Graceful degradation
- Multi-region redundancy (post-MVP)

### ✅ Low Latency
- Persistent connections (no handshake overhead)
- WebSocket = full-duplex (simultaneous send/receive)
- Minimal routing logic (~1ms per message)

---

## 🚀 Next Steps for Implementation

### Sprint 2-3 Focus:
1. **Set up SignalR Hub** on GCP Cloud Run
2. **Implement provider connection** in desktop app
3. **Test message routing** (provider → GCP → logs)
4. **Add Redis** for connection state

### Sprint 4-5 Focus:
1. **Add consumer WebSocket** connection
2. **Implement bidirectional routing** (consumer ↔ provider)
3. **Integrate xterm.js** for terminal UI
4. **Test end-to-end** terminal I/O

The proxy is the **heart** of UniCore - get this right and everything else follows! 💪
