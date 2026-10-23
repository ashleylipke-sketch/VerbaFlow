#!/usr/bin/env bash
# Puts the newest VerbaFlow online. Run it in Azure Cloud Shell (Bash), from the VerbaFlow folder:
#
#   bash deploy/azure-update.sh
#
# Anyone dictating at that moment loses what they have not saved, so pick a quiet moment.
set -euo pipefail
RG="verbaflow"; APP="verbaflow"
[ -f Dockerfile ] || { echo "Run this from the VerbaFlow folder (cd VerbaFlow first)."; exit 1; }
SUFFIX=$(az group show -n "$RG" --query "tags.vfSuffix" -o tsv 2>/dev/null || true)
[ -n "$SUFFIX" ] || { echo "VerbaFlow is not set up yet. Run: bash deploy/azure-setup.sh <region>"; exit 1; }
ACR="verbaflow$SUFFIX"

echo "=== Getting the newest version from GitHub"
git pull --ff-only
TAG=$(git rev-parse --short HEAD)

echo "=== Building version $TAG (a few minutes)"
az acr build -r "$ACR" -t "verbaflow:$TAG" . --only-show-errors -o none

echo "=== Switching VerbaFlow to version $TAG"
az containerapp update -n "$APP" -g "$RG" --image "$ACR.azurecr.io/verbaflow:$TAG" -o none
URL="https://$(az containerapp show -n "$APP" -g "$RG" --query properties.configuration.ingress.fqdn -o tsv)/"
echo "Done. VerbaFlow is at $URL (it can take a minute to come back)."
