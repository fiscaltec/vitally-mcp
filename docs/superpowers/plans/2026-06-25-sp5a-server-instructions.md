# SP5a — Server-level MCP `instructions` — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish concise usage guidance in the MCP server's `initialize` `instructions` field so every client/LLM is steered toward organisation-level data, the traits-vs-custom-objects distinction, the find/period-scope/source tooling, and the read-only model.

**Architecture:** Hold the guidance in a named constant (`VitallyServerInstructions.Text`); set it on the MCP server via the `AddMcpServer` options action (`McpServerOptions.ServerInstructions`). A unit test guards the text's content; an integration test proves it reaches the `initialize` response.

**Tech Stack:** .NET 10, C#, `ModelContextProtocol(.AspNetCore) 1.3.0` (`McpServerOptions.ServerInstructions` confirmed present), xUnit + FluentAssertions, `Microsoft.AspNetCore.Mvc.Testing` (`WebApplicationFactory`).

**Branch:** `feature/sp5a-server-instructions` (created off `main`; spec committed there).

**Spec:** [SP5a design](../specs/2026-06-25-sp5a-server-instructions-design.md)

## Global Constraints

- UK English in comments/docs/guidance. Repo uses **LF** line endings (`.gitattributes` present).
- The guidance must cover four themes (organisation-level data; traits-vs-custom-objects + instance scoping; find/period-scope/source tooling; read-only/permission model) and must NOT assert an unverified tenant-specific object name (no literal "customerGoals" as fact).
- Build/run from repo root; do NOT prefix commands with `cd`.

---

### Task 1: `VitallyServerInstructions` constant

**Files:**
- Create: `VitallyMcp/VitallyServerInstructions.cs`
- Test: `VitallyMcp.Tests/VitallyServerInstructionsTests.cs`

**Interfaces:**
- Produces: `VitallyServerInstructions.Text` (`public const string`).

- [ ] **Step 1: Write the failing test**

Create `VitallyMcp.Tests/VitallyServerInstructionsTests.cs`:

```csharp
using FluentAssertions;
using VitallyMcp;

namespace VitallyMcp.Tests;

public class VitallyServerInstructionsTests
{
    [Theory]
    [InlineData("organisation")]
    [InlineData("Traits")]
    [InlineData("custom object")]
    [InlineData("List_organizations(nameContains")]
    [InlineData("createdAfter")]
    [InlineData("Read-only")]
    public void Text_ContainsKeyGuidanceMarkers(string marker)
    {
        VitallyServerInstructions.Text.Should().Contain(marker);
    }

    [Fact]
    public void Text_IsNotEmpty()
    {
        VitallyServerInstructions.Text.Should().NotBeNullOrWhiteSpace();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~VitallyServerInstructionsTests" --nologo`
Expected: FAIL — `VitallyServerInstructions` not defined (compile error).

- [ ] **Step 3: Implement the constant**

Create `VitallyMcp/VitallyServerInstructions.cs` (ASCII; UK English):

```csharp
namespace VitallyMcp;

/// <summary>
/// Usage guidance published in the MCP <c>initialize</c> response (<c>instructions</c> field) so
/// every connecting client/LLM is steered toward effective use of the Vitally server. Kept in one
/// named constant so it is unit-testable and not buried inline in <c>Program.cs</c>.
/// </summary>
public static class VitallyServerInstructions
{
    public const string Text = """
        Vitally MCP server - how to use it well:
        - Rich customer data lives at the ORGANISATION level (mrr, healthScore, renewal dates, NPS,
          segments, traits). Account-level traits are often empty, so prefer organisations for
          customer context.
        - Traits are not the same as custom objects. Traits are key/value fields on a resource
          (request them via the 'traits' parameter; they are excluded by default). Custom objects
          are separate record types (e.g. goals, opportunities). Discover a tenant's custom objects
          with List_custom_objects, then get one organisation's instances in a single call:
          List_custom_object_instances(customObjectId, organizationId=...).
        - Find a customer with List_organizations(nameContains=...). Scope activity to a period with
          createdAfter/createdBefore on List_conversations, List_notes, List_tasks and List_meetings.
          Conversations carry 'source' (e.g. outlook, intercom) and 'status' to tell support tickets
          from calendar/email.
        - Read-only deployments deny all writes and hide the create/update/delete tools; a
          permission error means your token lacks the required tier.
        """;
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter "FullyQualifiedName~VitallyServerInstructionsTests" --nologo`
Expected: PASS (7 cases).

- [ ] **Step 5: Commit**

```powershell
git add VitallyMcp/VitallyServerInstructions.cs VitallyMcp.Tests/VitallyServerInstructionsTests.cs
git commit -m @'
feat(guidance): add VitallyServerInstructions guidance text (SP5a)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 2: Wire into the MCP server + integration guard

**Files:**
- Modify: `VitallyMcp/Program.cs`
- Test: `VitallyMcp.Tests/ServerInstructionsInitializeTests.cs`

**Interfaces:**
- Consumes: `VitallyServerInstructions.Text` (Task 1).

- [ ] **Step 1: Write the failing integration test**

Create `VitallyMcp.Tests/ServerInstructionsInitializeTests.cs` (mirrors the `WebApplicationFactory` + SSE-parsing pattern used by `ReadOnlyToolsListTests`; sets only NoAuth + EU + a dummy key — NOT read-only):

```csharp
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace VitallyMcp.Tests;

