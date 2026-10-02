#!/usr/bin/env bash
# Deploy the API to Cloud Run, setting up what it needs the first time.
#
#     ./deploy.sh
#
# Safe to run again: each setup step leaves what already exists alone, and the
# deploy replaces the running revision with one built from this folder. Needs
# `gcloud auth login` as an owner of the project.
#
# What it sets up, once:
#   - the service account the API runs as, allowed to run BigQuery queries in
#     the project and to read the 'cyber' dataset — not 'cyber_test', not
#     anything else
#   - the 'api-key' secret: what callers send as X-Api-Key. Created with a
#     random value, which is also written to the headset's
#     Assets/StreamingAssets/cloud.key, so the two match. Only the service
#     account can read it.
set -euo pipefail

PROJECT=cyberattack-platform
REGION=us-east1
SERVICE=cyber-api
DATASET=cyber
SECRET=api-key
ACCOUNT="${SERVICE}@${PROJECT}.iam.gserviceaccount.com"

HERE="$(cd "$(dirname "$0")" && pwd)"
HEADSET_KEY="${HERE}/../Assets/StreamingAssets/cloud.key"

gcloud config set project "${PROJECT}" --quiet >/dev/null

if ! gcloud iam service-accounts describe "${ACCOUNT}" >/dev/null 2>&1; then
    gcloud iam service-accounts create "${SERVICE}" \
        --display-name="Cyberattack Platform API" \
        --description="Runs the ${SERVICE} Cloud Run service: reads the ${DATASET} dataset, runs queries, reads the ${SECRET} secret"
fi

gcloud projects add-iam-policy-binding "${PROJECT}" \
    --member="serviceAccount:${ACCOUNT}" --role=roles/bigquery.jobUser \
    --condition=None --format=none

# Dataset-level access, granted in SQL: the bq command for it needs a feature
# Google enables per project.
bq query --quiet --use_legacy_sql=false --location="${REGION}" \
    "GRANT \`roles/bigquery.dataViewer\` ON SCHEMA \`${PROJECT}\`.${DATASET} TO \"serviceAccount:${ACCOUNT}\"" \
    >/dev/null

if ! gcloud secrets describe "${SECRET}" >/dev/null 2>&1; then
    umask 077
    python3 -c "import secrets; print(secrets.token_urlsafe(32), end='')" > "${HEADSET_KEY}"
    gcloud secrets create "${SECRET}" --replication-policy=user-managed \
        --locations="${REGION}" --data-file="${HEADSET_KEY}"
    echo "Wrote the new key to ${HEADSET_KEY}"
fi

gcloud secrets add-iam-policy-binding "${SECRET}" \
    --member="serviceAccount:${ACCOUNT}" --role=roles/secretmanager.secretAccessor --format=none

# Nine seconds, under the headset's own ten, so a slow request fails here
# with a reason in the log rather than there with none. Two instances at most:
# the data is a few megabytes and one instance answers many requests at once,
# so a third would only ever be a runaway.
gcloud run deploy "${SERVICE}" \
    --source "${HERE}" \
    --region "${REGION}" \
    --service-account "${ACCOUNT}" \
    --allow-unauthenticated \
    --set-secrets "API_KEY=${SECRET}:latest" \
    --set-env-vars "GOOGLE_CLOUD_PROJECT=${PROJECT},BIGQUERY_DATASET=${DATASET}" \
    --memory 512Mi \
    --cpu 1 \
    --timeout 9 \
    --min-instances 0 \
    --max-instances 2 \
    --quiet

gcloud run services describe "${SERVICE}" --region "${REGION}" --format='value(status.url)'
