# Changelog

All notable changes to this project are documented here. Format based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **Remote HTTP MCP server** using the streamable HTTP transport (MCP
  2025-06-18, stateless mode) on the `ModelContextProtocol.AspNetCore`
  package. Replaces the previous stdio transport.
- **Auth0 OAuth 2.1 protection** on the `/mcp` endpoint via JwtBearer.
  Publishes a `/.well-known/oauth-protected-resource` metadata document
  (RFC 9728) so MCP clients discover the authorisation server automatically.
- **Per-request Vitally API key resolution** via the new
  `VitallyApiKeyProvider`: reads the `Vitally:SecretRefClaim` from the
  authenticated user's access token, fetches the named secret from Azure
  Key Vault (with a 5-minute in-memory cache), and falls back to the
  configured `DefaultSecretRef` when the claim is absent.
- **Configurable Auth0 + Key Vault settings** under the `Vitally:` and
  `Auth0:` configuration sections. See `appsettings.Example.json`.
- **`Auth0:NoAuth` development flag** that skips JWT validation for local
  smoke tests; logs a loud warning at startup.

### Changed

- **BREAKING — Distribution model.** No more `.mcpb` bundles, no more
  standalone `.exe`. The server is now a single hosted instance that
  multiple users connect to by URL. Existing v3.x binaries continue to
  work against Vitally but receive no further updates.
- **BREAKING — Configuration shape.** `VITALLY_API_KEY`, `VITALLY_REGION`,
  and `VITALLY_SUBDOMAIN` environment variables are replaced by the
  `Vitally:` and `Auth0:` configuration sections (or the
  `Vitally__*` / `Auth0__*` env-var equivalents in ASP.NET Core style).
  Local self-hosters set `Vitally:DevelopmentApiKey` instead of
  `VITALLY_API_KEY`.
- `VitallyMcp.csproj` switched from `Microsoft.NET.Sdk` to
  `Microsoft.NET.Sdk.Web`. Drops `PublishSingleFile`, `SelfContained`, and
  the `win-x64;win-arm64` RuntimeIdentifiers — output is now framework-
  dependent and runs in any .NET 10 container image.

### Removed

- The `Check_for_updates` MCP tool and the `UpdateCheckService` it sat on.
  Hosted deployments don't need it — the server is the source of truth.
- The `Output/mcpb/` folder, `manifest.json`, and `Scripts/build-*.ps1`
  build pipeline. Container build pipeline lands in Phase 3.
- `VitallyConfig` — replaced by `VitallyServerOptions` (singleton,
  server-wide) plus `VitallyApiKeyProvider` (scoped, per-request).

## [3.0.1] - 2026-05-13

### Changed

- Friendlier and uniform wording for the three MCPB `user_config`
  fields shown by Claude Desktop during install (`VITALLY_API_KEY`,
  `VITALLY_REGION`, `VITALLY_SUBDOMAIN`). All three descriptions are
  now complete sentences, consistently punctuated, with concrete
  worked examples in plain English instead of references to internal
  hostnames. Region now appears before Subdomain in the form since
  the Subdomain hint refers to Region.

## [3.0.0] - 2026-05-12

### Added

- **Full CRUD coverage** for all 93 endpoints across 17 Vitally resource
  types. New resources beyond the prior 8 read-only ones: meetings (with
  participants and transcripts), custom traits, custom surveys, custom
  objects (and instances), project templates and categories, NPS responses,
  task and note categories, messages.
- **EU data centre support** via the optional `VITALLY_REGION` env var
  (defaults to `EU` against `rest.vitally-eu.io`). Set `US` to opt into the
  per-tenant `{subdomain}.rest.vitally.io` host.
- **Rate-limit-aware HTTP pipeline** - auto-retries on `429 Too Many
  Requests` honouring `Retry-After` and `X-RateLimit-Reset`, and logs a
  warning when remaining requests drop below threshold.
- **`Check_for_updates` MCP tool** - reports the latest GitHub Release plus
  architecture-matched download URLs for `.mcpb` and `.exe`.
- **Dual-client distribution** - GitHub Actions release workflow now
  produces both Claude Desktop (`.mcpb`) and Claude Code (`.exe`) artefacts
  for `win-x64` and `win-arm64`, plus `SHA256SUMS.txt`.
- **CI workflow** running on every push and PR (build + 204 tests + coverage).
- **CodeQL** SAST workflow scanning C# weekly and on every PR.
- **Dependabot** for NuGet and GitHub Actions updates.
- **Conventional-commits PR-title validation**.
- **204 unit tests** (xUnit + FluentAssertions + Moq) covering every tool
  class, JSON filtering, rate-limit retry, update checker, and region/auth
  header behaviour.
- Repository hygiene: `.gitattributes` (LF enforcement), `.editorconfig`
  (C# Microsoft conventions), comprehensive `.gitignore`,
  `.claude/settings.json` shared team permissions plus a hook that blocks
  direct pushes to `main`, PR/issue templates.

### Changed

- **Default region is now `EU`.** Existing US installs that did not set
  `VITALLY_REGION` explicitly must now add `VITALLY_REGION=US` to keep
  hitting the US API.
- **`VITALLY_SUBDOMAIN` is now conditional** - required when
  `VITALLY_REGION=US`, ignored on EU.
- Upgraded `ModelContextProtocol` from `0.4.0-preview.3` (preview) to
  `1.3.0` (GA). No source changes needed - SDK API surface stable on our
  stdio + attribute-based usage.
- Upgraded `Microsoft.Extensions.Hosting` / `Microsoft.Extensions.Http`
  `10.0.0` -> `10.0.7`.
- Upgraded test dependencies: `Microsoft.NET.Test.Sdk` `17.14.1` -> `18.5.1`,
  `xunit.runner.visualstudio` `3.1.4` -> `3.1.5`, `FluentAssertions` `8.8.0`
  -> `8.9.0`, `coverlet.collector` `6.0.4` -> `10.0.0`.
- Tool names migrated to snake_case (`List_account`, `Get_meeting`, ...).

### Fixed

- `admins/search` resource path was missing from `ResourceDefaultFields`,
  causing `Search_admins` responses to be stripped to `[id, createdAt,
  updatedAt]` instead of `[id, name, email]`. Added explicit entry.

### Removed

- Previous TypeScript implementation (`src/index.ts`, `package.json`,
  `package-lock.json`, `tsconfig.json`, `test.js`) - replaced by the C#
  rewrite.
- Stale `.github/workflows/docker-image.yml` - the Linux container build
  for the pre-rewrite implementation. No `Dockerfile` exists for the C#
  build (Windows-native single-file `.exe`).

## [1.1.0] - 2025-05-17

Pre-rewrite (TypeScript) - see GitHub Releases for full notes:
<https://github.com/fiscaltec/vitally-mcp/releases/tag/v1.1.0>.

## [1.0.1] - 2025-05-16

## [1.0.0] - 2025-05-16

First containerised release of the pre-rewrite TypeScript implementation.

[Unreleased]: https://github.com/fiscaltec/vitally-mcp/compare/v3.0.1...HEAD
[3.0.1]: https://github.com/fiscaltec/vitally-mcp/compare/v3.0.0...v3.0.1
[3.0.0]: https://github.com/fiscaltec/vitally-mcp/compare/v1.1.0...v3.0.0
[1.1.0]: https://github.com/fiscaltec/vitally-mcp/compare/v1.0.1...v1.1.0
[1.0.1]: https://github.com/fiscaltec/vitally-mcp/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/fiscaltec/vitally-mcp/releases/tag/v1.0.0
