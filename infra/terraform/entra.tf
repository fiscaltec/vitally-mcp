# Entra app registration for the Vitally MCP server (#107) — the OAuth client *and* the API
# resource in one registration, replacing the Auth0 client + Resource Server pair at the #108
# cutover.
#
# AS-BUILT CAPTURE, like the rest of this directory: the objects below were created with `az` /
# Microsoft Graph on 2026-09-02 and are recorded here so they are reviewable and reproducible.
# Adopt them via the import blocks in `imports.tf` — never `apply` this file blind, or Terraform
# will create a *second* registration and the identifier URI collision will fail the apply
# halfway through.
#
# This file introduces the `azuread` provider, which nothing else here needed. Adopting it requires
# `terraform init` again to pull the plugin.
#
# ---------------------------------------------------------------------------------------------
# TWO ORDERING CONSTRAINTS, both hit for real while provisioning. Terraform hides the first and
# NOT the second, so read this before editing:
#
#  1. `api.requestedAccessTokenVersion = 2` must be set BEFORE `identifier_uris`. The tenant's
#     `defaultAppManagementPolicy` enables
#     `identifierUris.uriAdditionWithoutUniqueTenantIdentifier`, which would force the
#     `https://fiscaltec.com/{guid}` shape; we are exempt only via `excludeAppsReceivingV2Tokens`.
#     The azuread provider sets both in one create call with the version first, so this works —
#     but a manual recreate must do it in two steps.
#
#  2. A scope cannot be referenced by `pre_authorized_application` in the same write that creates
#     it: Graph rejects the whole request with
#     `InvalidValue … has a Permission Id that cannot be found in the AppPermissions sets`.
#     Terraform models this correctly *because* `azuread_application_pre_authorized` is a separate
#     resource — keep it separate rather than folding it into an `api {}` block.
# ---------------------------------------------------------------------------------------------

data "azuread_client_config" "current" {}

# Microsoft Graph, for the delegated OIDC scopes below.
data "azuread_service_principal" "msgraph" {
  client_id = "00000003-0000-0000-c000-000000000000"
}

# The seven department groups that gate sign-in (Gate 1). Resolved by object id rather than by
# display name: these ids were read off the `FISCAL IT Auth0` app's own assignments, so they are
# provably the same groups that gate sign-in today and not merely same-named ones.
variable "entra_gate1_group_object_ids" {
  type        = map(string)
  description = "Department groups assigned directly to the Vitally MCP app for the sign-in gate."
  default = {
    "Product Department"                     = "012658dd-392f-4d84-af07-f97937f3a23e"
    "IT & Security Department"               = "6ba9bf61-e959-4dd9-8a96-d477a35b0d03"
    "Project Management Department"          = "8f674635-754c-4543-8d9e-ef2afbbf0d90"
    "Customer Operations Department"         = "f573c6de-ddae-407c-889d-ae9eb172e7f0"
    "Executive Leadership Team Department"   = "6e6e48b2-8425-4f96-815d-3e739de648d4"
    "Customer Account Management Department" = "552d8358-2202-42a4-bf8f-afe0468492bb"
    "Service Delivery Department"            = "e9605881-f400-44a3-9ee0-f5df5ea037f8"
  }
}

variable "entra_mcp_access_scope_id" {
  type        = string
  description = "Stable id of the exposed mcp.access delegated scope. Changing it invalidates every consent grant referencing it."
  default     = "fbdb4f49-d2f6-43b3-91a6-475117ab874b"
}

