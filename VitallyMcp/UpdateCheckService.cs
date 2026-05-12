using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace VitallyMcp;

/// <summary>
/// Checks the public GitHub Releases API for newer versions of this server.
/// Anonymous (60 requests/hour limit, plenty for on-demand checks).
/// </summary>
public class UpdateCheckService
{
    private const string RepoOwner = "fiscaltec";
    private const string RepoName = "vitally-mcp";
    private const string LatestReleaseUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    private readonly HttpClient _httpClient;

    public UpdateCheckService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        // GitHub requires a User-Agent on all API requests
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("VitallyMcp", GetCurrentVersion()));
        }
        if (!_httpClient.DefaultRequestHeaders.Accept.Any())
        {
            _httpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        }
    }

    public async Task<string> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var currentVersion = GetCurrentVersion();
        var arch = GetArchitectureSuffix();

        try
        {
            var response = await _httpClient.GetAsync(LatestReleaseUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            return BuildResult(currentVersion, arch, json);
        }
        catch (HttpRequestException ex)
        {
            return JsonSerializer.Serialize(new
            {
                currentVersion,
                error = $"Failed to reach GitHub Releases API: {ex.Message}",
                releasesUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases"
            });
        }
    }

    private static string BuildResult(string currentVersion, string archSuffix, string releaseJson)
    {
        using var doc = JsonDocument.Parse(releaseJson);
        var root = doc.RootElement;

        var tagName = root.GetProperty("tag_name").GetString() ?? string.Empty;
        var latestVersion = tagName.TrimStart('v', 'V');
        var releaseUrl = root.GetProperty("html_url").GetString();
        var publishedAt = root.GetProperty("published_at").GetString();

        var mcpbUrl = FindAssetUrl(root, $"{archSuffix}.mcpb");
        var exeUrl = FindAssetUrl(root, $"{archSuffix}.exe");

        var isUpToDate = CompareVersions(currentVersion, latestVersion) >= 0;

        return JsonSerializer.Serialize(new
        {
            currentVersion,
            latestVersion,
            isUpToDate,
            architecture = archSuffix,
            releaseUrl,
            mcpbDownloadUrl = mcpbUrl,
            exeDownloadUrl = exeUrl,
            publishedAt,
            updateInstructions = isUpToDate
                ? "Already on the latest version."
                : "Claude Desktop: download the .mcpb above and double-click to install. " +
                  "Claude Code: download the .exe above and replace the binary referenced in your mcp.json."
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string? FindAssetUrl(JsonElement releaseRoot, string nameSuffix)
    {
        if (!releaseRoot.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
            if (name is not null && name.EndsWith(nameSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return asset.TryGetProperty("browser_download_url", out var urlEl) ? urlEl.GetString() : null;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the runtime suffix used in release asset filenames.
    /// </summary>
    private static string GetArchitectureSuffix() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.Arm64 => "win-arm64",
        _ => "win-x64"
    };

    private static string GetCurrentVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        // AssemblyInformationalVersion is what Version in csproj feeds into; falls back to AssemblyVersion
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            // Strip any +commit-hash build metadata
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        var version = asm.GetName().Version;
        return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    /// <summary>
    /// Compares two semver-style version strings. Returns negative if a&lt;b, 0 if equal, positive if a&gt;b.
    /// Tolerant of trailing zeros and missing components (e.g. "3.0" == "3.0.0").
    /// </summary>
    private static int CompareVersions(string a, string b)
    {
        var aParts = a.Split('.');
        var bParts = b.Split('.');
        var max = Math.Max(aParts.Length, bParts.Length);
        for (var i = 0; i < max; i++)
        {
            var aPart = i < aParts.Length && int.TryParse(aParts[i], out var ap) ? ap : 0;
            var bPart = i < bParts.Length && int.TryParse(bParts[i], out var bp) ? bp : 0;
            if (aPart != bPart)
            {
                return aPart.CompareTo(bPart);
            }
        }
        return 0;
    }
}
