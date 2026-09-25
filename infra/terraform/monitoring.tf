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

  # ⚠️ LIVE on BOTH targets since 2026-09-25.
  #
  # #164 added Azure.Monitor.OpenTelemetry.AspNetCore and registers the exporter; the audit records
  # carry the microsoft.custom_event.name attribute that routes them to AppEvents. BOTH that
  # registration and the console suppression are conditional on ApplicationInsights__ConnectionString,
  # which is set on production (revision 39) and on staging (revision 17). Verified on each by reading
  # VitallyToolCall and VitallyUpstreamCall rows back out of AppEvents rather than inferred from a
  # healthy deploy — a wrong attribute routes to AppTraces with no error at all. AppRoleName separates
  # the two targets, so one component serves both without ambiguity.
  #
  # ⚠️ Staging is an ON-DEMAND app, so "set on staging" describes the app that exists today and NOT
  # any future one: the variable does not survive a recreate. containerapps-staging.tf carries it for
  # that reason. A staging app without it does not merely lose its own trail — once #142 enables
  # ContainerAppConsoleLogs on the shared CAE, its unsuppressed console records would be exported for
  # the whole environment.
  #
  # (Three earlier versions of this comment are worth not re-deriving: the first said there was no SDK
  # package at all, true until #164; the second said the variable was set on neither target, true until
  # 2026-09-25 morning; the third said production only, true for a few hours that same day.)
  #
  # It ingests over the private endpoint because the app's own traffic IS in the VNet — unlike the
  # CAE's platform log shipper, which is why ingestion below can stay false. DisableLocalAuth is true
  # on this component, so the exporter authenticates with the user-assigned managed identity, which
  # was granted Monitoring Metrics Publisher here on 2026-09-24. ⚠️ Verify that property with
  # `az resource show`: `az monitor app-insights component show` reports disableLocalAuth as null.
  internet_ingestion_enabled = false

  # Opened 2026-09-17 alongside the workspace, for the same reason and permanently.
  internet_query_enabled = true

  # Set 2026-09-17, matching the workspace above, which has had it since creation. It was unset —
  # i.e. local auth ENABLED — so the connection string would have been a write credential for a
  # component that phase 4 makes the home of PII-bearing audit records. The risk is fabricated
  # records rather than exfiltration: an audit trail that cannot distinguish genuine entries from
  # injected ones fails at the only job it has.
  #
  # Done NOW rather than with phase 4 deliberately: nothing emits to this component yet, so it
  # breaks nothing (verified — zero API keys, no connection string anywhere in the repo, no alert
  # rules), and turning it off first FORCES the phase 4 emitter down the managed-identity path
  # rather than relying on someone remembering not to reach for a connection string.
  #
  # Consequence for whoever wires that emitter: authenticate with the existing user-assigned managed
  # identity (Azure Monitor OpenTelemetry takes a `credential`), and give it Monitoring Metrics
  # Publisher on this component. A connection string will simply be refused.
  local_authentication_disabled = true
}
