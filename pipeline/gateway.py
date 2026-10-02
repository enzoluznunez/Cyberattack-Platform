"""Emit the API Gateway's description of the API from api.py's own routes.

API Gateway stands in front of the Cloud Run service and is the only way in:
it checks the caller's Google Cloud API key, then calls the service as its own
service account. It forwards only the paths this file lists, so every route
api.py serves is listed here and test_gateway.py fails if one is missing —
an endpoint added to api.py without a rerun would otherwise answer 404 at the
gateway while working on a laptop.

    python gateway.py            rewrite gateway.json
    python gateway.py --check    exit non-zero if it is stale

The description is OpenAPI 2.0, the version API Gateway has supported longest.
Query parameters are not listed: the gateway passes them through untouched,
and api.py is what checks them.
"""

import argparse
import json
import sys
from pathlib import Path

TARGET = Path(__file__).with_name("gateway.json")

# The Cloud Run service the gateway forwards to.
BACKEND = "https://cyber-api-546782261162.us-east1.run.app"

# What callers send their Google Cloud API key in. The headset already sends
# its key in this header, so nothing about the request changes but the key.
KEY_HEADER = "x-api-key"

# Open without a key: whether the service is up, and nothing about the data
# beyond how many rows it holds.
OPEN_PATHS = {"/health"}


def routes():
    """{path: name} for every path api.py serves to callers. The docs pages
    FastAPI adds are left out, so they stay unreachable from outside."""
    import api

    return {route.path: route.name for route in api.app.routes
            if getattr(route, "include_in_schema", False) and "GET" in getattr(route, "methods", ())}


def render():
    paths = {}
    for path, name in sorted(routes().items()):
        operation = {
            "operationId": name,
            "responses": {"200": {"description": "OK"}},
        }
        if path not in OPEN_PATHS:
            operation["security"] = [{"api_key": []}]
        paths[path] = {"get": operation}

    document = {
        "swagger": "2.0",
        "info": {
            "title": "cyber",
            "description": "The Cyberattack Platform API: breach sheets and lookups, drawn from BigQuery.",
            "version": "1.0.0",
        },
        "schemes": ["https"],
        "produces": ["application/json", "text/plain"],
        # Nine seconds, as Cloud Run's own limit, so a slow request fails at
        # the gateway with a reason rather than at the headset with none.
        "x-google-backend": {
            "address": BACKEND,
            "path_translation": "APPEND_PATH_TO_ADDRESS",
            "deadline": 9.0,
        },
        "securityDefinitions": {
            "api_key": {"type": "apiKey", "name": KEY_HEADER, "in": "header"},
        },
        "paths": paths,
    }
    return json.dumps(document, indent=2) + "\n"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()

    generated = render()
    if args.check:
        current = TARGET.read_text() if TARGET.exists() else ""
        if current != generated:
            print(f"{TARGET} is stale; run: python gateway.py", file=sys.stderr)
            return 1
        print(f"{TARGET} is up to date")
        return 0

    TARGET.write_text(generated)
    print(f"wrote {TARGET}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
