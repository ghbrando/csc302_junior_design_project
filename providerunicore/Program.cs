using providerunicore.Components;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Google.Cloud.Firestore;
using Google.Cloud.SecretManager.V1;
using unicoreprovider.Services;
using providerunicore.Services;
using System.Runtime.InteropServices;

var builder = WebApplication.CreateBuilder(args);

// 1. Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var projectId = builder.Configuration["Firebase:ProjectId"];

// Load sensitive relay config from GCP Secret Manager so they are not stored locally.
try
{
    var secretClient = SecretManagerServiceClient.Create();
    string Secret(string name) => secretClient
        .AccessSecretVersion($"projects/{projectId}/secrets/{name}/versions/latest")
        .Payload.Data.ToStringUtf8()
        .Trim();

    builder.Configuration["FrpRelay:ServerAddr"] = Secret("frp-relay-addr");
    builder.Configuration["FrpRelay:AuthToken"] = Secret("frp-relay-token");
}
catch (Exception ex)
{
    Console.WriteLine($"[WARNING] Could not load relay secrets from Secret Manager: {ex.Message}");
    Console.WriteLine("[WARNING] Falling back to appsettings.json values (if present).");
}

// Load GCP service account key for container GCS sync and Artifact Registry authentication
try
{
    var secretClient = SecretManagerServiceClient.Create();
    var gcpKey = secretClient
        .AccessSecretVersion($"projects/{projectId}/secrets/unicore-provider-gcp-key/versions/latest")
        .Payload.Data.ToStringUtf8()
        .Trim();
    Environment.SetEnvironmentVariable("GCP_SERVICE_ACCOUNT_KEY", gcpKey);
}
catch (Exception ex)
{
    Console.WriteLine($"[WARNING] GCP key not loaded from Secret Manager: {ex.Message}");
    Console.WriteLine("[WARNING] GCS sync and Artifact Registry auth will fail, but VMs will still start.");
}

// Add Authentication Services
builder.Services.AddSingleton(FirestoreDb.Create(projectId));
builder.Services.AddSingleton<IFirebaseAuthService, FirebaseAuthService>();

// Register Repositories
builder.Services.AddFirestoreRepository<Provider>(
    collectionName: "providers",
    documentIdSelector: p => p.FirebaseUid);

// allow lookup of consumer records when promoting to provider
builder.Services.AddFirestoreRepository<Consumer>(
    collectionName: "consumers",
    documentIdSelector: c => c.FirebaseUid);

builder.Services.AddFirestoreRepository<VirtualMachine>(
    collectionName: "virtual_machines",
    documentIdSelector: vm => vm.VmId);

builder.Services.AddFirestoreRepository<Payout>(
    collectionName: "payouts");

builder.Services.AddFirestoreRepository<MachineSpecs>(
    collectionName: "machine_specs",
    documentIdSelector: ms => ms.ProviderId);

builder.Services.AddFirestoreRepository<VmMigrationRequest>(
    collectionName: "vm_migration_requests",
    documentIdSelector: r => r.MigrationRequestId);

// Add Services
builder.Services.AddScoped<IProviderService, ProviderService>();
// consumer lookup service used during auth fallback
builder.Services.AddScoped<IConsumerService, ConsumerService>();
builder.Services.AddScoped<IVmService, VirtualMachineService>();
builder.Services.AddScoped<IPayoutService, PayoutService>();
builder.Services.AddSingleton<IAuthStateService, AuthStateService>();
builder.Services.AddScoped<IMachineService, MachineService>();
builder.Services.AddHttpClient();   // For Firebase REST API calls
builder.Services.AddControllers(); // Add API Controllers

// Docker services — registered as Singleton so the monitor can receive
// StartMonitoring() calls from the Dashboard and persist across requests.
builder.Services.AddSingleton<INotificationService, NotificationService>();
builder.Services.AddSingleton<IDockerService, DockerService>();
builder.Services.AddSingleton<ContainerMonitorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ContainerMonitorService>());
builder.Services.AddSingleton<PauseResumeListenerService>();


builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.Authority = $"https://securetoken.google.com/{projectId}";
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = $"https://securetoken.google.com/{projectId}",
        ValidateAudience = true,
        ValidAudience = projectId,
        ValidateLifetime = true
    };
});

builder.Services.AddAuthorization();

var app = builder.Build();

var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
{
    using var scope = app.Services.CreateScope();
    var AuthState = app.Services.GetRequiredService<IAuthStateService>(); // Singleton, can resolve from root
    String? firebaseUID = AuthState.FirebaseUid;
    if (!string.IsNullOrEmpty(firebaseUID))
    {
        var ProviderService = scope.ServiceProvider.GetRequiredService<IProviderService>();
        try
        {
            ProviderService.UpdateNodeStatusAsync("Offline", firebaseUID).Wait();
        }
        catch (Exception ex)
        {
            // Log the error but don't crash the shutdown
            Console.WriteLine($"Failed to update provider status to offline: {ex.Message}");
        }
    }
});
// Check for 'notify-send' package on Linux at startup
// Necessary for desktop push notifications
if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
{
    var check = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
    {
        FileName = "which",
        Arguments = "notify-send",
        RedirectStandardOutput = true,
        UseShellExecute = false
    });
    check?.WaitForExit();
    if (check?.ExitCode != 0)
    {
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogWarning("notify-send not found. Install libnotify-bin for desktop notifications.");
    }
}

// 2. Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles(); // Allows serving CSS/JS files
app.UseAntiforgery(); // Security feature for forms
app.UseAuthentication(); // Enable authentication
app.UseAuthorization(); // Enable authorization

// 3. Map the HTML Root (App.razor)
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode(); // Enables interactivity

// Map API Controllers
app.MapControllers();

app.Run();