# Adoption via import (Terraform 1.5+ import blocks).
# These resources ALREADY EXIST (deployed manually). Run `terraform plan` to preview the import
# + any drift, then `terraform apply` to bring them under management. Do NOT apply without first
# reviewing the plan — the goal is to ADOPT, not recreate.
#
# Sub: 282207c6-4107-47fa-9d4e-b2fa9b3066cb  RG: vitally-prod-rg-uksouth
#
# Comment these out again once the import has completed successfully.

locals {
  rg_id = "/subscriptions/282207c6-4107-47fa-9d4e-b2fa9b3066cb/resourceGroups/vitally-prod-rg-uksouth"
}

import {
  to = azurerm_virtual_network.vnet
  id = "${local.rg_id}/providers/Microsoft.Network/virtualNetworks/vitally-prod-vnet-uksouth"
}
import {
  to = azurerm_subnet.app
  id = "${local.rg_id}/providers/Microsoft.Network/virtualNetworks/vitally-prod-vnet-uksouth/subnets/snet-app"
}
import {
  to = azurerm_subnet.pe
  id = "${local.rg_id}/providers/Microsoft.Network/virtualNetworks/vitally-prod-vnet-uksouth/subnets/snet-pe"
}
import {
  to = azurerm_public_ip.nat
  id = "${local.rg_id}/providers/Microsoft.Network/publicIPAddresses/vitally-prod-natpip-uksouth"
}
import {
  to = azurerm_nat_gateway.nat
  id = "${local.rg_id}/providers/Microsoft.Network/natGateways/vitally-prod-natgw-uksouth"
}
import {
  to = azurerm_private_dns_zone.vault
  id = "${local.rg_id}/providers/Microsoft.Network/privateDnsZones/privatelink.vaultcore.azure.net"
}
import {
  to = azurerm_private_dns_zone.acr
  id = "${local.rg_id}/providers/Microsoft.Network/privateDnsZones/privatelink.azurecr.io"
}
import {
  to = azurerm_user_assigned_identity.app
  id = "${local.rg_id}/providers/Microsoft.ManagedIdentity/userAssignedIdentities/vitally-prod-id-uksouth"
}
import {
  to = azurerm_key_vault.secret
  id = "${local.rg_id}/providers/Microsoft.KeyVault/vaults/vitally-prod-kv-uksouth"
}
import {
  to = azurerm_key_vault.cmk
  id = "${local.rg_id}/providers/Microsoft.KeyVault/vaults/vitally-prod-cmk-uksouth"
}
import {
  to = azurerm_container_registry.acr
  id = "${local.rg_id}/providers/Microsoft.ContainerRegistry/registries/vitallyproducruksouth"
}
import {
  to = azurerm_log_analytics_workspace.law
  id = "${local.rg_id}/providers/Microsoft.OperationalInsights/workspaces/vitally-prod-law-uksouth"
}
import {
  to = azurerm_application_insights.appi
  id = "${local.rg_id}/providers/Microsoft.Insights/components/vitally-prod-appi-uksouth"
}
import {
  to = azurerm_container_app_environment.env
  id = "${local.rg_id}/providers/Microsoft.App/managedEnvironments/vitally-prod-cae-uksouth"
}
import {
  to = azurerm_container_app.app
  id = "${local.rg_id}/providers/Microsoft.App/containerApps/vitally-prod-ca-uksouth"
}
import {
  to = azurerm_container_app_job.scanner
  id = "${local.rg_id}/providers/Microsoft.App/jobs/vitally-prod-secscan-uksouth"
}
import {
  to = azurerm_private_endpoint.kv
  id = "${local.rg_id}/providers/Microsoft.Network/privateEndpoints/vitally-prod-pe-kv-uksouth"
}
import {
  to = azurerm_private_endpoint.acr
  id = "${local.rg_id}/providers/Microsoft.Network/privateEndpoints/vitally-prod-pe-acr-uksouth"
}

# ---- Need a looked-up ID first (uncomment + fill in, then plan) ----
# Diagnostic settings:  import id = "<target-resource-id>|to-law"
# Role assignments:     az role assignment list --scope <scope> --query "[].id"  → import "<assignment-id>"
# CMK key:              needs data-plane access to the firewalled CMK vault; import
#                       "https://vitally-prod-cmk-uksouth.vault.azure.net/keys/acr-cmk/<version>"
# DNS vnet links / NAT associations: see README for the composite ID formats.
