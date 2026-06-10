# VNet flow logs → dedicated, hardened storage account.
# Applied to the live estate via CLI on 2026-06-10; adopt via the import blocks in imports.tf.
# Traffic analytics is intentionally off (would ingest into the now-private LAW); raw flow logs only.

resource "azurerm_storage_account" "flowlogs" {
  name                            = "vitallyprodflowuksouth"
  resource_group_name             = data.azurerm_resource_group.rg.name
  location                        = var.location
  account_tier                    = "Standard"
  account_replication_type        = "LRS"
  account_kind                    = "StorageV2"
  min_tls_version                 = "TLS1_2"
  https_traffic_only_enabled      = true
  allow_nested_items_to_be_public = false
  public_network_access_enabled   = true

  # Default-deny, with the trusted-services bypass so Network Watcher can write flow logs.
  network_rules {
    default_action = "Deny"
    bypass         = ["AzureServices"]
  }
}

resource "azurerm_network_watcher_flow_log" "vnet" {
  name                 = "${var.name_prefix}-vnetflow-uksouth"
  network_watcher_name = "NetworkWatcher_uksouth"
  resource_group_name  = "NetworkWatcherRG"
  location             = var.location
  target_resource_id   = azurerm_virtual_network.vnet.id
  storage_account_id   = azurerm_storage_account.flowlogs.id
  enabled              = true

  retention_policy {
    enabled = true
    days    = 30
  }
}
