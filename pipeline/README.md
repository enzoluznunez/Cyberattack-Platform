# pipeline

Turns the two raw exports into the tables the app draws from, and serves them.

The exports live in a private Cloud Storage bucket; `rebuild.py` turns them
into two BigQuery tables, and the API serves sheets drawn from them. Everything
runs in the Google Cloud project `cyberattack-platform`, region `us-east1`.
Sign in once with:

```sh
gcloud auth application-default login
```

There is no password to configure. `.env.example` shows the few settings you
can change; copy it to `.env` (git ignores it) only if you need to.

Set up a virtual environment with Python 3.13:

```sh
python3.13 -m venv .venv
.venv/bin/pip install -r requirements-dev.txt
```

## The exports

`gs://cyberattack-platform-raw` holds both, verbatim:

- `breach_events.csv` — one row per disclosed breach, 2004–2024
- `company_fundamentals.csv` — one row per company per year, 2004–2023

The bucket is private and versioned, so replacing a file keeps the old one.
To replace an export:

```sh
gcloud storage cp breach_events.csv gs://cyberattack-platform-raw/
python rebuild.py
```

Local copies can sit in `pipeline/data/`, which git ignores.

## Rebuilding the data

```sh
python rebuild.py                    # the bucket -> the 'cyber' dataset
python rebuild.py --source data      # local copies instead of the bucket
python rebuild.py --dataset scratch  # into another dataset
```

One command, and nothing kept in between: every run starts from the exports.
`clean.py` owns what the tables hold, `publish.py` writes them, and
`rebuild.py` runs the two in order. Two tables come out:

- `breaches` — one row per breach. Attack types, information accessed, breached
  subsidiaries and parent/subsidiary are real lists, dates are dates, and every
  breach carries the industry (`division`) of its company.
- `fundamentals` — one row per company per year: assets and net income (in
  millions of dollars, as the export reports them), share price and auditor
  opinion. The export repeats 2,165 company-years; the fuller row is kept.

A company has one industry in both tables, from the breach export's SEC SIC
code, falling back to the fundamentals export's.

Cleaning refuses an export it does not understand — an attack type
`metrics.py` does not name, a breach outside the year range, two equally full
rows for one company-year that disagree — rather than loading it.

## Serving the app

The app ships no data. It asks `/views` at startup for the sheets that exist,
and draws each one from `/sheet`:

| Endpoint | What it answers |
|---|---|
| `/health` | Whether the service is up, and how many rows each table holds. Open without a key at the gateway. |
| `/views` | The sheets, and the names every filter takes |
| `/sheet?view=…` | One sheet, as the CSV the app parses |
| `/breaches` | Individual breaches, newest first, for the assistant to read out |

The three sheets are `attack_by_year` (the default), `industry_by_attack` and
`before_after`. Every sheet and `/breaches` take the same filters: `since`,
`until`, `industry`, `attack`, `information`, `accessed`, `relationship`,
`region`, `market`, `state` and `sec`. Most take several names separated by
commas; repeat `industry` instead, since one industry's name holds commas.

To run it on a laptop:

```sh
uvicorn api:app --host 0.0.0.0 --port 8000
```

Bind `0.0.0.0` rather than localhost: the request comes from a headset on the
same network, and the address it uses is the one line in
`Assets/StreamingAssets/cloud.url`.

## Deploying to Google Cloud

```
headset --(Google Cloud API key)--> API Gateway --(cyber-gateway-1)--> Cloud Run --> BigQuery
```

The same `api.py` runs on Cloud Run as the service `cyber-api`, which is
private: only API Gateway may call it. Callers use the gateway, at
`https://cyber-gateway-6z6s1gve.ue.gateway.dev`, sending a Google Cloud API key
in `X-Api-Key`. Only `/health` answers without one.

```sh
./deploy.sh
```

