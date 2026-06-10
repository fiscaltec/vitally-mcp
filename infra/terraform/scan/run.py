"""Key Vault secret-expiry scanner — runs as a scheduled Container Apps Job.

Gets a managed-identity token from the Container Apps identity endpoint, lists Key Vault
secrets via the KV REST API, and posts an Adaptive Card to the Teams Workflows webhook for
any secret expiring within 30 days. No Azure CLI dependency (runs on python:3-slim).
"""
import json, os, datetime, urllib.request, urllib.parse


def getj(url, headers):
    return json.load(urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=30))


ie = os.environ["IDENTITY_ENDPOINT"]
ih = os.environ["IDENTITY_HEADER"]
cid = os.environ["MI_CLIENT_ID"]
vault = os.environ["VAULT"]

q = urllib.parse.urlencode({"resource": "https://vault.azure.net", "api-version": "2019-08-01", "client_id": cid})
tok = getj(ie + "?" + q, {"X-IDENTITY-HEADER": ih})["access_token"]
print("[scan] token acquired", flush=True)

# List secrets, following nextLink pagination so large vaults aren't truncated.
secrets = []
next_url = "https://%s.vault.azure.net/secrets?api-version=7.4" % vault
while next_url:
    page = getj(next_url, {"Authorization": "Bearer " + tok})
    secrets.extend(page.get("value", []))
    next_url = page.get("nextLink")
now = datetime.datetime.now(datetime.timezone.utc)
thr = now + datetime.timedelta(days=30)
near = []
for s in secrets:
    a = s.get("attributes") or {}
    exp = a.get("exp")
    if a.get("enabled") and exp:
        d = datetime.datetime.fromtimestamp(exp, datetime.timezone.utc)
        if d <= thr:
            near.append((s["id"].rstrip("/").split("/")[-1], d.date().isoformat()))
print("[scan] near-expiry:", near, flush=True)

if not near:
    print("[scan] none within 30 days")
    raise SystemExit(0)

lines = "\n\n".join("- %s expires %s" % (n, e) for n, e in near)
card = {
    "type": "message",
    "attachments": [{
        "contentType": "application/vnd.microsoft.card.adaptive",
        "content": {
            "type": "AdaptiveCard",
            "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
            "version": "1.5",
            "body": [
                {"type": "TextBlock", "size": "Medium", "weight": "Bolder", "text": "Key Vault secrets nearing expiry (<=30 days)"},
                {"type": "TextBlock", "wrap": True, "text": lines},
                {"type": "TextBlock", "wrap": True, "isSubtle": True, "text": "Vault: %s - rotate per the 180-day standard." % vault},
            ],
        },
    }],
}
urllib.request.urlopen(
    urllib.request.Request(os.environ["TEAMS_WEBHOOK"], json.dumps(card).encode(), {"Content-Type": "application/json"}),
    timeout=30,
)
print("[scan] posted to Teams", flush=True)
