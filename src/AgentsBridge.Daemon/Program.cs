using AgentsBridge.Daemon;
using AgentsBridge.Local;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:9876");
builder.Services.AddSingleton<UnityConnectionManager>();
builder.Services.AddSingleton<UnityProcessMonitor>();
builder.Services.AddSingleton<UnityCrashDetector>();
builder.Services.AddSingleton<UnityHubProjectDiscovery>();
builder.Services.AddSingleton<UnityEditorLauncher>();
builder.Services.AddSingleton<ApiCallLog>();

WebApplication app = builder.Build();
app.UseMiddleware<ApiCallLoggingMiddleware>();
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(15)
});

app.MapBridgeEndpoints();
await app.RunAsync();

// Exposed for in-memory integration tests.
public partial class Program;
