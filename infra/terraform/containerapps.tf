locals {
  # The scanner Job decodes this base64'd Python script and runs it (matches the deployed job).
  scanner_command = "echo ${base64encode(file("${path.module}/scan/run.py"))} | base64 -d > /tmp/run.py && python3 /tmp/run.py"
}

resource "azurerm_container_app_environment" "env" {
  name                = "${var.name_prefix}-cae-uksouth"
  resource_group_name = data.azurerm_resource_group.rg.name
  location            = var.location
  # Changed 2026-09-17 from the default "log-analytics" destination, which writes directly to the
  # workspace using its SHARED KEY. That never worked here and never could: monitoring.tf sets
  # local_authentication_enabled = false, so the workspace refuses shared-key writes — which is why
  # ContainerAppConsoleLogs_CL had zero rows for the workspace's entire lifetime.
  #
  # It is also unsupported by design. Microsoft's Container Apps log-options documentation states:
  #
  #   "Private link: Sending logs directly to a Log Analytics Workspace through Private Link isn't
  #    supported. However, you can use Azure Monitor and send your logs to the same Log Analytics
  #    Workspace. This indirection is required to prevent system log data loss."
  #
  # With "azure-monitor", the categories and destination are configured by DIAGNOSTIC SETTINGS
  # instead (diagnostics.tf) — which reach the workspace over the Azure Monitor control plane rather
  # than a shared key, so they work with ingestion private and local auth disabled.
  #
  # ⚠️ The diagnostic settings are NOT optional with this value: set "azure-monitor" without them and
  # logs go nowhere silently, which is the state this repo was in before 2026-09-17 in the other
  # direction. log_analytics_workspace_id is deliberately absent — it belongs to the destination that
  # was removed, and the live resource's logAnalyticsConfiguration is now null.
  logs_destination = "azure-monitor"

  infrastructure_subnet_id       = azurerm_subnet.app.id
  internal_load_balancer_enabled = false # external ingress — app stays internet-facing

  workload_profile {
    name                  = "Consumption"
    workload_profile_type = "Consumption"
  }
}

resource "azurerm_container_app" "app" {
  name                         = "${var.name_prefix}-ca-uksouth"
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

  # ONE secret. Staging holds the same value under the same name (#156 normalised it), so the two
  # captures read alike — which is the point: a difference between them should mean something.
  #
  # The name records provenance deliberately. This is a COPY of the Key Vault secret
  # `entra-mcp-client-secret`, not a reference to it, so rotating the vault copy alone changes
  # nothing the app sends (#138). A provider-neutral name would hide the Entra credential behind it
  # and the 2027-03-01 expiry that comes with it.
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

    # Custom domain bound to a FREE MANAGED certificate. azurerm cannot create managed certs;
    # the binding is done out-of-band (az containerapp hostname bind --validation-method TXT).
    # Leave this block here for documentation; on adoption, ignore drift on the cert binding or
    # manage a BYO cert via azurerm_container_app_environment_certificate.
    # custom_domain {
    #   name                     = "vitally.fiscaltec.com"
    #   certificate_binding_type = "SniEnabled"
    #   certificate_id           = "<managed cert id>"
    # }
  }

  template {
    # Keep one replica always warm. With scale-to-zero (min 0), a request arriving after an
    # idle gap paid a server-side cold start (scheduling + ACR image pull over the private
    # endpoint + .NET boot + first-time Key Vault/Graph managed-identity token acquisitions) —
    # ~5s worst case in the logs, of which the first Graph token alone was ~3s. A warm replica
    # also keeps the token/JWKS/Key Vault caches hot. NOTE: this only affects the server-side
    # slice; client-perceived MCP latency is dominated by model inference, not this change.
    # Trade-off: one 0.5 vCPU / 1Gi replica is billed continuously at the idle rate.
    min_replicas = 1
    max_replicas = 3

    container {
      name   = "vitally-mcp"
      image  = "${azurerm_container_registry.acr.login_server}/vitally-mcp:${var.image_tag}"
      cpu    = 0.5
      memory = "1Gi"

      env {
        name  = "Vitally__Region"
        value = "EU"
      }
      env {
        name  = "Vitally__KeyVaultUri"
        value = "https://${azurerm_key_vault.secret.name}.vault.azure.net/"
      }
      env {
        name  = "AZURE_CLIENT_ID"
        value = var.managed_identity_client_id
      }
      env {
        name  = "OAuth__Authority"
        value = var.oauth_authority
      }
      env {
        name  = "OAuth__Audience"
        value = var.oauth_audience
      }
      # Deliberately a different variable from OAuth__Audience — see the note on oauth_resource in
      # variables.tf. They differ by exactly one trailing slash and must not be reunified.
      env {
        name  = "OAuth__Resource"
        value = var.oauth_resource
      }
      # Terminates the RFC 8707 `resource` parameter at the proxy and names the API by scope
      # instead. Required under Entra; empty is the RFC 8707 relay default, which Entra rejects.
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
        value = var.public_base_url
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
      # Set on production 2026-09-25 (#147). It switches on BOTH the Azure Monitor exporter — which
      # routes the audit records to AppEvents via microsoft.custom_event.name — and the suppression
      # of the VitallyMcp.AuditLogger category from the console provider. Unset, both are absent and
      # the records stay on stdout; the application treats an empty value as unset.
      env {
        name  = "ApplicationInsights__ConnectionString"
        value = var.application_insights_connection_string
      }
    }
  }
}

# Scheduled secret-expiry scanner (replaces the old Consumption Logic App).
resource "azurerm_container_app_job" "scanner" {
  name                         = "${var.name_prefix}-secscan-uksouth"
  resource_group_name          = data.azurerm_resource_group.rg.name
  location                     = var.location
  container_app_environment_id = azurerm_container_app_environment.env.id
  workload_profile_name        = "Consumption"
  replica_timeout_in_seconds   = 1800
  replica_retry_limit          = 1

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.app.id]
  }

  schedule_trigger_config {
    cron_expression          = "0 8 * * 1"
    parallelism              = 1
    replica_completion_count = 1
  }

  secret {
    name  = "teams-webhook"
    value = var.teams_webhook_url
  }

  template {
    container {
      name    = "secretscan"
      image   = "python:3-slim"
      cpu     = 0.5
      memory  = "1Gi"
      command = ["/bin/sh"]
      args    = ["-c", local.scanner_command]

      env {
        name  = "MI_CLIENT_ID"
        value = var.managed_identity_client_id
      }
      env {
        name  = "VAULT"
        value = azurerm_key_vault.secret.name
      }
      env {
        name        = "TEAMS_WEBHOOK"
        secret_name = "teams-webhook"
      }
    }
  }
}
