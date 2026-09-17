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
# workaround. Microsoft says so directly for this case:
#
#   "Private link: Sending logs directly to a Log Analytics Workspace through Private Link isn't
#    supported. However, you can use Azure Monitor and send your logs to the same Log Analytics
#    Workspace. This indirection is required to prevent system log data loss."
#
# ⚠️ THIS FILE IS HALF THE MECHANISM. A diagnostic setting is INERT unless the environment's
# logs_destination is "azure-monitor" (containerapps.tf). With the default "log-analytics" the CAE
# keeps using the shared-key shipper and ignores diagnostic settings entirely — which is exactly what
# happened on 2026-09-17: this setting was created, nothing arrived for 18 minutes, and the cause was
# the destination, not the setting. Change the two together or neither.
#
# Design: docs/superpowers/specs/2026-09-17-logging-observability-design.md. Issue: #142.

resource "azurerm_monitor_diagnostic_setting" "cae_system_logs" {
  name                       = "cae-system-logs"
  target_resource_id         = azurerm_container_app_environment.env.id
  log_analytics_workspace_id = azurerm_log_analytics_workspace.law.id

  # ⚠️ log_analytics_destination_type is deliberately NOT set, and an earlier version of this file
  # set it to "Dedicated" — which was wrong in a way worth recording, because the API does not tell
  # you. Both `az monitor diagnostic-settings create --export-to-resource-specific true` and an
  # explicit PUT carrying "logAnalyticsDestinationType": "Dedicated" RETURN it in their response and
  # then store null. Reading the setting back from ARM is the only way to see that. Claiming it here
  # would document a property the live resource does not have.
  #
  # It is implicit for this resource type — CONFIRMED 2026-09-17 against the live table once records
  # began flowing, rather than inferred from the documentation. ContainerAppSystemLogs has typed
  # columns (ContainerAppName, Reason, RevisionName, ReplicaName) with no "_s" suffixes, which is the
  # resource-specific shape; the custom-log shape would be ContainerAppSystemLogs_CL with _s columns.
  # So records land in the real table and per-table retention is available to #93, despite the
  # property reading null.

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
