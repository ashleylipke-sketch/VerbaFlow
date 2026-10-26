#!/usr/bin/env bash
# Puts VerbaFlow online in your Azure subscription. Run it in Azure Cloud Shell (Bash), from the VerbaFlow folder:
#
#   bash deploy/azure-setup.sh southafricanorth      (or: uksouth)
#
# It is safe to run again: anything that already exists is kept. It never prints a key or password.
# Full steps: docs/going-online.md.
set -euo pipefail

REGION="${1:-}"
case "$REGION" in
  southafricanorth|uksouth) ;;
  *) echo "Say where VerbaFlow should store its data: bash deploy/azure-setup.sh southafricanorth   (or uksouth)"; exit 1 ;;
esac

RG="verbaflow"
SAY() { printf '\n=== %s\n' "$*"; }
STOP() { printf '\n[X] %s\n' "$*" >&2; exit 1; }

[ -f Dockerfile ] && [ -d src/VerbaFlow.Api ] || STOP "Run this from the VerbaFlow folder (cd VerbaFlow first)."
az account show >/dev/null 2>&1 || STOP "Not signed in to Azure. Open Azure Cloud Shell from portal.azure.com and try again."
SUB=$(az account show --query name -o tsv)
TENANT=$(az account show --query tenantId -o tsv)
echo "Azure subscription: $SUB"

SAY "Switching on the Azure services VerbaFlow uses (once only, can take a few minutes)"
for p in Microsoft.App Microsoft.OperationalInsights Microsoft.ContainerRegistry Microsoft.DBforPostgreSQL Microsoft.Storage; do
  az provider register --namespace "$p" --wait >/dev/null
done
az extension add --name containerapp --upgrade --only-show-errors >/dev/null 2>&1 || true

SAY "Resource group '$RG' in $REGION"
if az group show -n "$RG" >/dev/null 2>&1; then
  HAVE=$(az group show -n "$RG" --query location -o tsv)
  [ "$HAVE" = "$REGION" ] || STOP "VerbaFlow is already set up in $HAVE. Run the script with $HAVE, or ask for help moving it."
  SUFFIX=$(az group show -n "$RG" --query "tags.vfSuffix" -o tsv)
else
  SUFFIX=$(openssl rand -hex 3)
  az group create -n "$RG" -l "$REGION" --tags vfSuffix="$SUFFIX" app=VerbaFlow -o none
fi
[ -n "$SUFFIX" ] || STOP "The resource group '$RG' was not made by this script. Remove or rename it, then run again."

ACR="verbaflow$SUFFIX"; STORAGE="verbaflow$SUFFIX"; PG="verbaflow-$SUFFIX"; LOGS="verbaflow-logs"; ENV="verbaflow-env"; APP="verbaflow"
SHARE="verbaflow-data"

SAY "Finding your Azure AI Speech and Azure OpenAI resources"
pick() { # kind [name] -> "name resourceGroup"; give SPEECH_NAME or OPENAI_NAME when you have more than one
  if [ -n "${2:-}" ]; then az cognitiveservices account list --query "[?name=='$2'].[name,resourceGroup]" -o tsv
  else az cognitiveservices account list --query "[?kind=='$1'].[name,resourceGroup]" -o tsv; fi
}
SPEECH=$(pick SpeechServices "${SPEECH_NAME:-}"); [ "$(printf '%s\n' "$SPEECH" | grep -c .)" = 1 ] || STOP "Expected exactly one Azure AI Speech resource, found: ${SPEECH:-none}. Name the one to use: SPEECH_NAME=<name> bash deploy/azure-setup.sh $REGION"
read -r SPEECH_NAME SPEECH_RG <<<"$SPEECH"
# The regional address, the same form that works on your computer.
SPEECH_ENDPOINT="https://$(az cognitiveservices account show -n "$SPEECH_NAME" -g "$SPEECH_RG" --query location -o tsv).api.cognitive.microsoft.com/"
SPEECH_KEY=$(az cognitiveservices account keys list -n "$SPEECH_NAME" -g "$SPEECH_RG" --query key1 -o tsv)
echo "Speech: $SPEECH_NAME"
OPENAI=$(pick OpenAI "${OPENAI_NAME:-}"); [ -n "$OPENAI" ] || OPENAI=$(pick AIServices)
[ "$(printf '%s\n' "$OPENAI" | grep -c .)" = 1 ] || STOP "Expected exactly one Azure OpenAI resource, found: ${OPENAI:-none}. Name the one to use: OPENAI_NAME=<name> bash deploy/azure-setup.sh $REGION"
read -r OPENAI_NAME OPENAI_RG <<<"$OPENAI"
OPENAI_ENDPOINT=$(az cognitiveservices account show -n "$OPENAI_NAME" -g "$OPENAI_RG" --query properties.endpoint -o tsv)
OPENAI_KEY=$(az cognitiveservices account keys list -n "$OPENAI_NAME" -g "$OPENAI_RG" --query key1 -o tsv)
OPENAI_DEPLOYMENT=${OPENAI_DEPLOYMENT:-gpt-4.1-mini}
echo "OpenAI: $OPENAI_NAME, deployment $OPENAI_DEPLOYMENT"

