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

from metrics import ATTACK_TYPES, DIVISION_NAMES, FIRST_YEAR, LAST_YEAR, REGIONS, VIEWS

SNAPSHOT = Path(__file__).with_name("regression") / "answers.json"


def requests():
    paths = ["/health", "/views", "/sheet"]

    for view in VIEWS:
        paths.append(f"/sheet?view={view}")
        paths.append(f"/sheet?view={view}&since=2018&until=2023")
        paths.append(f"/sheet?view={view}&sec=true")
        paths.append(f"/sheet?view={view}&sec=false")
        for industry in DIVISION_NAMES:
            paths.append(f"/sheet?view={view}&industry={industry}")
        for attack in ATTACK_TYPES:
            paths.append(f"/sheet?view={view}&attack={attack}")
        for region in REGIONS:
            paths.append(f"/sheet?view={view}&region={region}")
    for year in range(FIRST_YEAR, LAST_YEAR + 1):
        paths.append(f"/sheet?since={year}&until={year}")
    for limit, per in ((1, 1), (5, 2), (50, 5), (200, 50)):
        paths.append(f"/sheet?view=before_after&limit={limit}&per={per}")
    paths.append("/sheet?view=industry_by_attack&attack=Ransomware,Phishing&accessed=SSN")
    paths.append("/sheet?view=attack_by_year&information=Financial&relationship=Subsidiary / Affiliate")
    paths.append("/sheet?view=attack_by_year&state=CA,NY,TX&market=NYSE")

    for company in ("walmart", "sam's club", "target", "equifax", "%%", "zz-no-such-company"):
        paths.append(f"/breaches?company={company}")
    for ticker in ("WMT", "TGT", "EFX"):
        paths.append(f"/breaches?ticker={ticker}&limit=50")
    paths.append("/breaches?attack=Ransomware&since=2023&limit=50")

    # Rejections: the assistant reads these out loud, so their wording is part
    # of the contract too.
    paths += [
        "/sheet?view=pie_chart",
        "/sheet?attack=Hacking",
        "/sheet?industry=Atlantis",
        f"/sheet?since={FIRST_YEAR - 1}",
        f"/sheet?until={LAST_YEAR + 1}",
        "/sheet?since=2020&until=2010",
        "/sheet?state=Virginia",
        "/sheet?limit=0",
        "/sheet?limit=500",
        "/sheet?bogus=1",
        "/sheet?industry=Mining&attack=Credential Stuffing",
        "/breaches?company=x",
        "/breaches?limit=51",
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
