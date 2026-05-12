# VitallyMcp.Tests

Comprehensive automated test suite for the Vitally MCP server.

## Coverage

**179 tests, all passing** (xUnit + FluentAssertions + Moq), running fully mocked against `HttpMessageHandler` — no live API calls.

### Test classes

| File | Scope |
|------|-------|
| `VitallyConfigTests` | Environment variable loading and validation for `VITALLY_API_KEY` / `VITALLY_SUBDOMAIN`. Runs sequentially via `[Collection]` to avoid env-var conflicts. |
| `VitallyServiceTests` | Field/trait filtering, pagination, resource-specific defaults across every resource type, plus full coverage of `GetResourcesAsync`, `GetResourceByIdAsync`, `CreateResourceAsync`, `UpdateResourceAsync`, `DeleteResourceAsync`, `GetRawAsync` (with URL-encoded query params), `PostRawAsync`, `DeleteRawAsync`. Includes HTTP-verb, path, and Basic-auth header verification via Moq's `Protected().Verify(...)`. |
| `Tools/AccountsToolsTests` | List / get / create / update / delete + status filter + traits + list-by-organisation |
| `Tools/OrganizationsToolsTests` | CRUD + traits |
| `Tools/UsersToolsTests` | CRUD + search + list-by-account/organisation + traits |
| `Tools/AdminsToolsTests` | `SearchAdmins` by email |
| `Tools/ConversationsToolsTests` | CRUD + sub-paths (by account, by organisation) |
| `Tools/MessagesToolsTests` | List by conversation + get / create / delete |
| `Tools/NotesToolsTests` | CRUD + sub-paths + `ListNoteCategories` + traits |
| `Tools/ProjectsToolsTests` | CRUD + sub-paths + create-from-template + traits |
| `Tools/ProjectTemplatesToolsTests` | Templates + categories + categoryId filter + traits |
| `Tools/TasksToolsTests` | CRUD + sub-paths + `ListTaskCategories` + traits |
| `Tools/NpsResponsesToolsTests` | CRUD + sub-paths |
| `Tools/CustomObjectsToolsTests` | Objects + instances + search + CRUD |
| `Tools/MeetingsToolsTests` | Full CRUD + add / remove participant + 4 transcript methods + `archived` filter + traits |
| `Tools/CustomTraitsToolsTests` | List custom traits for `accounts` and `customObjects` models |
| `Tools/SurveysToolsTests` | List responses + get response + get question (raw `{data}` envelope passthrough) |

## Framework & dependencies

- **xUnit** 2.9.3 — test framework
- **Moq** 4.20.72 — HttpClient mocking (uses `Mock<HttpMessageHandler>` + `Protected()`)
- **FluentAssertions** 8.9.0 — readable assertions
- **coverlet.collector** 10.0.0 — code coverage
- **Microsoft.NET.Test.Sdk** 18.5.1

Targets `net10.0` to match the main project.

## Running

```powershell
# Full suite (from repo root)
dotnet test VitallyMcp.sln -c Debug --nologo --verbosity minimal

# Just one class
dotnet test --filter "FullyQualifiedName~MeetingsToolsTests"

# Just one method
dotnet test --filter "Name~AddMeetingParticipant"

# With coverage (opencover)
dotnet test /p:CollectCoverage=true /p:CoverletOutputFormat=opencover
```

## Patterns

### Mock HTTP client

```csharp
var client = TestHelpers.CreateMockHttpClient(jsonResponse);
var service = new VitallyService(client, new VitallyConfig { ApiKey = "...", Subdomain = "..." });
```

### URL / verb verification

```csharp
var (client, handler) = TestHelpers.CreateMockHttpClientWithHandler(json);
// ... act ...
handler.Protected().Verify(
    "SendAsync",
    Times.Once(),
    ItExpr.Is<HttpRequestMessage>(req =>
        req.Method == HttpMethod.Post
        && req.RequestUri!.AbsolutePath == "/resources/meetings/m-1/participants"),
    ItExpr.IsAny<CancellationToken>());
```

### Sample JSON

`TestHelpers.cs` exposes per-resource sample payloads (`GetSampleAccountJson`, `GetSampleMeetingJson`, etc.). Add a new helper there when you need a shape that doesn't fit an existing one; don't refactor existing helpers.

### Env-var tests

`VitallyConfigTests` mutates real environment variables, so it uses `[Collection("VitallyConfig Tests")]` to run sequentially and always cleans up in a `finally` block.

## Adding tests

When you add a new tool method (or a new tool class):

1. Add the matching test method in `Tools/{ResourceName}ToolsTests.cs`. One test per public `[McpServerTool]` method is the baseline.
2. For raw-passthrough methods (no field filtering — `GetRawAsync`/`PostRawAsync`/`DeleteRawAsync` based), mock the exact response shape the Vitally API returns and assert the raw body comes back through unchanged.
3. If the response shape is new, add a sample JSON helper to `TestHelpers.cs`.
4. If the tool exercises new behaviour at the service layer, add a corresponding `VitallyServiceTests` case.

## CI

The suite has no external dependencies and finishes in <500 ms, so it's safe to run in any CI step that follows `dotnet build`.

## Out of scope (intentional)

- Live Vitally API integration — covered by manual testing as described in `../CLAUDE.md`
- MCP protocol integration — relies on the `ModelContextProtocol` SDK's own test coverage
- End-to-end tests against Claude Desktop — covered by the manual install workflow
