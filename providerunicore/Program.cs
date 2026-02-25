using providerunicore.Components;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Google.Cloud.Firestore;
using Google.Cloud.SecretManager.V1;
using unicoreprovider.Services;

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

// Add Services
builder.Services.AddScoped<IProviderService, ProviderService>();
// consumer lookup service used during auth fallback
builder.Services.AddScoped<IConsumerService, ConsumerService>();
builder.Services.AddScoped<IVmService, VirtualMachineService>();
builder.Services.AddScoped<IPayoutService, PayoutService>();
builder.Services.AddScoped<IAuthStateService, AuthStateService>();
builder.Services.AddScoped<IMachineService, MachineService>();
builder.Services.AddHttpClient();   // For Firebase REST API calls
builder.Services.AddControllers(); // Add API Controllers

// Docker services — registered as Singleton so the monitor can receive
// StartMonitoring() calls from the Dashboard and persist across requests.
builder.Services.AddSingleton<IDockerService, DockerService>();
builder.Services.AddSingleton<ContainerMonitorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ContainerMonitorService>());


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