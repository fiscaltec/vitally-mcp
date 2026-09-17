# Azure Monitor Private Link Scope (AMPLS) — privatises Log Analytics + Application Insights.
# Applied to the live estate via CLI on 2026-06-10; adopt via the import blocks in imports.tf.
# The matching public-access lockdown lives on the resources themselves in monitoring.tf.

# Dedicated PE subnet — the azuremonitor private endpoint consumes ~13 IPs, so the existing
# /28 snet-pe (KV + ACR endpoints) cannot host it; this /27 sits in the free VNet space.
resource "azurerm_subnet" "pe_monitor" {
  name                              = "snet-pe-monitor"
  resource_group_name               = data.azurerm_resource_group.rg.name
  virtual_network_name              = azurerm_virtual_network.vnet.name
  address_prefixes                  = ["10.80.0.96/27"]
  private_endpoint_network_policies = "Disabled"
}

# ---- Azure Monitor private DNS zones (the 5 required for the azuremonitor group) ----
locals {
  monitor_private_dns_zones = [
    "privatelink.monitor.azure.com",
    "privatelink.oms.opinsights.azure.com",
    "privatelink.ods.opinsights.azure.com",
    "privatelink.agentsvc.azure-automation.net",
    "privatelink.blob.core.windows.net",
  ]
}

resource "azurerm_private_dns_zone" "monitor" {
  for_each            = toset(local.monitor_private_dns_zones)
  name                = each.value
  resource_group_name = data.azurerm_resource_group.rg.name
}

resource "azurerm_private_dns_zone_virtual_network_link" "monitor" {
  for_each              = toset(local.monitor_private_dns_zones)
  name                  = "link-${replace(replace(each.value, "privatelink.", ""), ".", "-")}"
  resource_group_name   = data.azurerm_resource_group.rg.name
  private_dns_zone_name = azurerm_private_dns_zone.monitor[each.value].name
  virtual_network_id    = azurerm_virtual_network.vnet.id
  registration_enabled  = false
}

# ---- The scope + scoped resources ----
resource "azurerm_monitor_private_link_scope" "ampls" {
  name                = "${var.name_prefix}-ampls-uksouth"
  resource_group_name = data.azurerm_resource_group.rg.name

  # These govern traffic FROM networks connected through the private endpoint — whether such a
  # network may also reach Azure Monitor resources outside this scope. They are NOT the control on
  # whether a resource accepts public traffic; that is internet_*_enabled in monitoring.tf.
  #
  # Two consequences that cost time on 2026-09-17 before the docs were read properly:
  #  - Log Analytics ingestion uses resource-specific endpoints and does not adhere to these modes
  #    at all, so this was never what blocked the Container Apps log shipper.
  #  - data delivered by a DIAGNOSTIC SETTING travels a private Microsoft channel and is governed by
  #    neither these modes nor internet_ingestion_enabled. That is why Key Vault and ACR records
  #    arrive while the CAE's shared-key shipper delivers nothing, and it is the fix in #142.
  ingestion_access_mode = "PrivateOnly"

  # Opened 2026-09-17 so operators can query at all; ingestion stays private. See monitoring.tf and
  # docs/superpowers/specs/2026-09-17-logging-observability-design.md.
  query_access_mode = "Open"
}

resource "azurerm_monitor_private_link_scoped_service" "law" {
  name                = "law-link"
  resource_group_name = data.azurerm_resource_group.rg.name
  scope_name          = azurerm_monitor_private_link_scope.ampls.name
  linked_resource_id  = azurerm_log_analytics_workspace.law.id
}

resource "azurerm_monitor_private_link_scoped_service" "appi" {
  name                = "appi-link"
  resource_group_name = data.azurerm_resource_group.rg.name
  scope_name          = azurerm_monitor_private_link_scope.ampls.name
  linked_resource_id  = azurerm_application_insights.appi.id
}

# ---- Private endpoint (binds all 5 zones so endpoint A-records auto-populate) ----
resource "azurerm_private_endpoint" "ampls" {
  name                = "${var.name_prefix}-pe-ampls-uksouth"
  resource_group_name = data.azurerm_resource_group.rg.name
  location            = var.location
  subnet_id           = azurerm_subnet.pe_monitor.id

  private_service_connection {
    name                           = "${var.name_prefix}-pe-ampls-uksouth-conn"
    private_connection_resource_id = azurerm_monitor_private_link_scope.ampls.id
    subresource_names              = ["azuremonitor"]
    is_manual_connection           = false
  }

  private_dns_zone_group {
    name                 = "ampls-dns-group"
    private_dns_zone_ids = [for z in azurerm_private_dns_zone.monitor : z.id]
  }
}
