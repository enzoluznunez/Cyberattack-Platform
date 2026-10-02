"""The gateway forwards only the paths gateway.json lists, so these fail if an
endpoint api.py serves would answer 404 behind it, or one would be open that
should need a key."""

import json

import gateway


def test_gateway_description_is_current():
    assert gateway.TARGET.exists(), "run: python gateway.py"
    assert gateway.TARGET.read_text() == gateway.render()


def test_every_endpoint_passes_the_gateway():
    described = json.loads(gateway.TARGET.read_text())["paths"]
    assert set(described) == set(gateway.routes())
    assert {"/health", "/views", "/sheet", "/breaches"} <= set(described)


def test_only_health_is_open():
    described = json.loads(gateway.TARGET.read_text())["paths"]
    open_paths = {path for path, item in described.items() if "security" not in item["get"]}
    assert open_paths == gateway.OPEN_PATHS


def test_the_docs_stay_internal():
    described = json.loads(gateway.TARGET.read_text())["paths"]
    assert not {"/docs", "/openapi.json", "/redoc"} & set(described)
