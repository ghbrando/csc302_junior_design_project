using providerunicore.Components;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Google.Cloud.Firestore;
using providerunicore.Repositories;
using unicoreprovider.Models;
using unicoreprovider.Services;

var builder = WebApplication.CreateBuilder(args);

// 1. Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var projectId = builder.Configuration["Firebase:ProjectId"];

// Add Authentication Services
builder.Services.AddSingleton(FirestoreDb.Create(projectId));
builder.Services.AddSingleton<IFirebaseAuthService, FirebaseAuthService>();

// Register Repositories
builder.Services.AddFirestoreRepository<Provider>(
    collectionName: "providers",
    documentIdSelector: p => p.FirebaseUid);

builder.Services.AddFirestoreRepository<VirtualMachine>(
    collectionName: "virtual_machines",
    documentIdSelector: vm => vm.VmId);

builder.Services.AddFirestoreRepository<Payout>(
    collectionName: "payouts");

// Add Services
builder.Services.AddScoped<IProviderService, ProviderService>();
builder.Services.AddScoped<IVmService, VirtualMachineService>();
builder.Services.AddScoped<IPayoutService, PayoutService>();
builder.Services.AddHttpClient();   // For Firebase REST API calls
builder.Services.AddControllers(); // Add API Controllers


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