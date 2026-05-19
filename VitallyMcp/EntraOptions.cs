namespace VitallyMcp;

public class EntraOptions
{
    public const string SectionName = "Entra";

    public string Authority { get; set; } = string.Empty;

    /// <summary>
    /// Value to validate the JWT <c>aud</c> claim against. For v2 Entra tokens this is typically
    /// the application (client) ID GUID, not the identifier URI.
    /// </summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>
    /// Canonical resource identifier published in <c>/.well-known/oauth-protected-resource</c>'s
    /// <c>resource</c> field per RFC 9728. Clients (e.g. Claude Code) validate this matches the
    /// server URL or origin, then send it as the <c>resource=</c> parameter on their OAuth
    /// authorize call. Must be one of the identifier URIs registered on the Entra application.
    /// If left empty, falls back to <see cref="Audience"/> for backwards compatibility.
    /// </summary>
    public string Resource { get; set; } = string.Empty;

    /// <summary>
    /// When true, JWT authentication is skipped. Local development only — never enable in production.
    /// </summary>
    public bool NoAuth { get; set; }
}
