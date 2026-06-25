using System.Net.Http;
using ModelContextProtocol.Protocol;

namespace VitallyMcp;

/// <summary>
/// Maps an "expected" tool exception to a <see cref="CallToolResult"/> whose text carries the real
/// message, so the MCP client (the calling LLM) sees the actual failure reason instead of the SDK's
/// generic "An error occurred invoking 'X'." Only a curated set of exception types is surfaced;
/// anything else is left to propagate so the SDK keeps its protocol/cancellation handling and the
/// generic message (we never leak unexpected internal exception detail).
/// </summary>
public static class ToolErrorResult
{
    /// <summary>
    /// The exceptions whose message we deliberately surface: Vitally API failures
    /// (<see cref="HttpRequestException"/>, body included by VitallyService.SendAsync), the
    /// read-only / RBAC denial (<see cref="UnauthorizedAccessException"/>), and our own client-side
    /// validation (<see cref="ArgumentException"/>).
    /// </summary>
    public static bool IsSurfaceable(Exception ex) =>
        ex is HttpRequestException or UnauthorizedAccessException or ArgumentException;

    public static CallToolResult Build(Exception ex) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = ex.Message }]
    };
}
