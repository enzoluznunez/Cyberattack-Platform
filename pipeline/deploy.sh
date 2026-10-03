#!/usr/bin/env bash
# Deploy the API to Cloud Run behind API Gateway.
#
#     ./deploy.sh
#
# Safe to run again: each setup step leaves what already exists alone, the
# deploy replaces the running revision with one built from this folder, and the
# gateway is moved to a new config only when gateway.json has changed. Needs
# `gcloud auth login` as an owner of the project.
#
#     headset --(Google Cloud API key)--> API Gateway --(cyber-gateway-1)--> Cloud Run --> BigQuery
#
# The service is private: Cloud Run lets in only the gateway's service account,
# and the gateway lets in only callers whose API key is restricted to this API.
# That key is created in the console (APIs & Services > Credentials) and goes in
# the headset's Assets/StreamingAssets/cloud.key.
set -euo pipefail

PROJECT=cyberattack-platform
REGION=us-east1
SERVICE=cyber-api
DATASET=cyber
API=cyber
GATEWAY=cyber-gateway
ACCOUNT="${SERVICE}@${PROJECT}.iam.gserviceaccount.com"
GATEWAY_ACCOUNT="cyber-gateway-1@${PROJECT}.iam.gserviceaccount.com"

HERE="$(cd "$(dirname "$0")" && pwd)"

# A stale gateway.json would leave a new endpoint unreachable, so it stops the
# script before anything is built.
PYTHON="${HERE}/.venv/bin/python"
[ -x "${PYTHON}" ] || PYTHON=python3
"${PYTHON}" "${HERE}/gateway.py" --check

gcloud config set project "${PROJECT}" --quiet >/dev/null

# --- The service account the API runs as: BigQuery jobs in the project, and
# --- read access to the 'cyber' dataset, not 'cyber_test' or anything else.
if ! gcloud iam service-accounts describe "${ACCOUNT}" >/dev/null 2>&1; then
    gcloud iam service-accounts create "${SERVICE}" \
        --display-name="Cyberattack Platform API" \
        --description="Runs the ${SERVICE} Cloud Run service: reads the ${DATASET} dataset and runs queries"
fi

gcloud projects add-iam-policy-binding "${PROJECT}" \
    --member="serviceAccount:${ACCOUNT}" --role=roles/bigquery.jobUser \
    --condition=None --format=none

# Dataset-level access, granted in SQL: the bq command for it needs a feature
# Google enables per project.
bq query --quiet --use_legacy_sql=false --location="${REGION}" \
    "GRANT \`roles/bigquery.dataViewer\` ON SCHEMA \`${PROJECT}\`.${DATASET} TO \"serviceAccount:${ACCOUNT}\"" \
    >/dev/null

# --- The API on Cloud Run, private.
# Nine seconds, under the headset's own ten, so a slow request fails here
# with a reason in the log rather than there with none. Two instances at most:
# the data is a few megabytes and one instance answers many requests at once,
# so a third would only ever be a runaway.
gcloud run deploy "${SERVICE}" \
    --source "${HERE}" \
    --region "${REGION}" \
    --service-account "${ACCOUNT}" \
    --no-allow-unauthenticated \
    --clear-secrets \
    --set-env-vars "GOOGLE_CLOUD_PROJECT=${PROJECT},BIGQUERY_DATASET=${DATASET}" \
    --memory 512Mi \
    --cpu 1 \
    --timeout 9 \
    --min-instances 0 \
    --max-instances 2 \
    --quiet

# Only the gateway may call it.
gcloud run services add-iam-policy-binding "${SERVICE}" --region "${REGION}" \
    --member="serviceAccount:${GATEWAY_ACCOUNT}" --role=roles/run.invoker --format=none
if gcloud run services get-iam-policy "${SERVICE}" --region "${REGION}" --format=json | grep -q '"allUsers"'; then
    gcloud run services remove-iam-policy-binding "${SERVICE}" --region "${REGION}" \
        --member=allUsers --role=roles/run.invoker --format=none
fi

# --- The gateway, kept in step with gateway.json. A config cannot be edited,
# --- so each version of the file becomes a config named after its contents,
# --- and the gateway is moved to it only when the contents changed.
CONFIG="${API}-$(shasum -a 256 "${HERE}/gateway.json" | cut -c1-12)"

if ! gcloud api-gateway apis describe "${API}" >/dev/null 2>&1; then
    gcloud api-gateway apis create "${API}" --display-name="Cyberattack Platform"
fi

if ! gcloud api-gateway api-configs describe "${CONFIG}" --api="${API}" >/dev/null 2>&1; then
    gcloud api-gateway api-configs create "${CONFIG}" --api="${API}" \
        --openapi-spec="${HERE}/gateway.json" \
        --backend-auth-service-account="${GATEWAY_ACCOUNT}"
fi

CURRENT="$(gcloud api-gateway gateways describe "${GATEWAY}" --location="${REGION}" \
    --format='value(apiConfig.basename())' 2>/dev/null || true)"
if [ -z "${CURRENT}" ]; then
    gcloud api-gateway gateways create "${GATEWAY}" --api="${API}" --api-config="${CONFIG}" \
        --location="${REGION}"
elif [ "${CURRENT}" != "${CONFIG}" ]; then
    gcloud api-gateway gateways update "${GATEWAY}" --api="${API}" --api-config="${CONFIG}" \
        --location="${REGION}"
fi

# Callers' keys work only once the API is a service enabled in the project.
gcloud services enable "$(gcloud api-gateway apis describe "${API}" --format='value(managedService)')"

echo "https://$(gcloud api-gateway gateways describe "${GATEWAY}" --location="${REGION}" --format='value(defaultHostname)')"
