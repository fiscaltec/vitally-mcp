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

# NOTE (Microsoft Graph): the identity also holds the Graph application permission
# GroupMember.Read.All (for the live group-membership check). Graph app-role grants are
# not managed here — assign via Graph/PowerShell or azuread_app_role_assignment in a
# separate Entra-scoped config.

# CI/CD (GitHub Actions OIDC): GitHub authenticates as this identity to deploy. The federated
# credential subject matches the deploy workflow's `environment: production` job, so only that
# environment can mint a token. Audience is the value azure/login requests by default.
resource "azurerm_federated_identity_credential" "github_actions_prod" {
  name                = "github-actions-prod-env"
  resource_group_name = data.azurerm_resource_group.rg.name
  parent_id           = azurerm_user_assigned_identity.app.id
  audience            = ["api://AzureADTokenExchange"]
  issuer              = "https://token.actions.githubusercontent.com"
  subject             = "repo:fiscaltec/vitally-mcp:environment:production"
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
