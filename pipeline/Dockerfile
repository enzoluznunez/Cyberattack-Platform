# The API's container image. Cloud Run builds it from this folder with
# `gcloud run deploy --source`; .gcloudignore keeps everything but the API's
# own files out of the upload.
FROM python:3.13-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1

WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY api.py database.py metrics.py sheetcsv.py ./

# The API reads and never writes, so it runs as a user that cannot.
RUN useradd --no-create-home --shell /usr/sbin/nologin api
USER api

# Cloud Run says which port to listen on in $PORT.
CMD ["sh", "-c", "exec uvicorn api:app --host 0.0.0.0 --port ${PORT:-8080}"]
