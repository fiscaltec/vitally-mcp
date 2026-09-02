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

variable "oauth_authority" {
  type        = string
  description = "Upstream OIDC issuer. Entra since the #108 cutover; the endpoints are read from its discovery document, not built from this."
  default     = "https://login.microsoftonline.com/75bd6050-92a8-4bde-a406-50000b310c86/v2.0"
}

# oauth_audience and oauth_resource are NOT the same value and must not be reconciled. Audience is
# validated against the token `aud` and follows the Entra App ID URI, which cannot carry a trailing
# slash (Entra refuses to register one on identifierUris). Resource is published in the RFC 9728
# document and keeps the slash, because that is the form Claude Code normalises to and then compares.
# OAuthOptions.IsResourceIndicatorAllowed tolerates exactly one slash of difference, which is what
# lets the two forms name one resource.
variable "oauth_audience" {
  type        = string
  description = "Entra App ID URI, validated against the JWT aud claim. NO trailing slash."
  default     = "https://vitally.fiscaltec.com"
}

variable "oauth_resource" {
  type        = string
  description = "Canonical resource identifier published in RFC 9728 and validated against as an RFC 8707 indicator. WITH the trailing slash."
  default     = "https://vitally.fiscaltec.com/"
}

variable "oauth_upstream_resource_scope" {
  type        = string
  description = "Scope naming this server's API upstream. Setting it terminates the RFC 8707 `resource` parameter at the proxy instead of relaying it — required under Entra, which rejects the trailing-slash form clients send (AADSTS9010010). Empty on Auth0."
  default     = "https://vitally.fiscaltec.com/mcp.access"
}

variable "oauth_shared_client_id" {
  type        = string
  description = "Entra app registration appId — both the OAuth client and the API resource (#107)."
  default     = "c3812e7d-a413-4169-b57e-803326611ba3"
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
# Everything the staging Container App does NOT share with production. The rest — region, vault,
# managed identity, shared Auth0 client, tier group ids — is deliberately the same, so a staging
# failure points at what changed rather than at the environment.
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

variable "staging_public_base_url" {
  type        = string
  description = "Canonical public origin for staging — no trailing slash (it is an origin, and Validate() trims one anyway)."
  default     = "https://vitally-staging.fiscaltec.com"
}

# ---- Secrets (DO NOT hardcode/commit — supply via TF_VAR_* or an untracked tfvars) ----
variable "oauth_shared_client_secret" {
  type        = string
  description = "Entra app client secret, sourced from the Key Vault secret `entra-mcp-client-secret` (Container App secret 'oauth-shared-client-secret'). Expires 2027-03-01 — see docs/runbooks/entra-app-registration.md."
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