public class ServerInstructionsInitializeTests : IClassFixture<ServerInstructionsInitializeTests.Factory>
{
    private readonly Factory _factory;

    public ServerInstructionsInitializeTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task Initialize_ReturnsServerInstructions()
    {
        using var client = _factory.CreateClient();

        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { },
                clientInfo = new { name = "test", version = "0.0.1" }
            }
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        // Body may be SSE-framed (data: {…}); the JSON-RPC result carries result.instructions.
        var json = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimStart())
            .Where(l => l.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            .Select(l => l["data:".Length..].Trim())
            .FirstOrDefault(c => c.StartsWith('{')) ?? text.Trim();

        using var doc = JsonDocument.Parse(json);
        var instructions = doc.RootElement.GetProperty("result").GetProperty("instructions").GetString();

        instructions.Should().NotBeNullOrWhiteSpace();
        instructions.Should().Contain("organisation");
        instructions.Should().Contain("custom object");
    }

    public class Factory : WebApplicationFactory<Program>
    {
        public Factory()
        {
            Environment.SetEnvironmentVariable("OAuth__NoAuth", "true");
            Environment.SetEnvironmentVariable("Vitally__Region", "EU");
            Environment.SetEnvironmentVariable("Vitally__DevelopmentApiKey", "sk_test_dummy");
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            return base.CreateHost(builder);
        }

        protected override void Dispose(bool disposing)
        {
            Environment.SetEnvironmentVariable("OAuth__NoAuth", null);
            Environment.SetEnvironmentVariable("Vitally__Region", null);
            Environment.SetEnvironmentVariable("Vitally__DevelopmentApiKey", null);
            base.Dispose(disposing);
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ServerInstructionsInitializeTests" --nologo`
Expected: FAIL — `result.instructions` is absent/null (the server doesn't set it yet), so `GetProperty("instructions")` throws or the value is null.

- [ ] **Step 3: Wire the instructions into the MCP server**

In `VitallyMcp/Program.cs`, change the `AddMcpServer()` call (currently `var mcpBuilder = builder.Services.AddMcpServer()`) to pass the options action:

```csharp
var mcpBuilder = builder.Services.AddMcpServer(options => options.ServerInstructions = VitallyServerInstructions.Text)
    .WithHttpTransport(options => options.Stateless = true)
    .WithToolsFromAssembly();
```

Leave the rest of the MCP registration (the `WithRequestFilters(...)` block with the CallTool + read-only ListTools filters) unchanged. If the installed 1.3.0 `AddMcpServer` options type names the property differently than `ServerInstructions`, adjust to the installed API — the intent is: set the server's `instructions`. (Add any required `using`.)

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter "FullyQualifiedName~ServerInstructionsInitializeTests" --nologo`
Expected: PASS.

- [ ] **Step 5: Full suite + commit**

Run: `dotnet test VitallyMcp.sln -c Debug --nologo --verbosity minimal` — all green.

```powershell
git add VitallyMcp/Program.cs VitallyMcp.Tests/ServerInstructionsInitializeTests.cs
git commit -m @'
feat(guidance): publish server instructions in the MCP initialize response (SP5a)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

- [ ] **Step 6: Live check (optional, no Vitally key needed)**

Start NoAuth (`OAuth__NoAuth=true`, `Vitally__Region=EU`, `Vitally__DevelopmentApiKey=sk_live_placeholder`, `ASPNETCORE_URLS=http://localhost:5099`, `dotnet run --project VitallyMcp/VitallyMcp.csproj` in the background; poll for readiness). POST an `initialize` and confirm the SSE response's `result.instructions` carries the guidance. Stop the server (`Get-Process -Name VitallyMcp | Stop-Process -Force`).

---

### Task 3: Docs (`CLAUDE.md`)

**Files:**
- Modify: `CLAUDE.md`

- [ ] **Step 1: Note the published instructions**

In `CLAUDE.md`, in the "Hosting and transport (Program.cs)" bullet list, add a bullet after the line about the MCP server registration (`- MCP server registered via AddMcpServer()...`):

```markdown
- Publishes server-level usage guidance in the MCP `initialize` response via `McpServerOptions.ServerInstructions` (text in `VitallyServerInstructions.Text`): steers clients toward organisation-level data, the traits-vs-custom-objects distinction, the name/date-range filters, and the read-only/permission model.
```

- [ ] **Step 2: Commit**

```powershell
git add CLAUDE.md
git commit -m @'
docs: note the published MCP server instructions (SP5a)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

## Final verification

- [ ] `dotnet test VitallyMcp.sln -c Debug --nologo --verbosity minimal` — all green.
- [ ] Open a PR from `feature/sp5a-server-instructions` into `main`; CI green; resolve any Copilot/CodeQL review threads (the `main` ruleset requires resolution); merge (squash).
