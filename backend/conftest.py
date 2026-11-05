import os

# Every test reads this dataset, which the session rebuilds from the exports
# before anything runs, so the tests never touch 'cyber', the dataset the
# deployed API serves. Set before database is imported, so no .env can point
# the tests anywhere else. Cloud Build tests in 'cyber_ci' (BIGQUERY_TEST_DATASET),
# so a build and a laptop run never rebuild the same tables at once.
TEST_DATASET = os.environ.get("BIGQUERY_TEST_DATASET", "cyber_test")
if TEST_DATASET == "cyber":
    raise RuntimeError("the tests rebuild their dataset; never point them at 'cyber'")
os.environ["BIGQUERY_DATASET"] = TEST_DATASET

import pytest  # noqa: E402

import clean  # noqa: E402
import database  # noqa: E402
import publish  # noqa: E402


@pytest.fixture(scope="session")
def raw():
    """Both exports as delivered, read once from Cloud Storage (or RAW_SOURCE)."""
    return clean.read_raw()


@pytest.fixture(scope="session")
def tables(raw):
    """Both exports, cleaned, and published into the test dataset. A run that
    reads the data starts from the exports, so those tests check the whole path —
    export, cleaning, tables, API — rather than whatever was loaded last. A run
    that never asks for the data never publishes it."""
    assert database.name() == TEST_DATASET
    breaches, fundamentals = clean.usable(*raw)
    publish.publish(breaches, fundamentals)
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
def client(tables):
    from fastapi.testclient import TestClient

    import api

    with TestClient(api.app) as connected:
        yield connected
