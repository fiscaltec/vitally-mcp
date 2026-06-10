locals {
  # The scanner Job decodes this base64'd Python script and runs it (matches the deployed job).
  scanner_command = "echo ${base64encode(file("${path.module}/scan/run.py"))} | base64 -d > /tmp/run.py && python3 /tmp/run.py"
}

resource "azurerm_container_app_environment" "env" {
  name                           = "${var.name_prefix}-cae-uksouth"
  resource_group_name            = data.azurerm_resource_group.rg.name
  location                       = var.location
  log_analytics_workspace_id     = azurerm_log_analytics_workspace.law.id
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
    min_replicas = 0
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
      env {
        name  = "OAuth__Resource"
        value = var.oauth_audience
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
