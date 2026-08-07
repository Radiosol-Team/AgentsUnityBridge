using AgentsBridge.Daemon;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:9876");
builder.Services.AddSingleton<UnityConnectionManager>();

WebApplication app = builder.Build();
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(15)
});

app.MapBridgeEndpoints();
await app.RunAsync();

// Exposed for in-memory integration tests.
public partial class Program;
