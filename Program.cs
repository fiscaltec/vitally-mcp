using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
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

// Register HttpClient and VitallyService
builder.Services.AddHttpClient<VitallyService>();

// Configure MCP server with stdio transport and automatic tool discovery
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
