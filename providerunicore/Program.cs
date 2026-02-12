using providerunicore.Components;

var builder = WebApplication.CreateBuilder(args);

// 1. Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

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

// 3. Map the HTML Root (App.razor)
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode(); // Enables interactivity

app.Run();