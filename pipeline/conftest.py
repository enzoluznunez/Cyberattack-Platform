import os

# Every test reads this dataset, which the session rebuilds from the exports
# before anything runs, so the tests never touch 'cyber', the dataset the
# deployed API serves. Set before database is imported, so no .env can point
# the tests anywhere else.
TEST_DATASET = "cyber_test"
os.environ["BIGQUERY_DATASET"] = TEST_DATASET

import pandas as pd  # noqa: E402
import pytest  # noqa: E402

import database  # noqa: E402
import publish  # noqa: E402
import rebuild  # noqa: E402


@pytest.fixture(scope="session", autouse=True)
def tables():
    """Both exports, cleaned, and published into the test dataset. Every run
    starts from the exports in Cloud Storage (or RAW_SOURCE), so the tests
    check the whole path — export, cleaning, tables, API — rather than whatever
    was loaded last."""
    assert database.name() == TEST_DATASET
    breaches, fundamentals = rebuild.build()
    publish.publish(breaches, fundamentals, TEST_DATASET)
    return breaches, fundamentals


@pytest.fixture(scope="session")
def breaches(tables):
    return tables[0]


@pytest.fixture(scope="session")
def fundamentals(tables):
    return tables[1]


@pytest.fixture(scope="session")
def attacks(breaches):
    """One row per (breach, attack type): what the counted sheets count."""
    return breaches.explode("attack_types").rename(columns={"attack_types": "attack"})


@pytest.fixture(scope="session")
def client():
    from fastapi.testclient import TestClient

    import api

    with TestClient(api.app) as connected:
        yield connected


def raw_frame(**columns):
    """A one-or-more-row export, every value text, for the cleaning tests."""
    return pd.DataFrame({name: [str(v) for v in values] for name, values in columns.items()})
