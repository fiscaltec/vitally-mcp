# User-assigned managed identity used by the Container App, the scanner Job, and ACR CMK.
resource "azurerm_user_assigned_identity" "app" {
  name                = "${var.name_prefix}-id-uksouth"
  resource_group_name = data.azurerm_resource_group.rg.name
  location            = var.location
}

# Runtime: read the Vitally API key from the secret vault.
resource "azurerm_role_assignment" "mi_kv_secrets_user" {
  scope                = azurerm_key_vault.secret.id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_user_assigned_identity.app.principal_id
}

# Runtime: pull the container image.
resource "azurerm_role_assignment" "mi_acr_pull" {
  scope                = azurerm_container_registry.acr.id
  role_definition_name = "AcrPull"
  principal_id         = azurerm_user_assigned_identity.app.principal_id
}

# ACR CMK: wrap/unwrap the customer-managed key in the dedicated CMK vault.
resource "azurerm_role_assignment" "mi_cmk_crypto" {
  scope                = azurerm_key_vault.cmk.id
  role_definition_name = "Key Vault Crypto Service Encryption User"
  principal_id         = azurerm_user_assigned_identity.app.principal_id
}

# Telemetry: publish audit records and traces to Application Insights. Added 2026-09-24 with #164's
# audit routing. It is required rather than optional: the component has DisableLocalAuth = true, so
# the instrumentation key in the connection string is NOT accepted as a credential and the exporter
# authenticates with this identity. Without this grant the records are dropped at ingestion —
# asynchronously, so nothing in the app notices.
#
# ⚠️ Verify DisableLocalAuth with `az resource show`, not `az monitor app-insights component show`:
# the extension reports it as null for this component while ARM reports true.
resource "azurerm_role_assignment" "mi_monitoring_metrics_publisher" {
  scope                = azurerm_application_insights.appi.id
  role_definition_name = "Monitoring Metrics Publisher"
  principal_id         = azurerm_user_assigned_identity.app.principal_id
}

# NOTE (Microsoft Graph): the identity also holds the Graph application permission
# GroupMember.Read.All (for the live group-membership check). Graph app-role grants are
# not managed here — assign via Graph/PowerShell or azuread_app_role_assignment in a
# separate Entra-scoped config.

# CI/CD (GitHub Actions OIDC): GitHub authenticates as this identity to deploy. The federated
# credential subject matches the deploy workflow's `environment:` job, so only that environment can
# mint a token. Audience is the value azure/login requests by default.
#
# ONE CREDENTIAL PER TARGET, and that is the whole reason deploy.yml keys off a GitHub environment
# name: the subject is an exact string match, so a deploy to a target with no credential here fails
# at azure/login rather than deploying somewhere unintended.
resource "azurerm_federated_identity_credential" "github_actions_prod" {
  name                = "github-actions-prod-env"
  resource_group_name = data.azurerm_resource_group.rg.name
  parent_id           = azurerm_user_assigned_identity.app.id
  audience            = ["api://AzureADTokenExchange"]
  issuer              = "https://token.actions.githubusercontent.com"
  subject             = "repo:fiscaltec/vitally-mcp:environment:production"
}

resource "azurerm_federated_identity_credential" "github_actions_staging" {
  name                = "github-actions-staging-env"
  resource_group_name = data.azurerm_resource_group.rg.name
  parent_id           = azurerm_user_assigned_identity.app.id
  audience            = ["api://AzureADTokenExchange"]
  issuer              = "https://token.actions.githubusercontent.com"
  subject             = "repo:fiscaltec/vitally-mcp:environment:staging"
}

# Deploy: `az acr import` the built image into the private ACR. AcrPush does NOT include the
# importImage action, so the deploy identity needs Contributor on the registry.
resource "azurerm_role_assignment" "mi_acr_contributor" {
  scope                = azurerm_container_registry.acr.id
  role_definition_name = "Contributor"
  principal_id         = azurerm_user_assigned_identity.app.principal_id
}

# Deploy: roll the Container App to the new revision (`az containerapp update`).
resource "azurerm_role_assignment" "mi_ca_contributor" {
  scope                = azurerm_container_app.app.id
  role_definition_name = "Contributor"
  principal_id         = azurerm_user_assigned_identity.app.principal_id
}

# Deploy: the same, for staging. Scoped to the app rather than the resource group so a deploy to one
# target cannot roll the other.
resource "azurerm_role_assignment" "mi_ca_staging_contributor" {
  scope                = azurerm_container_app.staging.id
  role_definition_name = "Contributor"
  principal_id         = azurerm_user_assigned_identity.app.principal_id
}
