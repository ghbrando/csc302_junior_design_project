using Google.Cloud.Firestore;
using Google.Cloud.SecretManager.V1;
using heartbeatservice.Workers;

var builder = WebApplication.CreateBuilder(args);

var projectId = builder.Configuration["Firebase:ProjectId"]
    ?? throw new InvalidOperationException("Firebase:ProjectId is required in appsettings.json");

// Load the relay VM address from GCP Secret Manager (same secret as providerunicore).
// Falls back to appsettings.json value if Secret Manager is unavailable (e.g., local dev).
try
{
    var secretClient = SecretManagerServiceClient.Create();
    string Secret(string name) => secretClient
        .AccessSecretVersion($"projects/{projectId}/secrets/{name}/versions/latest")
        .Payload.Data.ToStringUtf8()
        .Trim();

    builder.Configuration["FrpRelay:ServerAddr"] = Secret("frp-relay-addr");
}
catch (Exception ex)
{
    Console.WriteLine($"[WARNING] Could not load relay secrets from Secret Manager: {ex.Message}");
    Console.WriteLine("[WARNING] Falling back to appsettings.json FrpRelay:ServerAddr.");
}

// Register Firestore
builder.Services.AddSingleton(FirestoreDb.Create(projectId));

// Register repositories (Singleton — worker is long-lived, Firestore client is thread-safe)
builder.Services.AddFirestoreRepository<UniCore.Shared.Models.Provider>(
    collectionName: "providers",
    documentIdSelector: p => p.FirebaseUid,
    lifetime: ServiceLifetime.Singleton);

builder.Services.AddFirestoreRepository<UniCore.Shared.Models.VirtualMachine>(
    collectionName: "virtual_machines",
    documentIdSelector: vm => vm.VmId,
    lifetime: ServiceLifetime.Singleton);

// Register the heartbeat background worker
builder.Services.AddHostedService<HeartbeatWorker>();

var app = builder.Build();

// Cloud Run requires the container to listen on HTTP and respond to health checks.
// This minimal endpoint satisfies that requirement without adding any other web functionality.
app.MapGet("/healthz", () => Results.Ok("healthy"));

app.Run();
