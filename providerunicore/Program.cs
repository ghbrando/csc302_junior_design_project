using providerunicore.Components;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Google.Cloud.Firestore;

var builder = WebApplication.CreateBuilder(args);

// 1. Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var projectId = builder.Configuration["Firebase:ProjectId"];

// Add Authentication Services
builder.Services.AddSingleton(FirestoreDb.Create(projectId));
builder.Services.AddSingleton<IFirebaseAuthService, FirebaseAuthService>();
// Add Provider Service
builder.Services.AddScoped<IProviderService, ProviderService>();
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

// VM Service
builder.Services.AddSingleton<unicoreprovider.Services.VmService>();

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