data "azurerm_client_config" "current" {}

# ---- Secret vault: fully private (private endpoint only) ----
resource "azurerm_key_vault" "secret" {
  name                          = "${var.name_prefix}-kv-uksouth"
  resource_group_name           = data.azurerm_resource_group.rg.name
  location                      = var.location
  tenant_id                     = data.azurerm_client_config.current.tenant_id
  sku_name                      = "standard"
  rbac_authorization_enabled    = true
  purge_protection_enabled      = true
  soft_delete_retention_days    = 90
  public_network_access_enabled = false
}

# NOTE: the 'vitally-shared' secret VALUE (the Vitally API key) is intentionally NOT managed
# here — it would land in Terraform state. Manage it out-of-band (az keyvault secret set) and set
# its expiry per the 180-day rotation standard. The scanner Job alerts before expiry.

resource "azurerm_private_endpoint" "kv" {
  name                = "${var.name_prefix}-pe-kv-uksouth"
  resource_group_name = data.azurerm_resource_group.rg.name
  location            = var.location
  subnet_id           = azurerm_subnet.pe.id

  private_service_connection {
    name                           = "kv-conn"
    private_connection_resource_id = azurerm_key_vault.secret.id
    subresource_names              = ["vault"]
    is_manual_connection           = false
  }
  private_dns_zone_group {
    name                 = "kv-zg"
    private_dns_zone_ids = [azurerm_private_dns_zone.vault.id]
  }
}

resource "azurerm_monitor_diagnostic_setting" "kv" {
  name                       = "to-law"
  target_resource_id         = azurerm_key_vault.secret.id
  log_analytics_workspace_id = azurerm_log_analytics_workspace.law.id

  enabled_log { category = "AuditEvent" }
  enabled_log { category = "AzurePolicyEvaluationDetails" }
  enabled_metric { category = "AllMetrics" }
}

# ---- Dedicated CMK vault: firewalled (deny + trusted-services bypass) so ACR can reach the key ----
resource "azurerm_key_vault" "cmk" {
  name                          = "${var.name_prefix}-cmk-uksouth"
  resource_group_name           = data.azurerm_resource_group.rg.name
  location                      = var.location
  tenant_id                     = data.azurerm_client_config.current.tenant_id
  sku_name                      = "standard"
  rbac_authorization_enabled    = true
  purge_protection_enabled      = true
  soft_delete_retention_days    = 90
  public_network_access_enabled = true

  network_acls {
    default_action = "Deny"
    bypass         = "AzureServices"
  }
}

# RSA key for ACR CMK encryption. versionless_id (used by ACR) enables auto-rotation.
# CAVEAT: managing this key via TF needs the runner to have a data-plane path to the firewalled
# CMK vault (trusted-services bypass blocks arbitrary runners). On adoption it is imported, not
# created; for ongoing management run from a permitted network or manage the key out-of-band.
resource "azurerm_key_vault_key" "acr_cmk" {
  name         = "acr-cmk"
  key_vault_id = azurerm_key_vault.cmk.id
  key_type     = "RSA"
  key_size     = 3072
  key_opts     = ["wrapKey", "unwrapKey"]

  depends_on = [azurerm_role_assignment.mi_cmk_crypto]
}
