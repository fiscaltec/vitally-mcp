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
