using CeruleanToolbox;
using CeruleanToolbox.Components;
using CeruleanToolbox.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// The configuration key ConnectionStoreFolder gives the app a separate store of saved connections (for demos and tests).
builder.Services.AddCeruleanToolboxServices(builder.Configuration["ConnectionStoreFolder"] ?? "CeruleanToolbox");
builder.Services.AddSingleton<IConnectionFileDialogService, BrowserConnectionFileDialogService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

// POST /api/connections/{id}/export: the browser downloads the encrypted file (defined in Toolbox.Core).
app.MapConnectionExport();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
