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

# ---- PRODUCTION OAuth inputs: the LIVE values, which are still Auth0 ----
#
# This directory is an as-built capture, so these record what production is configured with *now* —
# not what #108 moves it to. Changing them to the Entra values is part of applying the flip, not
# preparation for it: a capture that runs ahead of reality is how a plan comes to propose a change
# nobody asked for, and how a reader concludes the cutover already happened. The Entra target values
# live in CLAUDE.md, under "The Auth0 → Entra cutover (#108) and its rollback".
#
# Staging has its own `staging_oauth_*` variables below and is already on Entra.
variable "oauth_authority" {
  type        = string
  description = "Upstream OIDC issuer for PRODUCTION — currently Auth0. Endpoints are read from the issuer's discovery document, not built from this."
  default     = "https://fiscal-it.uk.auth0.com/"
}

# oauth_audience and oauth_resource are NOT the same value and must not be reconciled — though note
# the reason differs by provider. Both targets are on Entra; the Auth0 row is the rollback posture.
#
#   Auth0 (rollback only): the Resource Server identifier carries a trailing slash, so Audience
#     and Resource happen to look identical. That coincidence is what made them one variable
#     originally, and is why they are two now.
#   Entra (both targets, live): Audience follows the App ID URI, which cannot
#     carry a trailing slash — Entra refuses to register one on identifierUris — while Resource keeps
#     it. The no-slash rule is Entra's, not a general one; do not "correct" the live Auth0 value.
#
# Resource is published in the RFC 9728 document and keeps the slash either way, because that is the
# form Claude Code normalises to and then compares.
# OAuthOptions.IsResourceIndicatorAllowed tolerates exactly one slash of difference, which is what
# lets the two forms name one resource.
variable "oauth_audience" {
  type        = string
  description = "Identifier validated against the JWT aud claim for PRODUCTION — currently the Auth0 Resource Server identifier, WITH the trailing slash. Becomes the slash-less Entra App ID URI at the flip."
  default     = "https://vitally.fiscaltec.com/"
}

variable "oauth_resource" {
  type        = string
  description = "Canonical resource identifier published in RFC 9728 and validated against as an RFC 8707 indicator. WITH the trailing slash."
  default     = "https://vitally.fiscaltec.com/"
}

variable "oauth_upstream_resource_scope" {
  type        = string
  description = "PRODUCTION only. Setting it terminates the RFC 8707 `resource` parameter at the proxy instead of relaying it — required under Entra, whose v2 authorize endpoint refuses any `resource` that does not match the requested scopes (AADSTS9010010), whatever its spelling. Empty while production is on Auth0, where the relayed parameter is what binds the audience."
  default     = ""
}

variable "oauth_shared_client_id" {
  type        = string
  description = "Shared OAuth client_id for PRODUCTION — currently the Auth0 native app. Becomes the Entra app registration appId at the flip."
  default     = "VgB00WSYN2V0KkhtYx3WZXYH9XRBvK1D"
}

variable "public_base_url" {
  type    = string
  default = "https://vitally.fiscaltec.com"
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
# its `vitally-shared` secret, managed identity, ACR, Container Apps Environment and the
# `sg-vitally-*` tier group ids — is deliberately the same, so a staging failure points at what
# changed rather than at the environment.
#
# The identity provider IS shared again: both targets point at the Entra app registration since the
# 2026-09-16 production flip. The `staging_*` client id, upstream scope and client secret variables
# below now hold the same values as their `oauth_*` counterparts and are ready to be collapsed — see
# the simplification list in #102.
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

variable "staging_oauth_authority" {
  type        = string
  description = "Upstream OIDC issuer for staging. Moved to Entra first at #108, ahead of production."
  default     = "https://login.microsoftonline.com/75bd6050-92a8-4bde-a406-50000b310c86/v2.0"
}

# Staging's Audience and Resource diverge by HOST as well as by slash, and that is expected. One
# Entra app registration serves both origins (#107), so a staging token's `aud` is production's App
# ID URI — whereas `resource` must equal the staging origin, because MCP clients reject a metadata
# document whose `resource` does not match the server they fetched it from.
variable "staging_oauth_audience" {
  type        = string
  description = "Entra App ID URI validated against a staging token's aud. Production's URI, because one registration serves both origins. NO trailing slash."
  default     = "https://vitally.fiscaltec.com"
}

variable "staging_oauth_resource" {
  type        = string
  description = "Canonical resource identifier published by staging, WITH the trailing slash. Must equal the staging origin."
  default     = "https://vitally-staging.fiscaltec.com/"
}

# These are STAGING's OAuth client, and they are NOT the production values above: staging flipped to
# the Entra app registration on 2026-09-03 while production is still on the Auth0 client. That is the
# current split, not a future one — do not feed `oauth_shared_client_id` / `oauth_shared_client_secret`
# to the staging app, which would point staging back at Auth0 while its authority says Entra.
# These variables and the production ones reunify only once production flips too.
variable "staging_oauth_shared_client_id" {
  type        = string
  description = "Shared OAuth client_id for STAGING — the Entra app registration appId (#107), since staging flipped on 2026-09-03."
  default     = "c3812e7d-a413-4169-b57e-803326611ba3"
}

variable "staging_oauth_upstream_resource_scope" {
  type        = string
  description = "STAGING only. Set, because staging is on Entra: the proxy terminates the RFC 8707 `resource` parameter and names the API by this scope instead."
  default     = "https://vitally.fiscaltec.com/mcp.access"
}

variable "staging_public_base_url" {
  type        = string
  description = "Canonical public origin for staging — no trailing slash (it is an origin, and Validate() trims one anyway)."
  default     = "https://vitally-staging.fiscaltec.com"
}

# ---- Secrets (DO NOT hardcode/commit — supply via TF_VAR_* or an untracked tfvars) ----
variable "oauth_shared_client_secret" {
  type        = string
  description = "Client secret for PRODUCTION's `oauth_shared_client_id` (Container App secret 'oauth-shared-client-secret') — currently the Auth0 client's. Becomes the Entra one at the flip."
  sensitive   = true
}

variable "staging_oauth_shared_client_secret" {
  type        = string
  description = "Client secret for STAGING's `staging_oauth_shared_client_id` — the Entra app's, sourced from the Key Vault secret `entra-mcp-client-secret`. Expires 2027-03-01; see docs/runbooks/entra-app-registration.md."
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
