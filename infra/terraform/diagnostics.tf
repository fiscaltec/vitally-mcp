# Diagnostic settings that deliver telemetry into vitally-prod-law-uksouth.
#
# WHY THIS FILE EXISTS, and why the Container App's own appLogsConfiguration is not the mechanism:
#
# The CAE's appLogsConfiguration is a SHARED-KEY shipper — it authenticates with the workspace id
# and primary shared key. monitoring.tf sets local_authentication_enabled = false, so the workspace
# refuses it. That path has therefore never delivered a single row, from the day the workspace was
# created, and no network setting changes it (verified 2026-09-17: ContainerAppConsoleLogs_CL had
# zero rows, ever, while Key Vault and ACR data arrived normally).
#
# A diagnostic setting authenticates through the Azure Monitor control plane instead, over a private
# Microsoft channel that is governed by neither internet_ingestion_enabled nor the AMPLS access
# modes. That is precisely why Key Vault and ACR records reach this workspace today with ingestion
# private and local auth disabled — and it is why this is the right mechanism rather than a
# workaround.
#
# Design: docs/superpowers/specs/2026-09-17-logging-observability-design.md. Issue: #142.

resource "azurerm_monitor_diagnostic_setting" "cae_system_logs" {
  name                       = "cae-system-logs"
  target_resource_id         = azurerm_container_app_environment.env.id
  log_analytics_workspace_id = azurerm_log_analytics_workspace.law.id

  # "Dedicated" = resource-specific export, so records land in the real ContainerAppSystemLogs table
  # rather than the generic AzureDiagnostics/_CL shape. Load-bearing rather than cosmetic: per-table
  # retention and table-level RBAC are only possible against a real table, and both are required by
  # the retention work in #93.
  log_analytics_destination_type = "Dedicated"

  enabled_log {
    category = "ContainerAppSystemLogs"
  }
}

# ⚠️ ContainerAppConsoleLogs is DELIBERATELY NOT ENABLED HERE YET — this is phase 2b, and it is
# gated, not forgotten.
#
# The console stream currently carries customer identifiers: AuditLogger writes the caller's object
# id and the Vitally resource path to stdout, and System.Net.Http.HttpClient logs outbound URIs
# including query strings, which carry Search_users / Search_admins terms (#143). Exporting it today
# would put that data into a table documented as customer-data-free, with the shortest retention and
# the broadest access — the opposite of where the 2026-09-17 policy decision deliberately placed it.
#
# Enable it only after BOTH:
#   - #143, which filters the HttpClient categories down to Warning in Program.cs, and
#   - the audit reroute, which moves AuditLogger to TelemetryClient.TrackEvent and suppresses it
#     from the console provider specifically.
#
# Until then, read startup failures — which reach stdout and so are NOT covered by the system-log
# category above — from the live stream, which is independent of this export path:
#
#   az containerapp logs show -n vitally-prod-ca-uksouth -g vitally-prod-rg-uksouth \
#     --type console --tail 100
#
# ContainerAppHTTPLogs is also deliberately absent: it carries request URLs and needs the same PII
# scrutiny as the HttpClient categories before it can be considered.
