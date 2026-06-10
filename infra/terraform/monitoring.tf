resource "azurerm_log_analytics_workspace" "law" {
  name                = "${var.name_prefix}-law-uksouth"
  resource_group_name = data.azurerm_resource_group.rg.name
  location            = var.location
  sku                 = "PerGB2018"
  retention_in_days   = 30

  # Privatised via AMPLS (ampls.tf): ingestion/query reachable only over the private endpoint.
  internet_ingestion_enabled   = false
  internet_query_enabled       = false
  local_authentication_enabled = false
}

resource "azurerm_application_insights" "appi" {
  name                = "${var.name_prefix}-appi-uksouth"
  resource_group_name = data.azurerm_resource_group.rg.name
  location            = var.location
  application_type    = "web"
  workspace_id        = azurerm_log_analytics_workspace.law.id

  # Privatised via AMPLS (ampls.tf). Local auth (connection-string ingestion) is left ENABLED:
  # the Container App emits telemetry via the instrumentation key, so disabling it would break it.
  internet_ingestion_enabled = false
  internet_query_enabled     = false
}
