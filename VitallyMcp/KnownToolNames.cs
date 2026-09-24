namespace VitallyMcp;

/// <summary>
/// The tool names this server actually registered.
/// </summary>
/// <remarks>
/// <para>
/// Exists so <see cref="AuditLogger"/> can tell a real tool name from one a caller invented. The
/// audit filter runs for <b>unknown</b> tool names too — a <c>tools/call</c> naming a tool that does
/// not exist is still a call that happened and still gets a record — so the name reaching the
/// breadcrumb is caller-controlled text, and the console stream is the one the data map declares
/// customer-data-free.
/// </para>
/// <para>
/// ⚠️ <b>A shape check is not enough, which is what this replaced.</b> Matching
/// <c>^[A-Za-z][A-Za-z0-9_]{0,63}$</c> excludes an email and a hyphenated record id, but
/// <c>Acme_123</c> and <c>alice</c> pass it happily — and a customer name or a person's name is
/// exactly the sort of identifier the console table must not carry. Membership of the registered set
/// is the only test that actually answers the question.
/// </para>
/// </remarks>
public sealed class KnownToolNames
{
    private readonly HashSet<string> _names;

    public KnownToolNames(IEnumerable<string> names) =>
        _names = new HashSet<string>(names, StringComparer.Ordinal);

    /// <summary>How many tools were registered, for the startup log line and for tests.</summary>
    public int Count => _names.Count;

    /// <summary>Whether <paramref name="name"/> is a tool this server registered.</summary>
    public bool IsRegistered(string? name) => name is not null && _names.Contains(name);
}
