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
# It was gated on the console stream carrying customer identifiers: AuditLogger wrote the caller's
# object id and the Vitally resource path to stdout, and System.Net.Http.HttpClient logged outbound
# URIs including query strings, which carry Search_users / Search_admins terms (#143). Exporting it
# then would have put that data into a table documented as customer-data-free, with the shortest
# retention and the broadest access — the opposite of where the 2026-09-17 policy decision placed it.
#
# ⚠️ BOTH conditions are now MET ON PRODUCTION, so this is unfinished work rather than a blocked gate:
#   - #143 filters the HttpClient categories down to Warning in Program.cs — closed.
#   - The audit reroute landed and was SWITCHED ON 2026-09-25. AuditLogger's records carry the
#     microsoft.custom_event.name attribute and Program.cs suppresses the category from the console
#     provider. NOTE it lands via the Azure Monitor OpenTelemetry exporter, not TelemetryClient
#     .TrackEvent as this comment used to say — see the design doc's routing decision. Both the
#     export and the suppression are conditional on ApplicationInsights__ConnectionString, which is
#     set on BOTH targets as of 2026-09-25 (production revision 39, staging revision 17), each
#     verified by reading VitallyToolCall rows back out of AppEvents; both consoles now carry only a
#     customer-data-free breadcrumb.
#
# So this is unfinished work, not a blocked gate — it is simply not enabled yet.
#
# ⚠️ It is NOT per-target, and that outlives the enabling. This setting is attached to the shared
# CAE, so it exports the console of EVERY app in the environment; there is no way to scope it to
# production. Staging is an on-demand app and the variable does NOT survive a recreate, so once this
# is enabled, a staging spin-up that omits ApplicationInsights__ConnectionString would carry that
# app's unsuppressed audit records — caller object ids, tool arguments including free-text search
# terms, record ids, against the production Vitally tenant — into the console table.
#
# That is the same failure mode Authorization__ReadOnly already has, so the staging spin-up now has
# two variables to set and containerapps-staging.tf carries both. Check them together.
#
# Until then, read startup failures — which reach stdout and so are NOT covered by the system-log
# category above — from the live stream, which is independent of this export path:
#
#   az containerapp logs show -n vitally-prod-ca-uksouth -g vitally-prod-rg-uksouth \
#     --type console --tail 100
#
# ContainerAppHTTPLogs is also deliberately absent: it carries request URLs and needs the same PII
# scrutiny as the HttpClient categories before it can be considered.
