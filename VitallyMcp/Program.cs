using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VitallyMcp;

var builder = Host.CreateApplicationBuilder(args);

// Configure logging to standard error
builder.Logging.AddConsole(consoleLogOptions =>
{
    consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
});

// Load Vitally configuration from environment variables
var vitallyConfig = VitallyConfig.FromEnvironment();

// Register VitallyConfig as a singleton
builder.Services.AddSingleton(vitallyConfig);

// Register the rate-limit-aware HTTP handler and wire it into the VitallyService's HttpClient
builder.Services.AddTransient<VitallyRateLimitHandler>();
builder.Services.AddHttpClient<VitallyService>()
    .AddHttpMessageHandler<VitallyRateLimitHandler>();

// Configure MCP server with stdio transport and automatic tool discovery
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
