"""Where the data lives: two BigQuery tables in one dataset.

    breaches        one row per breach
    fundamentals    one row per company per year

Settings come from the environment, or from pipeline/.env on a laptop;
.env.example shows its shape. Nothing here holds a password or a key: on a
laptop the client signs in with `gcloud auth application-default login`, and on
Cloud Run with the service's own account.
"""

import os
from functools import cache
from pathlib import Path

from dotenv import load_dotenv
from google.cloud import bigquery

load_dotenv(Path(__file__).with_name(".env"))

# Where the dataset is created, beside the bucket and the service.
LOCATION = "us-east1"

BREACHES = "breaches"
FUNDAMENTALS = "fundamentals"

# The most one query may scan. Both tables together are a few megabytes, so a
# query that reaches this is a mistake, and BigQuery refuses it rather than
# billing for it.
MAX_BYTES_BILLED = 100 * 1024 * 1024


@cache
def setting(name):
    """A setting by name from the environment, or None when it is not set."""
    return os.environ.get(name) or None


@cache
def client():
    """One client per process; it pools its own connections. The project comes
    from GOOGLE_CLOUD_PROJECT when set, and otherwise from the signed-in
    account's default, which on Cloud Run is the project it runs in."""
    return bigquery.Client(project=setting("GOOGLE_CLOUD_PROJECT"), location=LOCATION)


def name():
    return os.environ.get("BIGQUERY_DATASET", "cyber")


def table(which, dataset=None):
    """A table's full name, quoted for SQL."""
    return f"`{client().project}.{dataset or name()}.{which}`"


def query(sql, parameters=()):
    """Rows of one query. Every value a caller supplied travels as a query
    parameter, never inside the SQL text."""
    config = bigquery.QueryJobConfig(query_parameters=list(parameters),
                                     maximum_bytes_billed=MAX_BYTES_BILLED)
    return list(client().query(sql, job_config=config).result())
