namespace VitallyMcp;

public class EntraOptions
{
    public const string SectionName = "Entra";

    public string Authority { get; set; } = string.Empty;

    public string Audience { get; set; } = string.Empty;

    /// <summary>
    /// When true, JWT authentication is skipped. Local development only — never enable in production.
    /// </summary>
    public bool NoAuth { get; set; }
}
