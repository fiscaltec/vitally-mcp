# VitallyMcp.Tests

Automated test suite for the Vitally MCP server.

## Coverage

**400 tests, all passing** (xUnit + FluentAssertions + Moq + ASP.NET Core
test host), running fully in-process — no live API calls, no real Auth0
tenant, no Key Vault.

> **Integration tests that set environment variables must join
> `IntegrationTestCollection`.** `Program.cs` reads `OAuth:NoAuth` and
> `Authorization:ReadOnly` at composition time — before `WebApplicationFactory`
> can inject configuration — so environment variables are the only override
> that works, and they are process-wide. xUnit runs test classes in parallel by
> default, so without the shared collection two fixtures setting
> `OAuth__NoAuth` differently will race. Set every variable your fixture
> depends on explicitly rather than relying on a default, so a value left
> behind by a sibling is overwritten deterministically.

### Test classes

| File | Scope |
|------|-------|
| `VitallyApiKeyProviderTests` | Dev-fallback resolution: no `SecretClient` registered → returns `DevelopmentApiKey`; neither set → throws. |
| `VitallyServiceTests` | Field/trait filtering, pagination, resource-specific defaults across every resource type, plus full coverage of `GetResourcesAsync`, `GetResourceByIdAsync`, `CreateResourceAsync`, `UpdateResourceAsync`, `DeleteResourceAsync`, `GetRawAsync` (with URL-encoded query params), `PostRawAsync`, `DeleteRawAsync`. Includes HTTP-verb, path, and Basic-auth header verification via Moq's `Protected().Verify(...)`. Also asserts that the response body is surfaced in `HttpRequestException` on non-2xx responses (regression guard). |
| `VitallyRateLimitHandlerTests` | 429 retry behaviour, `Retry-After`/`X-RateLimit-Reset` header parsing, low-remaining warnings. |
| `OAuthOptionsTests` | `IsRedirectUriAllowed` — RFC 8252 loopback any-port acceptance, https-loopback rejection, allowlist matching with subdomain/path-segment spoof guards, validation normalisation. |
| `OAuthProxyEndpointsTests` | Integration test (via `WebApplicationFactory<Program>`) for the proxy endpoints: rejects disallowed `redirect_uri`, accepts loopback + allowlisted hosted callbacks, filters partially-disallowed registration requests, refuses unsupported grants. Also pins the RFC 8414 façade — `issuer` equals the serving origin *and* the `authorization_servers` value in the protected-resource document (asserted against each other, so the two documents cannot drift apart while both still look right), `authorization_response_iss_parameter_supported` is advertised, and `/oauth/callback` emits exactly one `iss` naming us, replacing any upstream value. |
| `UpstreamOidcMetadataTests` | The resolver that replaced the Auth0-shaped URL concatenation: all four endpoints read from a stubbed discovery document, the standard well-known path constructed beneath the issuer, cache reuse (including across resolver instances), a failed resolve not being cached, last-known-good served when a *refresh* fails, and rejection of a document that is malformed, not an object, missing any of the four, or gives one as a relative/plaintext URL. The stub's `issuer` matches the configured `Authority` (the resolver checks the two per OIDC Discovery §4.3) while its endpoints are deliberately unrelated to it — different hosts, `userinfo_endpoint` on a third — so no assertion can pass by concatenation. Also covers the issuer check itself: a document naming another issuer is refused, a one-character trailing-slash difference is not. |
| `OAuthTokenProxyForwardTests` | Where `/oauth/token` actually forwards to. Stubs the *default* `HttpClient` (`Options.DefaultName` — the one `factory.CreateClient()` resolves) and asserts the posted URI is the discovered `token_endpoint` and that the client's loopback `redirect_uri` was replaced with our own callback. Exists because the sibling proxy tests only reach the unsupported-grant guard, which returns before any upstream call — so reverting the forward to `{authority}/oauth/token` would otherwise leave the suite green. |
| `UpstreamOidcStartupFailFastTests` | That the fail-fast is actually *wired into* Program.cs — the easy half to lose, since the guard sits between `builder.Build()` and `app.Run()`. Drives the real composition root: the host refuses to start when discovery is unreachable or incomplete, and starts fine without discovery when `OAuth:SharedClientId` is unset. |
| `OAuthProxyPublicOriginTests` | The production shape `OAuthProxyEndpointsTests` cannot reach: with `OAuth:PublicBaseUrl` set, the published identity must be that configured origin and not the request `Host`. Its sibling's assertions would pass even on a regression to Host-derived values, because there the two coincide. |
| `Tools/AccountsToolsTests` | List / get / create / update / delete + status filter + traits + list-by-organisation |
| `Tools/SummaryToolsTests` | `Get_organization_summary` — the read-only composite (org get-by-id with curated rollup traits, object-name resolution, two organisation-scoped instance searches) and its per-sub-call error isolation |
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
| `ToolAnnotationCoverageTests` | Reflection sweep over every `[McpServerTool]`: all four annotation hints explicitly set, values matching the name-prefix rules, and a prefix-independent check that `ReadOnly == true` implies `Destructive == false` (and vice versa). Uses `CustomAttributeData.NamedArguments` rather than the attribute instance, because the four properties are non-nullable `bool` — a defaulted value is otherwise indistinguishable from an explicit one. |
| `ToolAuthorizePolicyCoverageTests` | Every tool carries exactly one `[Authorize]` whose policy matches its annotations, plus the exact 56 / 25 / 12 read / write / delete distribution. Pairs with the count assertion so neither can pass vacuously. |
| `AuthorizationFilterToolsListTests` | The load-bearing authorisation suite. Per-tier `tools/list` filtering (exact 56 / 81 / 93 partition, subset relations, and the two off-prefix tools by name), the no-permissions caller seeing nothing, NoAuth dev mode seeing all 93 unfiltered, a reader's write call being refused, and exactly one audit-deny record per refused call. Also pins that whitespace in a configured permission cannot desync discovery from enforcement. |
| `VitallyPermissionHandlerTests` | The ASP.NET Core authorisation handler: succeeds when the caller holds the permission, does not when they lack it, and short-circuits to success when authorisation is bypassed (RBAC off or `NoAuth`) so local dev is never filtered to an empty list. |
| `ResourceMetadataDiscoveryTests` | The 401 challenge carries exactly **one** `WWW-Authenticate` value with a `resource_metadata` pointer, adds `error="invalid_token"` when a token was presented, and keeps the status at exactly 401 — which `.github/workflows/deploy.yml` smoke-tests. Both well-known metadata paths serve, asserted with exact collection counts rather than `Contain`, since `ProtectedResourceMetadata` ships `BearerMethodsSupported` pre-populated and an append would silently duplicate it. Also asserts no property is serialised as `null`: RFC 9728 §3.2 requires an unused parameter to be omitted, and a strict client rejects the whole document over the difference — invisible to a test that only reads the properties it expects to find. |
| `ToolsListCachingTests` | Asserts the serialised wire form of the cache hints — `ttlMs` as integer milliseconds and `cacheScope` as `"private"` — deliberately on the raw JSON rather than the SDK's CLR properties, so an SDK rename is caught here rather than by clients. |
| `IntegrationTestCollection` | Not a test — the xUnit collection definition serialising every class that mutates process-wide environment variables. See the note above; membership is mandatory for such classes. |
| `StubOidcDiscovery` | Not a test — the canned upstream discovery document plus the stub/failing/fail-on-refresh handlers, and the `UseStubDiscovery()` extension every integration factory with `OAuth:SharedClientId` set now needs (startup resolves discovery and refuses to boot without it). |
| `CapturingLogger` / `CapturingLoggerProvider` | Test helper capturing `ILogger` output so audit assertions can be made against what `AuditLogger` actually recorded. |

