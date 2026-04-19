using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Google.Cloud.Firestore;
using Google.Cloud.SecretManager.V1;
using consumerunicore.Components;
using consumerunicore.Services;
using unicoreconsumer.Services;


var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
        options.DetailedErrors = builder.Environment.IsDevelopment());
var projectId = builder.Configuration["Firebase:ProjectId"];

builder.Services.AddSingleton(FirestoreDb.Create(projectId));
builder.Services.AddSingleton<IFirebaseAuthService, FirebaseAuthService>();

builder.Services.AddFirestoreRepository<Consumer>(
    collectionName: "consumers",
    documentIdSelector: c => c.FirebaseUid);

builder.Services.AddScoped<IConsumerService, ConsumerService>();

// when logging in we may need to pivot a provider into a consumer
builder.Services.AddFirestoreRepository<Provider>(
    collectionName: "providers",
    documentIdSelector: p => p.FirebaseUid);
builder.Services.AddScoped<IProviderService, ProviderService>();

// Matchmaking: read-only access to machine_specs and virtual_machines
builder.Services.AddFirestoreRepository<MachineSpecs>(
    collectionName: "machine_specs",
    documentIdSelector: ms => ms.ProviderId);
builder.Services.AddFirestoreRepository<VirtualMachine>(
    collectionName: "virtual_machines",
    documentIdSelector: vm => vm.VmId);

builder.Services.AddFirestoreRepository<VmMigrationRequest>(
    collectionName: "vm_migration_requests",
    documentIdSelector: r => r.MigrationRequestId);

builder.Services.AddScoped<IMatchmakingService, MatchmakingService>();
builder.Services.AddScoped<IMigrationRequestService, MigrationRequestService>();
builder.Services.AddScoped<IWebShellService, WebShellService>();
builder.Services.AddScoped<IPaymentMethodService, PaymentMethodService>();
// Register ConsumerVmService through HttpClientFactory so it gets configured client
// instead of a plain scoped registration which would bypass the typed client.
// (previously caused BaseAddress to be missing.)
// builder.Services.AddScoped<IConsumerVmService, ConsumerVmService>();

// state service used by Blazor components to track current user
builder.Services.AddScoped<IAuthStateService, AuthStateService>();

// Configure HttpClient for API calls with BaseAddress and register service with it
builder.Services.AddScoped<IConsumerVmService, ConsumerVmService>();
builder.Services.AddSingleton<INotificationService, NotificationService>();

builder.Services.AddHttpClient();   // For Firebase REST API calls
builder.Services.AddControllers();  // Add API Controllers

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

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();
app.UseAuthentication();
app.UseAuthorization();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();


app.MapControllers();


app.Run();
