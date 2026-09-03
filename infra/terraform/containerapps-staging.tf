# Staging Container App — the on-demand pre-production target for identity-provider changes (#112).
#
# LIFECYCLE. Staging is spun up when it is needed and torn down when the work is done (decided
# 2026-08-28), so this resource frequently describes something that does not currently exist — that
# is the intended state, not drift to reconcile. Its import block in imports.tf therefore only
# applies while the app is live; comment it out otherwise. Everything else staging needs (the CAE,
# managed identity, ACR, Key Vault, DNS records, Auth0 Resource Server, GitHub environment and the
# federated credential) is persistent scaffolding that deliberately survives a teardown — see the
# teardown table in CLAUDE.md before deleting any of it.
#
# WHY IT EXISTS. Authentication has the largest blast radius in this system, so the Entra migration
# (#102) is validated here before production. The alternatives were both rejected: a local server
# behind an ephemeral HTTPS tunnel orphans one identity-provider app registration per run (identifier
# URIs are immutable and must equal the server origin), and validating straight against production is
# the failure mode the staging-first design exists to avoid.
#
# WHY IT IS CHEAP. It shares the VNet-injected Container Apps Environment, so it reaches Key Vault and
# ACR over the existing private endpoints with no additional networking, and it shares the
# user-assigned managed identity, so the AcrPull / Key Vault Secrets User / Graph GroupMember.Read.All
# grants all apply to it already. What is actually staging-specific is this app, a stable hostname, an
# ingress certificate and one federated credential.
#
# WHY THIS IS DUPLICATED RATHER THAN for_each'd over a map with the production app. Refactoring the
# two into one resource would change `azurerm_container_app.app`'s address, invalidating the import
# block that adopts the live production app — and this configuration is documentation of the as-built
# estate that is never applied blind (see README.md). Explicit duplication is the readable, low-risk
# form here; keep the two in step by hand.
resource "azurerm_container_app" "staging" {
  name                         = var.staging_app_name
  resource_group_name          = data.azurerm_resource_group.rg.name
  container_app_environment_id = azurerm_container_app_environment.env.id
  revision_mode                = "Single"
  workload_profile_name        = "Consumption"

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.app.id]
  }

  registry {
    server   = azurerm_container_registry.acr.login_server
    identity = azurerm_user_assigned_identity.app.id
  }

  # The same Entra app registration as production, so this is the same secret value. Its redirect
  # URIs carry both origins' /oauth/callback; the proxy's callback is fixed per origin.
  secret {
    name  = "oauth-shared-client-secret"
    value = var.oauth_shared_client_secret
  }

  ingress {
    external_enabled = true
    target_port      = 8080
    transport        = "auto"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }

    # Custom domain bound to a FREE MANAGED certificate, as production is. azurerm cannot create
    # managed certs, so the binding is done out-of-band:
    #   az containerapp hostname add  -n <app> -g <rg> --hostname vitally-staging.fiscaltec.com
    #   az containerapp hostname bind -n <app> -g <rg> --hostname vitally-staging.fiscaltec.com \
    #     --environment vitally-prod-cae-uksouth --validation-method CNAME
    # Cloudflare (fiscaltec.com) carries the un-proxied CNAME to the app FQDN plus the
    # asuid.vitally-staging TXT ownership proof. The CNAME must stay DNS-only — proxying it breaks
    # both the managed-certificate issuance and the TLS chain clients see.
    # custom_domain {
    #   name                     = "vitally-staging.fiscaltec.com"
    #   certificate_binding_type = "SniEnabled"
    #   certificate_id           = "<managed cert id>"
    # }
  }

  template {
    # Unlike production, staging scales to zero. The warm replica production keeps exists to avoid a
    # server-side cold start on real user traffic; staging has no users to protect, and a cold start
    # is absorbed by the retries in deploy.yml's smoke and verify-oauth-metadata.sh.
    min_replicas = 0
    max_replicas = 2

    container {
      name   = "vitally-mcp"
      image  = "${azurerm_container_registry.acr.login_server}/vitally-mcp:${var.staging_image_tag}"
      cpu    = 0.5
      memory = "1Gi"

      env {
        name  = "Vitally__Region"
        value = "EU"
      }
      # Deliberately the production vault and the production `vitally-shared` secret: there is only
      # one Vitally tenant and its API keys are global, so there is no staging key to point at. That
      # means staging writes reach real Vitally data — see the note in CLAUDE.md.
      env {
        name  = "Vitally__KeyVaultUri"
        value = "https://${azurerm_key_vault.secret.name}.vault.azure.net/"
      }
      env {
        name  = "AZURE_CLIENT_ID"
        value = var.managed_identity_client_id
      }
      # Staging is pointed at a new identity provider first and production follows once it has
      # passed; both are on Entra now that #108 has cut over.
      env {
        name  = "OAuth__Authority"
        value = var.staging_oauth_authority
      }
      # Note this is *production's* App ID URI: one Entra registration serves both origins, so a
      # staging token's `aud` names production. Resource below must still name the staging origin,
      # so here the two diverge by host as well as by slash. See variables.tf.
      env {
        name  = "OAuth__Audience"
        value = var.staging_oauth_audience
      }
      env {
        name  = "OAuth__Resource"
        value = var.staging_oauth_resource
      }
      env {
        name  = "OAuth__UpstreamResourceScope"
        value = var.oauth_upstream_resource_scope
      }
      env {
        name  = "OAuth__NoAuth"
        value = "false"
      }
      env {
        name  = "OAuth__SharedClientId"
        value = var.oauth_shared_client_id
      }
      env {
        name        = "OAuth__SharedClientSecret"
        secret_name = "oauth-shared-client-secret"
      }
      env {
        name  = "OAuth__AllowedClientRedirectUris__0"
        value = var.allowed_client_redirect_uri
      }
      env {
        name  = "OAuth__PublicBaseUrl"
        value = var.staging_public_base_url
      }
      # Same tier groups as production. Entitlement is resolved live from Graph transitiveMembers
      # using only the `oid` claim, so it is identity-provider-independent and needs no staging
      # variant — which is also why staging can be moved to Entra without touching these.
      env {
        name  = "Authorization__LiveGroupCheck"
        value = "true"
      }
      env {
        name  = "Authorization__ReaderGroupId"
        value = var.entra_group_reader
      }
      env {
        name  = "Authorization__EditorGroupId"
        value = var.entra_group_editor
      }
      env {
        name  = "Authorization__AdminGroupId"
        value = var.entra_group_admin
      }
    }
  }
}