## Framework & dependencies

- **xUnit** — test framework
- **Moq** — `HttpClient` mocking (`Mock<HttpMessageHandler>` + `Protected()`)
- **FluentAssertions** — readable assertions
- **Microsoft.AspNetCore.Mvc.Testing** — in-process integration host for `OAuthProxyEndpointsTests`, `OAuthProxyPublicOriginTests` and the other integration classes (uses `WebApplicationFactory<Program>`)
- **Microsoft.Testing.Extensions.CodeCoverage** — code coverage (`--coverage`)
- **Microsoft.Testing.Extensions.TrxReport** — TRX output for the CI test summary (`--report-trx`)

Targets `net10.0` to match the main project. See `VitallyMcp.Tests.csproj`
for the current version pinning — Dependabot keeps these up to date.

## Running

```powershell
# Full suite (from repo root)
dotnet test VitallyMcp.sln -c Debug

# Just one class
dotnet test VitallyMcp.sln -c Debug --filter-class "*MeetingsToolsTests"

# Just one method
dotnet test VitallyMcp.sln -c Debug --filter-method "*AddMeetingParticipant*"

# With coverage (cobertura, as CI collects it)
dotnet test VitallyMcp.sln -c Debug --coverage --coverage-output-format cobertura --results-directory TestResults
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

If the factory sets `OAuth:SharedClientId`, it **must** also call
`services.UseStubDiscovery()` from `builder.ConfigureServices(...)`. Startup
resolves the upstream OIDC discovery document and refuses to boot without it,
so a factory that omits the stub will try to reach the fake `Authority` host
and fail during `CreateClient()`.

Note that configuration injected this way is **not** visible to the raw
`builder.Configuration[...]` reads at the top of `Program.cs` — it arrives
later. Anything a test needs to influence has to be read from the resolved
`IOptions<T>` after `builder.Build()` (as the discovery guard does) or, failing
that, set via an environment variable and the serialised
`IntegrationTestCollection`.

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
