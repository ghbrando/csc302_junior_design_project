using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Google.Cloud.Firestore;
using Google.Cloud.SecretManager.V1;
using consumerunicore.Repositories;
using consumerunicore.Components;
using consumerunicore.Models;
using consumerunicore.Services;
using unicoreconsumer.Services;


var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
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

// state service used by Blazor components to track current user
builder.Services.AddScoped<IAuthStateService, AuthStateService>();

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
