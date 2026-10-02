"""Rebuild the tables the API serves, from the exports, in one command.

    python rebuild.py                       the exports in Cloud Storage -> the 'cyber' dataset
    python rebuild.py --dataset cyber_test  the same, into a scratch dataset
    python rebuild.py --source data         local copies of the exports instead

The exports are read (clean.read_raw), typed (clean.usable) and published as
two tables (publish.publish). Nothing is kept in between: every run starts from
the exports, so the tables are always what the exports and this code say.

Needs `gcloud auth application-default login` on a laptop.
"""

import argparse
import sys

import clean
import database
import publish


def build(source=clean.RAW_SOURCE):
    """The exports -> (breaches, fundamentals), without writing anything."""
    return clean.usable(*clean.read_raw(source))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", default=clean.RAW_SOURCE,
                        help="gs://bucket or a local directory holding both exports (default: %(default)s)")
    parser.add_argument("--dataset", default=None,
                        help="dataset to publish into (default: BIGQUERY_DATASET or cyber)")
    args = parser.parse_args()

    breaches, fundamentals = build(args.source)
    counts = publish.publish(breaches, fundamentals, args.dataset)

    name = args.dataset or database.name()
    print(f"{args.source} -> {database.client().project}.{name}: "
          + ", ".join(f"{table} {rows} rows" for table, rows in counts.items()))
    return 0


if __name__ == "__main__":
    sys.exit(main())