Cloud Build builds the image from the `Dockerfile` (`.gcloudignore` uploads
only the API's own files) and Cloud Run serves it. The script also keeps in
place, and leaves alone once there:

- the service account `cyber-api`, which may run BigQuery queries and read the
  `cyber` dataset, and nothing else
- the gateway's service account `cyber-gateway-1` as the only one allowed to
  invoke the service
- the gateway `cyber-gateway` in `us-east1`, serving the API `cyber` from
  `gateway.json`. A gateway config cannot be edited, so each version of the
  file becomes a config named after its contents, and the gateway moves to it
  only when the file changed.

The gateway forwards only the paths `gateway.json` lists. It is generated from
`api.py`'s own routes, and `pytest` fails if it is stale:

```sh
python gateway.py --check
```

The key itself is made in the console (APIs & Services → Credentials), as an
ordinary API key restricted to the API `cyber` — not bound to a service
account, which Google allows only for Gemini and Vertex — and goes in the
headset's `Assets/StreamingAssets/cloud.key`. Disabling or deleting it there
cuts the headset off without a redeploy.

Requests time out after nine seconds at both the gateway and Cloud Run, under
the headset's own ten, and at most two instances run. To hold the deployed API
to the regression recording:

```sh
REGRESSION_URL=https://cyber-gateway-6z6s1gve.ue.gateway.dev \
REGRESSION_KEY="$(cat ../Assets/StreamingAssets/cloud.key)" pytest tests/test_regression.py
```

## CI/CD

Cloud Build runs `cloudbuild.yaml` (at the repository root) on every push to
GitHub, as the service account `cyber-ci`:

```
any branch:  pytest
main:        pytest -> docker build -> push to Artifact Registry -> deploy to Cloud Run -> /health through the gateway
```

Two triggers, `cyber-test` and `cyber-deploy`, share the file and differ only in
`_DEPLOY`. A failing test stops the build, so nothing untested reaches Cloud
Run. The image is tagged with the commit it was built from.

Builds test in their own dataset, `cyber_ci`, so one never rebuilds the tables
a laptop run is reading. `cyber-ci` may rebuild that dataset, read the
exports, and ship new revisions of `cyber-api` — not read `cyber`, change IAM
or touch the gateway. The one-time setup above and changes to `gateway.json`
still go through `./deploy.sh`; a build fails if `gateway.json` is stale, which
is the reminder to run it.

To run the tests on Cloud Build by hand, from the repository root:

```sh
gcloud builds submit --region=us-east1 \
    --service-account=projects/cyberattack-platform/serviceAccounts/cyber-ci@cyberattack-platform.iam.gserviceaccount.com
```

## The one list

`metrics.py` owns the names: the attack types and every other category the
filters take, the year range, the sheets, the before/after offsets, and the
SIC boundaries the industries are cut on, with their colours. `clean.py`,
`publish.py` and `api.py` all read that one file, so they cannot disagree
about what an industry or an attack type is.

## Generated artifacts

One file is generated outside this directory and checked in, so the C# side
cannot drift from the API:

```sh
python codegen.py --check      # Assets/Source/Gemini/Tools/CyberContract.g.cs
```

## Tests

```sh
pytest
```

A run whose tests read the data first rebuilds a separate dataset, `cyber_test`
(`cyber_ci` on Cloud Build), from the exports, and every test reads that one — never `cyber`, which
the API serves. So a run checks the whole path: exports, cleaning, tables and
API. Each sheet is checked cell by cell against the same count done in pandas.
Needs the sign-in above.

- **Unit** — cleaning rules, the sheet CSV format, the one list, the generated files.
- **Integration** — every endpoint through FastAPI's test client, against `cyber_test`.
- **Regression** — `tests/test_regression.py` sends 141 requests and fails on
  any answer that differs by a byte from a recording, one test per request.
  Record before a change with `python regression.py`; the recording holds real
  data, so it lives in the git-ignored `regression/`, and the tests skip
  without it. Set `REGRESSION_URL` and `REGRESSION_KEY` to hold a deployed API
  to the same recording.
