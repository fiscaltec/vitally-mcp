# Staging Container App — the on-demand pre-production target for identity-provider changes (#112).
#
# LIFECYCLE. Staging is spun up when it is needed and torn down when the work is done (decided
# 2026-08-28), so this resource frequently describes something that does not currently exist — that
# is the intended state, not drift to reconcile. Its import block in imports.tf therefore only
# applies while the app is live; comment it out otherwise. Everything else staging needs (the CAE,
# managed identity, ACR, Key Vault, DNS records, the Entra app's staging redirect URI, GitHub
# environment and the federated credential) is persistent scaffolding that deliberately survives a
# teardown — see the teardown table in CLAUDE.md before deleting any of it.
#
# WHY IT EXISTS. Authentication has the largest blast radius in this system, so identity-provider
# changes are validated here before production — which is what happened for the Entra migration
# (#102): staging ran it from 2026-09-03 and production followed on 2026-09-16. It remains the
# pre-production target for the next such change. The alternatives were both rejected: a local server
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

  # The SAME value as production's, under the SAME name since #156 normalised it. Both targets run
  # the one Entra app registration — staging since 2026-09-03, production since 2026-09-16 — so the
  # value, the name and the variable are now shared, and a difference between the two captures should
  # mean something rather than being a leftover.
  #
  # Handing the wrong secret to either app is not caught at startup: the app boots and /health passes.
  # It surfaces at the token exchange as an authentication error from the provider, so sign-in fails
  # for everyone while the app looks healthy. That is why a secret change is verified by signing in,
  # not by a health probe.
  secret {
    name  = "entra-oauth-client-secret"
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
      # Deliberately the production vault and the production `vitally-shared` secret. Vitally does
      # allow additional API keys per environment, but offers no read-scoped key (checked
      # 2026-09-15), so a separate staging key would carry the same write access to the same single
      # tenant — it would buy revocability, not safety. Hence staging writes reach real Vitally data,
      # and `Authorization__ReadOnly` below is what guards it. See CLAUDE.md.
      env {
        name  = "Vitally__KeyVaultUri"
        value = "https://${azurerm_key_vault.secret.name}.vault.azure.net/"
      }
      env {
        name  = "AZURE_CLIENT_ID"
        value = var.managed_identity_client_id
      }
      # Standing policy: a new identity provider goes to staging first and production follows only
      # once it has passed there. That is how the Entra move ran — staging 2026-09-03, production
      # 2026-09-16, both complete — so the five IDENTITY variables agree and were collapsed onto the
      # shared oauth_* ones by #156 (closing #102): authority, audience, upstream_resource_scope,
      # shared_client_id and the secret. During the NEXT such migration they diverge again, and the
      # way to do that is to reintroduce a staging_* variable for the one setting under test rather
      # than forking the whole set.
      #
      # NOT staging_oauth_resource and NOT staging_public_base_url — each target publishes its own
      # origin, and sharing those would make staging advertise production's, which strict RFC 9728
      # clients reject.
      env {
        name  = "OAuth__Authority"
        value = var.oauth_authority
      }
      # Note this is *production's* App ID URI — the shared variable, deliberately: one Entra
      # registration serves both origins, so a staging token's `aud` names production. Resource below
      # must still name the staging origin, so here the two diverge by host as well as by slash.
      # See variables.tf.
      env {
        name  = "OAuth__Audience"
        value = var.oauth_audience
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
        secret_name = "entra-oauth-client-secret"
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
      # Staging shares the PRODUCTION Vitally API key — there is one Vitally tenant, no sandbox, and
      # no read-scoped key available (checked 2026-09-15) — so its write and delete tools mutate real
      # customer data. This switch is the only thing preventing that.
      #
      # BUT THIS FILE DOES NOT APPLY IT. infra/terraform/ is a back-filled as-built capture and
      # `terraform apply` is never run here — staging is stood up through deploy.yml and the
      # `az containerapp` commands in CLAUDE.md. So recording it here does NOT make a recreated app
      # come up guarded: it starts on the application default, false. Set it out of band as part of
      # the spin-up and then verify it:
      #
      #   (the full form, which reports NOT ASSESSED rather than printing nothing when the
      #    lookup fails, is in docs/runbooks/read-only-and-rbac-rollout.md — an empty result
      #    from a bare loop is indistinguishable from an unguarded app)
      #   CA=vitally-staging-ca-uksouth; RG=vitally-prod-rg-uksouth
      #   REVS=$(az containerapp revision list -n $CA -g $RG \
      #     --query '[?properties.trafficWeight > `0`].name' -o tsv) || echo "NOT ASSESSED"
      #   for REV in $REVS; do az containerapp revision show -n $CA -g $RG --revision "$REV" \
      #     --query "properties.template.containers[0].env[?name=='Authorization__ReadOnly'].value|[0]" -o tsv; done
      #
      # Empty output means unguarded, not "defaulted to safe". It reads the SERVING revision
      # deliberately: `az containerapp show` returns the desired template, which reports the new
      # value the moment an update is accepted while the previous — unguarded — revision may
      # still be taking every request.
      #
      # Unset it for the tier-enforcement acceptance test, which has to see the write tools to prove
      # a reader is denied one, then put it back — under the EXIT trap in
      # docs/runbooks/entra-cutover-staging-validation.md, so an interrupted run cannot leave it off.
      env {
        name  = "Authorization__ReadOnly"
        value = "true"
      }
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