SAY "Finding the VerbaFlow app registration (Microsoft sign-in)"
CLIENT=$(az ad app list --display-name VerbaFlow --query "[0].appId" -o tsv)
[ -n "$CLIENT" ] || STOP "No app registration called VerbaFlow. Do docs/entra-sign-in-setup.md first."
echo "App registration: $CLIENT"

SAY "Storage for recordings and letterheads ($STORAGE)"
az storage account show -n "$STORAGE" -g "$RG" >/dev/null 2>&1 || \
  az storage account create -n "$STORAGE" -g "$RG" -l "$REGION" --sku Standard_LRS --kind StorageV2 \
    --min-tls-version TLS1_2 --allow-blob-public-access false -o none
STORAGE_KEY=$(az storage account keys list -n "$STORAGE" -g "$RG" --query "[0].value" -o tsv)
az storage share-rm show --storage-account "$STORAGE" -n "$SHARE" >/dev/null 2>&1 || \
  az storage share-rm create --storage-account "$STORAGE" -n "$SHARE" --quota 100 -o none

SAY "Database ($PG), the smallest size. This step can take 5 to 10 minutes the first time"
new_password() { echo "Vf$(openssl rand -base64 48 | tr -dc 'A-Za-z0-9' | cut -c1-30)"; }
if ! az postgres flexible-server show -n "$PG" -g "$RG" >/dev/null 2>&1; then
  PG_PASSWORD=$(new_password)
  az postgres flexible-server create -n "$PG" -g "$RG" -l "$REGION" --tier Burstable --sku-name Standard_B1ms \
    --storage-size 32 --version 16 --admin-user vfadmin --admin-password "$PG_PASSWORD" \
    --public-access 0.0.0.0 --backup-retention 7 --yes -o none
  az postgres flexible-server db create -s "$PG" -g "$RG" -d verbaflow -o none
else
  # Already made: keep the password the app already has.
  PG_PASSWORD=$(az containerapp secret show -n "$APP" -g "$RG" --secret-name db-password --query value -o tsv 2>/dev/null || true)
  if [ -z "$PG_PASSWORD" ]; then
    # Only VerbaFlow uses this password, so a new one is safe.
    PG_PASSWORD=$(new_password)
    az postgres flexible-server update -n "$PG" -g "$RG" --admin-password "$PG_PASSWORD" -o none
  fi
  az postgres flexible-server db show -s "$PG" -g "$RG" -d verbaflow >/dev/null 2>&1 || az postgres flexible-server db create -s "$PG" -g "$RG" -d verbaflow -o none
fi
DB_CONNECTION="Host=$PG.postgres.database.azure.com;Database=verbaflow;Username=vfadmin;Password=$PG_PASSWORD;SSL Mode=Require"

SAY "Container registry ($ACR) and building VerbaFlow from this folder (a few minutes)"
az acr show -n "$ACR" -g "$RG" >/dev/null 2>&1 || az acr create -n "$ACR" -g "$RG" -l "$REGION" --sku Basic --admin-enabled true -o none
TAG=$(git rev-parse --short HEAD 2>/dev/null || date +%Y%m%d%H%M)
az acr build -r "$ACR" -t "verbaflow:$TAG" . --only-show-errors -o none
ACR_PASSWORD=$(az acr credential show -n "$ACR" --query "passwords[0].value" -o tsv)

SAY "Logs and the Container Apps environment"
az monitor log-analytics workspace show -n "$LOGS" -g "$RG" >/dev/null 2>&1 || \
  az monitor log-analytics workspace create -n "$LOGS" -g "$RG" -l "$REGION" --retention-time 30 -o none
LOGS_ID=$(az monitor log-analytics workspace show -n "$LOGS" -g "$RG" --query customerId -o tsv)
LOGS_KEY=$(az monitor log-analytics workspace get-shared-keys -n "$LOGS" -g "$RG" --query primarySharedKey -o tsv)
az containerapp env show -n "$ENV" -g "$RG" >/dev/null 2>&1 || \
  az containerapp env create -n "$ENV" -g "$RG" -l "$REGION" --logs-workspace-id "$LOGS_ID" --logs-workspace-key "$LOGS_KEY" -o none
az containerapp env storage set -n "$ENV" -g "$RG" --storage-name vfdata --azure-file-account-name "$STORAGE" \
  --azure-file-account-key "$STORAGE_KEY" --azure-file-share-name "$SHARE" --access-mode ReadWrite -o none
ENV_ID=$(az containerapp env show -n "$ENV" -g "$RG" --query id -o tsv)

