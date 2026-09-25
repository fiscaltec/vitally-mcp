variable "subscription_id" {
  type        = string
  description = "Azure subscription (IT-Production)."
  default     = "282207c6-4107-47fa-9d4e-b2fa9b3066cb"
}

variable "location" {
  type    = string
  default = "uksouth"
}

variable "resource_group_name" {
  type    = string
  default = "vitally-prod-rg-uksouth"
}

variable "name_prefix" {
  type        = string
  description = "Resource naming prefix (vitally-prod-{type}-uksouth convention)."
  default     = "vitally-prod"
}

variable "image_tag" {
  type        = string
  description = "Container image tag deployed to the Container App."
  default     = "v4.0.16"
}

# ---- Identity / OAuth / Authorization (non-secret config) ----
variable "managed_identity_client_id" {
  type    = string
  default = "d93687a0-ef76-4df8-804e-d941067abdeb"
}

# ---- OAuth inputs: the LIVE values. Entra, on BOTH targets ----
#
# This directory is an as-built capture, so these record what the targets are configured with *now*.
# A capture that runs ahead of reality is how a plan comes to propose a change nobody asked for, and
# one that lags is how a reader concludes a cutover has not happened.
#
# These are SHARED by production and staging. They were duplicated as `oauth_*` / `staging_oauth_*`
# while the targets ran different identity providers during the #108 migration; both have run Entra
# since 2026-09-16 and #156 collapsed the duplicates onto these (closing #102).
#
# Only `oauth_resource` / `staging_oauth_resource` and `public_base_url` / `staging_public_base_url`
# stay per-target, permanently: each target publishes its own origin, and sharing those would make
# staging advertise production's — an RFC 9728 document naming a server it is not, which strict
# clients reject outright.
variable "oauth_authority" {
  type        = string
  description = "Upstream OIDC issuer — Entra, shared by both targets. Endpoints are read from the issuer's discovery document, not built from this."
  default     = "https://login.microsoftonline.com/75bd6050-92a8-4bde-a406-50000b310c86/v2.0"
}

# oauth_audience and oauth_resource are NOT the same value and must not be reconciled. They differ
# by exactly one trailing slash:
#
#   Audience follows the Entra App ID URI, which cannot carry a trailing slash — Entra refuses to
#     register one on identifierUris.
#   Resource is published in the RFC 9728 document and keeps the slash, because that is the form
#     Claude Code normalises to and then compares.
#
# OAuthOptions.IsResourceIndicatorAllowed tolerates exactly one slash of difference, which is what
# lets the two forms name one resource. Reconciling them breaks token validation in one direction
# and the metadata document in the other.
variable "oauth_audience" {
  type        = string
  description = "Identifier validated against the JWT aud claim — the Entra App ID URI, with NO trailing slash (Entra refuses to register one on identifierUris). Shared: one registration serves both origins, so a staging token's aud names this production URI too."
  default     = "https://vitally.fiscaltec.com"
}

variable "oauth_resource" {
  type        = string
  description = "Canonical resource identifier PRODUCTION publishes in RFC 9728 and validates against as an RFC 8707 indicator. WITH the trailing slash. Per-target — staging has its own."
  default     = "https://vitally.fiscaltec.com/"
}

variable "oauth_upstream_resource_scope" {
  type        = string
  description = "Shared by both targets. Set, so the proxy terminates the RFC 8707 `resource` parameter instead of relaying it — required under Entra, whose v2 authorize endpoint refuses any `resource` that does not match the requested scopes (AADSTS9010010), whatever its spelling. Empty is the RFC 8707 default, relaying the indicator; Entra is the outlier."
  default     = "https://vitally.fiscaltec.com/mcp.access"
}

variable "oauth_shared_client_id" {
  type        = string
  description = "Shared OAuth client_id — the Entra app registration appId (#107). One registration serves both targets."
  default     = "c3812e7d-a413-4169-b57e-803326611ba3"
}

variable "public_base_url" {
  type        = string
  description = "Canonical public origin for PRODUCTION — no trailing slash. Per-target: staging has its own."
  default     = "https://vitally.fiscaltec.com"
}

variable "allowed_client_redirect_uri" {
  type    = string
  default = "https://claude.ai/api/mcp/auth_callback"
}

variable "entra_group_reader" {
  type    = string
  default = "71451cc9-f5df-44ee-8ed1-3acc41a911eb"
}

