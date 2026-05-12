# Changelog

All notable changes to this project are documented here. Format based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/fiscaltec/vitally-mcp/compare/v3.0.0...HEAD
[3.0.0]: https://github.com/fiscaltec/vitally-mcp/compare/v1.1.0...v3.0.0
[1.1.0]: https://github.com/fiscaltec/vitally-mcp/compare/v1.0.1...v1.1.0
[1.0.1]: https://github.com/fiscaltec/vitally-mcp/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/fiscaltec/vitally-mcp/releases/tag/v1.0.0