SAY "Starting VerbaFlow"
YAML=$(mktemp); trap 'rm -f "$YAML"' EXIT
export VF_PG_PASSWORD="$PG_PASSWORD" VF_DB="$DB_CONNECTION" VF_SPEECH_KEY="$SPEECH_KEY" VF_OPENAI_KEY="$OPENAI_KEY" \
  VF_ACR_PASSWORD="$ACR_PASSWORD" VF_SPEECH_ENDPOINT="$SPEECH_ENDPOINT" VF_OPENAI_ENDPOINT="$OPENAI_ENDPOINT" \
  VF_OPENAI_DEPLOYMENT="$OPENAI_DEPLOYMENT" VF_TENANT="$TENANT" VF_CLIENT="$CLIENT" VF_REGION="$REGION" VF_ENV_ID="$ENV_ID" \
  VF_ACR="$ACR" VF_TAG="$TAG"
python3 - "$YAML" <<'PY'
import json, os, sys
e = os.environ
secrets = {"db-password": e["VF_PG_PASSWORD"], "db": e["VF_DB"], "speech-key": e["VF_SPEECH_KEY"],
           "openai-key": e["VF_OPENAI_KEY"], "acr": e["VF_ACR_PASSWORD"]}
env = [("Speech__Endpoint", e["VF_SPEECH_ENDPOINT"]), ("OpenAI__Endpoint", e["VF_OPENAI_ENDPOINT"]),
       ("OpenAI__Deployment", e["VF_OPENAI_DEPLOYMENT"]), ("Entra__TenantId", e["VF_TENANT"]), ("Entra__ClientId", e["VF_CLIENT"])]
acr = e["VF_ACR"]
doc = {"location": e["VF_REGION"], "properties": {
  "managedEnvironmentId": e["VF_ENV_ID"],
  "configuration": {
    "activeRevisionsMode": "Single",
    "ingress": {"external": True, "targetPort": 8080, "transport": "auto", "allowInsecure": False},
    "secrets": [{"name": k, "value": v} for k, v in secrets.items()],
    "registries": [{"server": f"{acr}.azurecr.io", "username": acr, "passwordSecretRef": "acr"}]},
  "template": {
    "containers": [{
      "name": "verbaflow", "image": f"{acr}.azurecr.io/verbaflow:{e['VF_TAG']}",
      "resources": {"cpu": 1.0, "memory": "2Gi"},
      "env": [{"name": k, "value": v} for k, v in env] + [
        {"name": "Database__ConnectionString", "secretRef": "db"},
        {"name": "Speech__Key", "secretRef": "speech-key"},
        {"name": "OpenAI__Key", "secretRef": "openai-key"}],
      "volumeMounts": [{"volumeName": "data", "mountPath": "/data"}],
      "probes": [{"type": "Liveness", "httpGet": {"path": "/api/health", "port": 8080},
                  "initialDelaySeconds": 20, "periodSeconds": 30, "failureThreshold": 5}]}],
    "scale": {"minReplicas": 1, "maxReplicas": 1},
    "volumes": [{"name": "data", "storageType": "AzureFile", "storageName": "vfdata"}]}}}
with open(sys.argv[1], "w") as f: json.dump(doc, f)  # JSON is valid YAML
PY
if az containerapp show -n "$APP" -g "$RG" >/dev/null 2>&1; then
  az containerapp update -n "$APP" -g "$RG" --yaml "$YAML" -o none
else
  az containerapp create -n "$APP" -g "$RG" --yaml "$YAML" -o none
fi
URL="https://$(az containerapp show -n "$APP" -g "$RG" --query properties.configuration.ingress.fqdn -o tsv)/"

SAY "Letting Microsoft sign-in send people back to ${URL}signin"
APP_OBJECT=$(az ad app show --id "$CLIENT" --query id -o tsv)
URIS=$(az ad app show --id "$CLIENT" --query "spa.redirectUris" -o json)
BODY=$(python3 -c "import json,sys; u=json.loads(sys.argv[1]) or []; u += [] if sys.argv[2] in u else [sys.argv[2]]; print(json.dumps({'spa': {'redirectUris': u}}))" "$URIS" "${URL}signin")
az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$APP_OBJECT" \
  --headers Content-Type=application/json --body "$BODY" -o none \
  || echo "[!] Could not add the address to the app registration. Add ${URL}signin as a Single-page application redirect URI by hand (docs/going-online.md)."

SAY "Checking it answers"
for i in $(seq 1 30); do
  if curl -fs "${URL}api/health" >/dev/null 2>&1; then OK=1; break; fi; sleep 10
done
if [ "${OK:-}" = 1 ]; then
  printf '\nVerbaFlow is online at:\n\n    %s\n\nOpen it and click Sign in with Microsoft.\n' "$URL"
else
  printf '\n[!] VerbaFlow has not answered yet at %s\nWait a few minutes and open it. If it still does not load, see "If something goes wrong" in docs/going-online.md.\n' "$URL"
fi
