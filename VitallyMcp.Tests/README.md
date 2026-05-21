# VitallyMcp.Tests

Automated test suite for the Vitally MCP server.

## Coverage

**216 tests, all passing** (xUnit + FluentAssertions + Moq + ASP.NET Core
test host), running fully in-process — no live API calls, no real Auth0
tenant, no Key Vault.

### Test classes

| File | Scope |
|------|-------|
| `VitallyApiKeyProviderTests` | Dev-fallback resolution: no `SecretClient` registered → returns `DevelopmentApiKey`; neither set → throws. |
| `VitallyServiceTests` | Field/trait filtering, pagination, resource-specific defaults across every resource type, plus full coverage of `GetResourcesAsync`, `GetResourceByIdAsync`, `CreateResourceAsync`, `UpdateResourceAsync`, `DeleteResourceAsync`, `GetRawAsync` (with URL-encoded query params), `PostRawAsync`, `DeleteRawAsync`. Includes HTTP-verb, path, and Basic-auth header verification via Moq's `Protected().Verify(...)`. Also asserts that the response body is surfaced in `HttpRequestException` on non-2xx responses (regression guard). |
| `VitallyRateLimitHandlerTests` | 429 retry behaviour, `Retry-After`/`X-RateLimit-Reset` header parsing, low-remaining warnings. |
| `OAuthOptionsTests` | `IsRedirectUriAllowed` — RFC 8252 loopback any-port acceptance, https-loopback rejection, allowlist matching with subdomain/path-segment spoof guards, validation normalisation. |
| `OAuthProxyEndpointsTests` | Integration test (via `WebApplicationFactory<Program>`) for `/oauth/authorize` and `/oauth/register`: rejects disallowed `redirect_uri`, accepts loopback + allowlisted hosted callbacks, filters partially-disallowed registration requests. |
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

- **xUnit** — test framework
- **Moq** — `HttpClient` mocking (`Mock<HttpMessageHandler>` + `Protected()`)
- **FluentAssertions** — readable assertions
- **Microsoft.AspNetCore.Mvc.Testing** — in-process integration host for `OAuthProxyEndpointsTests` (uses `WebApplicationFactory<Program>`)
- **coverlet.collector** — code coverage
- **Microsoft.NET.Test.Sdk**

Targets `net10.0` to match the main project. See `VitallyMcp.Tests.csproj`
for the current version pinning — Dependabot keeps these up to date.

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

### Building a service under test

```csharp
var client = TestHelpers.CreateMockHttpClient(jsonResponse);
var service = TestHelpers.BuildVitallyService(client);
```

`BuildVitallyService` wires a `VitallyApiKeyProvider` that returns a fixed
test API key — no Key Vault required.

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

`TestHelpers.cs` exposes per-resource sample payloads (`GetSampleAccountJson`,
`GetSampleMeetingJson`, etc.). Add a new helper there when you need a shape
that doesn't fit an existing one; don't refactor existing helpers.

### Integration tests against the real composition root

`OAuthProxyEndpointsTests.Factory` shows the pattern — derive from
`WebApplicationFactory<Program>`, override `CreateHost` with in-memory
configuration (`OAuth:NoAuth=true`, dummy `DevelopmentApiKey`, fake
`Authority`/`Audience`/`SharedClientId`), and exercise the endpoint via
the returned `HttpClient`.

## Adding tests

When you add a new tool method (or a new tool class):

1. Add the matching test method in `Tools/{ResourceName}ToolsTests.cs`.
   One test per public `[McpServerTool]` method is the baseline.
2. For raw-passthrough methods (no field filtering —
   `GetRawAsync` / `PostRawAsync` / `DeleteRawAsync` based), mock the
   exact response shape Vitally returns and assert the raw body comes
   back through unchanged.
3. If the response shape is new, add a sample JSON helper to
   `TestHelpers.cs`.
4. If the tool exercises new behaviour at the service layer, add a
   corresponding `VitallyServiceTests` case.

## CI

The suite has no external dependencies and finishes in under a second, so
it runs in `.github/workflows/ci.yml` after `dotnet build`.

## Out of scope (intentional)

- Live Vitally API integration — covered by manual smoke tests as
  described in `../CLAUDE.md`.
- MCP protocol-level integration — relies on the `ModelContextProtocol`
  SDK's own test coverage.
- End-to-end tests against Claude Desktop / Claude Code — covered by the
  manual install workflow.
