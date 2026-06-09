resource "azurerm_log_analytics_workspace" "law" {
  name                = "${var.name_prefix}-law-uksouth"
  resource_group_name = data.azurerm_resource_group.rg.name
  location            = var.location
  sku                 = "PerGB2018"
  retention_in_days   = 30
}

resource "azurerm_application_insights" "appi" {
  name                = "${var.name_prefix}-appi-uksouth"
  resource_group_name = data.azurerm_resource_group.rg.name
  location            = var.location
  application_type    = "web"
  workspace_id        = azurerm_log_analytics_workspace.law.id
}