variable "entra_group_editor" {
  type    = string
  default = "19b9d659-284c-4f93-b1c3-a6354db1027c"
}

variable "entra_group_admin" {
  type    = string
  default = "70b48a20-d4b1-47dc-a132-21bc99272a86"
}

# ---- Staging target (#112) ----
# Everything the staging Container App does NOT share with production. The rest — region, vault and
# its `vitally-shared` secret, managed identity, ACR, Container Apps Environment, the `sg-vitally-*`
# tier group ids AND the whole OAuth identity set above — is deliberately the same, so a staging
# failure points at what changed rather than at the environment.
#
# The five duplicated identity variables (authority, audience, upstream_resource_scope,
# shared_client_id and the client secret) were collapsed onto their `oauth_*` counterparts by #156,
# closing #102. They existed separately only while the targets ran different providers during the
# #108 migration.
#
# What remains below is genuinely per-target. `staging_oauth_resource` and `staging_public_base_url`
# stay separate permanently: each target publishes its own origin, so sharing those would make
# staging advertise production's — an RFC 9728 document naming a server it is not, which strict
# clients reject outright.
variable "staging_app_name" {
  type        = string
  description = "Staging Container App name. Deliberately outside the name_prefix convention: it is a second app inside the production RG and Container Apps Environment, not a second environment."
  default     = "vitally-staging-ca-uksouth"
}

variable "staging_image_tag" {
  type        = string
  description = "Container image tag deployed to the staging Container App. Moves independently of production's."
  default     = "sha-3c40e0e"
}

# Staging's Audience and Resource diverge by HOST as well as by slash, and that is expected. One
# Entra app registration serves both origins (#107), so a staging token's `aud` is production's App
# ID URI — which is why staging reads the shared `oauth_audience` above — whereas `resource` must
# equal the staging origin, because MCP clients reject a metadata document whose `resource` does not
# match the server they fetched it from.
variable "staging_oauth_resource" {
  type        = string
  description = "Canonical resource identifier published by staging, WITH the trailing slash. Must equal the staging origin."
  default     = "https://vitally-staging.fiscaltec.com/"
}

variable "staging_public_base_url" {
  type        = string
  description = "Canonical public origin for staging — no trailing slash (it is an origin, and Validate() trims one anyway)."
  default     = "https://vitally-staging.fiscaltec.com"
}

# ---- Secrets (DO NOT hardcode/commit — supply via TF_VAR_* or an untracked tfvars) ----
# ONE secret for both targets, held on each Container App under the SAME name,
# `entra-oauth-client-secret` (#156 normalised staging onto production's name and removed the
# retained rollback credential that had occupied the other one).
#
# The Container App secret is a COPY of the Key Vault secret `entra-mcp-client-secret`, not a
# reference to it — so rotating the vault copy alone changes nothing the app sends. That is #138's
# trap, and it is why this name records its provenance rather than being provider-neutral.
variable "oauth_shared_client_secret" {
  type        = string
  description = "Client secret for `oauth_shared_client_id` — the Entra app's, sourced from the Key Vault secret `entra-mcp-client-secret` and COPIED onto both Container Apps as 'entra-oauth-client-secret'. Expires 2027-03-01; see docs/runbooks/entra-app-registration.md."
  sensitive   = true
}

variable "application_insights_connection_string" {
  type        = string
  description = "Connection string for the Application Insights component `vitally-prod-appi-uksouth`, set on BOTH Container Apps (production and staging) since 2026-09-25 (#147). Read it with `az monitor app-insights component show -a vitally-prod-appi-uksouth -g vitally-prod-rg-uksouth --query connectionString -o tsv`. It is REQUIRED even though the component has DisableLocalAuth = true: that setting refuses the instrumentation key as a credential, while the string still names the component and its ingestion endpoint. Deliberately has no default — an adoption must supply the live value rather than plant a placeholder that would silently stop the export."
  sensitive   = true
}

variable "teams_webhook_url" {
  type        = string
  description = "Teams Power Automate Workflows webhook URL for the secret-expiry scanner job."
  sensitive   = true
}

# ---- Entra (entra.tf, #107) ----
variable "tenant_id" {
  type        = string
  description = "Entra tenant hosting the app registration and the sg-vitally-* / department groups."
  default     = "75bd6050-92a8-4bde-a406-50000b310c86"
}
