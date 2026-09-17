resource "azurerm_log_analytics_workspace" "law" {
  name                = "${var.name_prefix}-law-uksouth"
  resource_group_name = data.azurerm_resource_group.rg.name
  location            = var.location
  sku                 = "PerGB2018"
  retention_in_days   = 30

  # Query was opened on 2026-09-17 so operators can read telemetry at all: it was PrivateOnly, and
  # Azure Monitor supports no IP allowlist, so the only alternatives were public query or no query.
  # Query is not an anonymous surface (Entra auth + workspace RBAC). Deliberate, see
  # docs/superpowers/specs/2026-09-17-logging-observability-design.md.
  internet_query_enabled = true

  # Re-locked 2026-09-17 after being opened briefly that afternoon to test a hypothesis that turned
  # out to be wrong. Recorded because the reasoning is not obvious: the CAE's shipper was never
  # blocked by the network. local_authentication_enabled below is FALSE and appLogsConfiguration is
  # a shared-key shipper, so the workspace refuses it on AUTHENTICATION whatever this flag says.
  # That is why nothing arrived in 14 minutes of polling with it open.
  #
  # Nothing needs it open: the fix is a diagnostic setting (diagnostics.tf), which authenticates
  # through the Azure Monitor control plane rather than a shared key — the same reason Key Vault and
  # ACR records reach this workspace with ingestion private. Do NOT re-open this, and do NOT rescue
  # the shared-key path by setting local_authentication_enabled = true; that would trade a real
  # hardening control for the worse of two mechanisms.
  internet_ingestion_enabled = false

  local_authentication_enabled = false
}

resource "azurerm_application_insights" "appi" {
  name                = "${var.name_prefix}-appi-uksouth"
  resource_group_name = data.azurerm_resource_group.rg.name
  location            = var.location
  application_type    = "web"
  workspace_id        = azurerm_log_analytics_workspace.law.id

  # ⚠️ This component receives NOTHING. Verified 2026-09-17: no APPLICATIONINSIGHTS_CONNECTION_STRING
  # (or any ApplicationInsights* variable) on the Container App, no SDK package reference in
  # VitallyMcp.csproj, and no code in VitallyMcp/ referencing it. An earlier version of this comment
  # said "the Container App emits telemetry via the instrumentation key" — it does not, and never has.
  # Wiring the SDK up is the planned route for audit, failure and performance telemetry, and it
  # ingests over the private endpoint because the app's own traffic IS in the VNet — unlike the CAE's
  # platform log shipper, which is why ingestion below can stay false.
  internet_ingestion_enabled = false

  # Opened 2026-09-17 alongside the workspace, for the same reason and permanently.
  internet_query_enabled = true
}
