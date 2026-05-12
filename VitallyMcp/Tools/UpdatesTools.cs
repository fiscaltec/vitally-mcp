using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VitallyMcp.Tools;

[McpServerToolType]
public static class UpdatesTools
{
    [McpServerTool(Name = "Check_for_updates", Title = "Check for updates", ReadOnly = true, Destructive = false), Description("Check the public GitHub Releases for a newer version of the Vitally MCP server. Returns the current and latest versions, whether an update is available, the release URL, and direct download links for both the Claude Desktop (.mcpb) and Claude Code (.exe) artefacts matching the current architecture.")]
    public static async Task<string> CheckForUpdates(UpdateCheckService updateCheckService)
    {
        return await updateCheckService.CheckForUpdatesAsync();
    }
}
