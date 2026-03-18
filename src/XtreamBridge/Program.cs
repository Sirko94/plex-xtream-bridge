using Serilog;
using XtreamBridge.Models;
using XtreamBridge.Persistence;
using XtreamBridge.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Serilog ───────────────────────────────────────────────────────────────────
var configDir = builder.Configuration["Paths:Config"] ?? "/config";
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        path: Path.Combine(configDir, "logs", "bridge-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7)
    .CreateLogger();

builder.Host.UseSerilog();

// ── Configuration ─────────────────────────────────────────────────────────────
// Load in priority order: appsettings.json → override file → env vars
var overridePath = Path.Combine(configDir, "appsettings.override.json");
builder.Configuration
    .AddJsonFile(overridePath, optional: true, reloadOnChange: true)
    .AddEnvironmentVariables(prefix: "XTREAM__");

builder.Services.Configure<AppSettings>(builder.Configuration);

// ── Persistence ───────────────────────────────────────────────────────────────
builder.Services.AddSingleton<SyncStateRepository>();

// ── HTTP Client ───────────────────────────────────────────────────────────────
builder.Services.AddHttpClient<XtreamClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

// ── Application Services ──────────────────────────────────────────────────────
builder.Services.AddScoped<XtreamClient>();
builder.Services.AddScoped<StrmGeneratorService>();
builder.Services.AddSingleton<EpgService>();
builder.Services.AddSingleton<SyncBackgroundService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SyncBackgroundService>());

// ── API ───────────────────────────────────────────────────────────────────────
builder.Services.AddControllers(); // responses use System.Text.Json; Newtonsoft.Json only used inside XtreamClient

builder.Services.AddResponseCaching();
builder.Services.AddEndpointsApiExplorer();

// ── Kestrel ───────────────────────────────────────────────────────────────────
builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(8080));

// ─────────────────────────────────────────────────────────────────────────────

var app = builder.Build();

app.UseSerilogRequestLogging(o =>
    o.MessageTemplate = "HTTP {RequestMethod} {RequestPath} → {StatusCode} ({Elapsed:0.0}ms)");

app.UseDefaultFiles();   // serve index.html from wwwroot
app.UseStaticFiles();    // serve JS/CSS from wwwroot
app.UseResponseCaching();
app.MapControllers();

// ── Startup validation ────────────────────────────────────────────────────────
var settings = app.Services
    .GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<AppSettings>>()
    .CurrentValue;

if (string.IsNullOrEmpty(settings.Server.BaseUrl))
{
    Log.Warning("⚠  No Xtream credentials configured yet — open http://<host>:8080 to set them up");
}
else
{
    Log.Information("XtreamBridge ready | Provider: {Url} | LiveTV={Live} | STRM={Strm}",
        settings.Server.BaseUrl, settings.Bridge.EnableLiveTv, settings.Bridge.EnableStrmGeneration);
}

await app.RunAsync();
