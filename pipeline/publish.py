"""Publish the cleaned tables to BigQuery.

rebuild.py calls this with what clean.usable() returned. Each table is replaced
whole rather than updated in place: the tables are derived, so rebuilding them
is the one way to be sure nothing stale survives.

The schemas are written out rather than inferred from the frames, so a column
changing type in the export fails the load instead of quietly changing what
the API reads. A figure the export did not report is NULL, and a list it left
empty is an empty array, so "unreported" has one spelling per kind of column.
"""

import pandas_gbq
from google.api_core.exceptions import NotFound
from google.cloud import bigquery

import database

F = bigquery.SchemaField

SCHEMAS = {
    database.BREACHES: [
        F("breach_key", "INT64", mode="REQUIRED"),
        F("cik", "INT64", mode="REQUIRED"),
        F("company", "STRING", mode="REQUIRED"),
        F("targets", "STRING", mode="REPEATED"),
        F("target_relationships", "STRING", mode="REPEATED"),
        F("ticker", "STRING"),
        F("market", "STRING"),
        F("state", "STRING"),
        F("region", "STRING"),
        F("country", "STRING", mode="REQUIRED"),
        F("city", "STRING"),
        F("lat", "FLOAT64"),
        F("lon", "FLOAT64"),
        F("sic", "INT64"),
        F("sic_description", "STRING"),
        F("naics", "INT64"),
        F("division", "STRING"),
        F("year", "INT64", mode="REQUIRED"),
        F("disclosed_on", "DATE", mode="REQUIRED"),
        F("discovered_on", "DATE"),
        F("started_on", "DATE"),
        F("ended_on", "DATE"),
        F("cost_usd", "FLOAT64"),
        F("records_lost", "INT64"),
        F("information_type", "STRING"),
        F("attack_types", "STRING", mode="REPEATED"),
        F("information_accessed", "STRING", mode="REPEATED"),
        F("disclosed_to_sec", "BOOL", mode="REQUIRED"),
        F("filing_url", "STRING"),
        F("filing_date", "DATE"),
        F("filing_type", "STRING"),
        F("auditor", "STRING"),
        F("market_cap_usd", "FLOAT64"),
        F("revenue_usd", "FLOAT64"),
        F("net_income_usd", "FLOAT64"),
        F("assets_usd", "FLOAT64"),
    ],
    database.FUNDAMENTALS: [
        F("cik", "INT64", mode="REQUIRED"),
        F("gvkey", "INT64"),
        F("ticker", "STRING", mode="REQUIRED"),
        F("company", "STRING"),
        F("year", "INT64", mode="REQUIRED"),
        F("fiscal_year_end_month", "INT64"),
        F("sic", "INT64"),
        F("division", "STRING"),
        F("assets_musd", "FLOAT64"),
        F("net_income_musd", "FLOAT64"),
        F("price_close", "FLOAT64"),
        F("auditor_opinion", "INT64"),
    ],
}


def publish(breaches, fundamentals, dataset=None):
    """Replace both tables. Returns {table: rows now in it}."""
    client = database.client()
    name = dataset or database.name()
    # Created only when missing: creating one takes a project-wide permission
    # that Cloud Build's account, which may edit only its own dataset, lacks.
    try:
        client.get_dataset(f"{client.project}.{name}")
    except NotFound:
        client.create_dataset(bigquery.Dataset(f"{client.project}.{name}"))

    counts = {}
    for which, frame in ((database.BREACHES, breaches), (database.FUNDAMENTALS, fundamentals)):
        schema = SCHEMAS[which]
        pandas_gbq.to_gbq(
            frame[[field.name for field in schema]],
            f"{name}.{which}",
            project_id=client.project,
            location=database.LOCATION,
            if_exists="replace",
            table_schema=[field.to_api_repr() for field in schema],
            progress_bar=False,
        )
        counts[which] = client.get_table(database.table_id(which, name)).num_rows
    return counts
