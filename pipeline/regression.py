"""The API's answers, recorded, so a later API can be held to them.

tests/test_regression.py sends every request below and fails on any answer
that differs from the recording by a single byte, status included. The Unity
app only ever sees these responses, so while they match, a change underneath —
a new database, a new host, a rewritten pipeline — is invisible to it.

    python regression.py            record the current API's answers

Record before a change, change, run pytest. The recording holds real data, so
it is kept out of the repository (regression/answers.json is git-ignored), and
without one the regression tests skip rather than fail.
"""

import json
import sys
import urllib.error
import urllib.request
from pathlib import Path

from metrics import ATTACK_TYPES, DIVISION_NAMES, FIRST_YEAR, LAST_YEAR, US_REGIONS

SNAPSHOT = Path(__file__).with_name("regression") / "answers.json"


def requests():
    paths = ["/health", "/map"]

    for region in US_REGIONS:
        paths.append(f"/sheet?region={region}")

    for company in ("walmart", "sam's club", "target", "equifax", "%%", "zz-no-such-company"):
        paths.append(f"/breaches?company={company}")
    for ticker in ("WMT", "TGT", "EFX"):
        paths.append(f"/breaches?ticker={ticker}&limit=50")
    paths.append("/breaches?attack=Ransomware&since=2023&limit=50")
    for industry in DIVISION_NAMES:
        paths.append(f"/breaches?industry={industry}&limit=50")
    for attack in ATTACK_TYPES:
        paths.append(f"/breaches?attack={attack}&limit=50")
    for region in US_REGIONS:
        paths.append(f"/breaches?region={region}&limit=50")
    for year in range(FIRST_YEAR, LAST_YEAR + 1):
        paths.append(f"/breaches?since={year}&until={year}&limit=50")
    paths.append("/breaches?state=CA,NY,TX&market=NYSE&limit=50")
    paths.append("/breaches?information=Financial&relationship=Subsidiary / Affiliate&limit=50")

    # Rejections: the assistant reads these out loud, so their wording is part
    # of the contract too.
    paths += [
        "/sheet",
        "/sheet?region=Canada",
        "/sheet?region=US West&since=2020",
        "/breaches?attack=Hacking",
        "/breaches?industry=Atlantis",
        f"/breaches?since={FIRST_YEAR - 1}",
        f"/breaches?until={LAST_YEAR + 1}",
        "/breaches?since=2020&until=2010",
        "/breaches?state=Virginia",
        "/breaches?company=x",
        "/breaches?limit=51",
        "/breaches?country=Japan",
    ]
    return paths


def fetch(url, key, path):
    """One request sent over the network to a deployed API, as [status, body].
    Only spaces are escaped, as the test client escapes them, so both see the
    same URL."""
    request = urllib.request.Request(url.rstrip("/") + path.replace(" ", "%20"),
                                     headers={"X-Api-Key": key} if key else {})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return [response.status, response.read().decode()]
    except urllib.error.HTTPError as refused:
        return [refused.code, refused.read().decode()]


def load():
    """The recorded answers, or None when nothing has been recorded."""
    if not SNAPSHOT.exists():
        return None
    return json.loads(SNAPSHOT.read_text())


def record():
    from fastapi.testclient import TestClient

    import api

    with TestClient(api.app) as client:
        answers = {}
        for path in requests():
            response = client.get(path)
            answers[path] = [response.status_code, response.text]

    SNAPSHOT.parent.mkdir(exist_ok=True)
    SNAPSHOT.write_text(json.dumps(answers, indent=0))
    print(f"recorded {len(answers)} answers to {SNAPSHOT}")
    return 0


if __name__ == "__main__":
    sys.exit(record())