resource "azuread_application" "vitally_mcp" {
  display_name     = "Vitally MCP"
  sign_in_audience = "AzureADMyOrg"
  owners           = [data.azuread_client_config.current.object_id]

  notes = "OAuth client + API resource for the Vitally MCP server (github.com/fiscaltec/vitally-mcp). Replaces the Auth0 client + Resource Server pair. See issue #107."

  # No trailing slash — Entra refuses to register one (`IdentifierUrisEndsWithSlash` /
  # `ValueCannotEndWithSlash`). Clients still send the trailing-slash form as their RFC 8707
  # `resource`; OAuthOptions.IsResourceIndicatorAllowed tolerates exactly one slash of difference
  # so the two forms name one resource.
  identifier_uris = ["https://vitally.fiscaltec.com"]

  api {
    # See ordering constraint 1 in the header.
    requested_access_token_version = 2

    oauth2_permission_scope {
      id                         = var.entra_mcp_access_scope_id
      value                      = "mcp.access"
      type                       = "User"
      enabled                    = true
      admin_consent_display_name = "Access the Vitally MCP server"
      admin_consent_description  = "Allows the signed-in user to call the Vitally MCP server on their behalf. Tool-level entitlement is resolved separately from sg-vitally-* group membership."
      user_consent_display_name  = "Access Vitally MCP"
      user_consent_description   = "Allows you to use the Vitally MCP server on your behalf."
    }
  }

  # The proxy owns a single fixed callback per origin; MCP clients never register with Entra —
  # they converge on this app through the RFC 7591 DCR shim at /oauth/register.
  web {
    redirect_uris = [
      "https://vitally.fiscaltec.com/oauth/callback",
      "https://vitally-staging.fiscaltec.com/oauth/callback",
    ]

    implicit_grant {
      access_token_issuance_enabled = false
      id_token_issuance_enabled     = false
    }
  }

  # Delegated only. The server never calls Graph as the user — entitlement comes from the Container
  # App's managed identity holding GroupMember.Read.All, which is in identity.tf and unrelated to
  # this registration.
  required_resource_access {
    resource_app_id = data.azuread_service_principal.msgraph.client_id

    dynamic "resource_access" {
      # Ids resolved from this tenant's Graph service principal rather than copied from
      # documentation: `email` is 64a6cdd6-…-3cc8405e90d0 here, which is *not* the value that
      # commonly circulates for it.
      for_each = toset([
        "37f7f235-527c-4136-accd-4a02d197296e", # openid
        "14dad69e-099b-42c9-810b-d002981feec1", # profile
        "64a6cdd6-aab1-4aaf-94b8-3cc8405e90d0", # email
        "7427e0e9-2fba-42fe-b0c0-848c9e6a8182", # offline_access
      ])
      content {
        id   = resource_access.value
        type = "Scope"
      }
    }
  }

  # The app requests its own API's scope: that request is what binds the access token's audience
  # to this resource once #108 stops relaying the RFC 8707 `resource` parameter.
  required_resource_access {
    resource_app_id = azuread_application.vitally_mcp.client_id

    resource_access {
      id   = var.entra_mcp_access_scope_id
      type = "Scope"
    }
  }
}

# Pre-authorising the app for its own scope is what suppresses the consent screen — the equivalent
# of Auth0's `skip_consent_for_verifiable_first_party_clients`, which the Vitally MCP Resource
# Server sets today. Separate resource by necessity: see ordering constraint 2 in the header.
resource "azuread_application_pre_authorized" "self" {
  application_id       = azuread_application.vitally_mcp.id
  authorized_client_id = azuread_application.vitally_mcp.client_id
  permission_ids       = [var.entra_mcp_access_scope_id]
}

resource "azuread_service_principal" "vitally_mcp" {
  client_id = azuread_application.vitally_mcp.client_id
  owners    = [data.azuread_client_config.current.object_id]

  # Gate 1 — sign-in is restricted to principals assigned below, mirroring what
  # `FISCAL IT Auth0` enforces today.
  app_role_assignment_required = true
}

# Gate 1 assignments. **These must be direct.** The Entra app-assignment gate honours only direct
# members of an assigned group — nesting does not grant sign-in. So the department groups are
# assigned here individually; assigning the `sg-vitally-*` tier groups instead would admit only
# their two direct members. That is the trap this repo has already hit once, which is why the
# department groups are both assigned here AND nested inside `sg-vitally-*` for Gate 2.
#
# Gate 2 (which tier of tools a signed-in user gets) is a different mechanism entirely:
# GraphGroupPermissionResolver reads `sg-vitally-*` membership *transitively* via Graph, using only
# the `oid` claim, so it is independent of this registration and of the IdP.
resource "azuread_app_role_assignment" "gate1" {
  for_each = var.entra_gate1_group_object_ids

  principal_object_id = each.value
  resource_object_id  = azuread_service_principal.vitally_mcp.object_id

  # The all-zero id is the implicit "default access" role, which is what an app with no declared
  # app roles assigns. This registration deliberately declares none: entitlement is resolved from
  # Graph group membership, not from roles or a groups claim.
  app_role_id = "00000000-0000-0000-0000-000000000000"
}

# ---------------------------------------------------------------------------------------------
# NOT captured here, deliberately:
#
#  * The client secret. It is created out-of-band and stored in Key Vault
#    (`entra-mcp-client-secret`) — see docs/runbooks/entra-app-registration.md. Modelling it as
#    `azuread_application_password` would persist the value in Terraform state, which this
#    directory's README explicitly warns against, and the vault's data plane is private-endpoint
#    only so Terraform cannot write it from outside the VNet anyway.
#
#  * The tenant-wide admin consent grant (`openid profile email offline_access mcp.access`).
#    `azuread_service_principal_delegated_permission_grant` could express it, but the grant was
#    made with `az ad app permission admin-consent` and adopting it adds a resource whose drift
#    detection is noisy for no operational gain. The runbook records how to re-grant it.
# ---------------------------------------------------------------------------------------------

output "entra_app_client_id" {
  description = "appId of the Vitally MCP Entra app — becomes OAuth:SharedClientId at the #108 cutover."
  value       = azuread_application.vitally_mcp.client_id
}

output "entra_app_id_uri" {
  description = "App ID URI — becomes OAuth:Audience at cutover. Note the ABSENCE of a trailing slash, unlike OAuth:Resource."
  value       = one(azuread_application.vitally_mcp.identifier_uris)
}
