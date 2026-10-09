from datetime import date
from enum import StrEnum
from typing import Annotated

from fastapi import FastAPI, HTTPException, Query, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse, PlainTextResponse
from google.cloud.bigquery import ArrayQueryParameter, ScalarQueryParameter
from pydantic import BaseModel, Field, field_validator, model_validator

import database
import sheetcsv
from metrics import (
    ATTACK_TYPES,
    BREACH_LIMIT,
    BREACH_LIMIT_MAXIMUM,
    COUNTRIES,
    DEFAULT_VIEW,
    DIVISION_NAMES,
    FIRST_YEAR,
    FUNDAMENTALS,
    INFORMATION_ACCESSED,
    INFORMATION_TYPES,
    LAST_YEAR,
    LIMIT_MAXIMUM,
    LIMIT_MINIMUM,
    MARKETS,
    OFFSETS,
    REGIONS,
    RELATIONSHIPS,
    SHEET_LIMIT,
    SHEET_PER,
    VIEWS,
)


def named(name, values):
    return StrEnum(name, {value: value for value in values})


# Every filter that names a category takes only the names metrics.py lists, so
# nothing a caller typed reaches a query except as a checked parameter value.
ViewName = named("ViewName", VIEWS)
DivisionName = named("DivisionName", DIVISION_NAMES)
AttackType = named("AttackType", ATTACK_TYPES)
InformationType = named("InformationType", INFORMATION_TYPES)
Accessed = named("Accessed", INFORMATION_ACCESSED)
Relationship = named("Relationship", RELATIONSHIPS)
RegionName = named("RegionName", REGIONS)
MarketName = named("MarketName", MARKETS)
CountryName = named("CountryName", COUNTRIES)

# The names each filter takes, which a rejection lists so the assistant can say
# what would have worked.
VOCABULARY = {
    "view": list(VIEWS),
    "industry": DIVISION_NAMES,
    "attack": ATTACK_TYPES,
    "information": INFORMATION_TYPES,
    "accessed": INFORMATION_ACCESSED,
    "relationship": RELATIONSHIPS,
    "region": REGIONS,
    "market": MARKETS,
    "country": list(COUNTRIES),
}

# Every filter on a column, in the order a description lists them: the column it
# tests, and whether that column holds a list of names (a breach passes when any
# of them is named) or a single one.
FILTER_COLUMNS = {
    "industry": ("b.division", False),
    "attack": ("b.attack_types", True),
    "information": ("b.information_type", False),
    "accessed": ("b.information_accessed", True),
    "relationship": ("b.target_relationships", True),
    "region": ("b.region", False),
    "market": ("b.market", False),
    "state": ("b.state", False),
    "country": ("b.country", False),
}

# Nothing here checks who is asking. Deployed, the service is private: Cloud
# Run lets in only API Gateway's service account, and the gateway lets in only
# callers with a Google Cloud API key restricted to this API (gateway.json says
# which paths need one). On a laptop and in the tests there is no gateway, and
# nothing is asked for.
app = FastAPI(title="Cyberattack Platform")


# Pydantic reports a list of structured errors; the voice client reads 'detail'
# as one string, so flatten it into a sentence the model can say out loud.
@app.exception_handler(RequestValidationError)
async def readable_validation_error(request: Request, exc: RequestValidationError):
    parts = []
    for error in exc.errors():
        path = [p for p in error["loc"] if p not in ("query", "body", "path")]
        where = str(path[0]) if path else ""
        given = error.get("input")
        if error["type"] == "enum":
            names = VOCABULARY.get(where)
            message = (f"{given!r} is not one of: {', '.join(names)}" if names
                       else f"{given!r} is not one of the names this parameter takes")
        else:
            # A ValueError raised in a validator arrives as "Value error, <text>".
            # The assistant reads this out loud, so drop the machinery.
            message = error["msg"].removeprefix("Value error, ")
            if given is not None and not isinstance(given, (list, dict)):
                message = f"{message} (got {given!r})"
        parts.append(f"{where}: {message}" if where else message)
    return JSONResponse(status_code=422, content={"detail": "; ".join(parts)})


class Filters(BaseModel):
    """Which breaches count. Every filter narrows; none changes what a sheet's
    rows and columns mean. A filter that takes names takes several, and a
    breach passes when it matches any of them."""

    model_config = {"extra": "forbid"}

    # The years breaches were disclosed in, inclusive.
    since: Annotated[int, Field(ge=FIRST_YEAR, le=LAST_YEAR)] = FIRST_YEAR
    until: Annotated[int, Field(ge=FIRST_YEAR, le=LAST_YEAR)] = LAST_YEAR

    # Industry is never split on commas: 'Finance, Insurance, Real Estate' is
    # one name. Repeat the parameter to name several.
    industry: list[DivisionName] = []
    attack: list[AttackType] = []
    information: list[InformationType] = []
    accessed: list[Accessed] = []
    relationship: list[Relationship] = []
    region: list[RegionName] = []
    market: list[MarketName] = []
    state: list[str] = []
    country: list[CountryName] = []
    sec: bool | None = None

    # Callers send these as one comma-separated value; FastAPI hands a list only
    # when the parameter is repeated. Accept both and flatten.
    @field_validator(*(name for name in FILTER_COLUMNS if name != "industry"), mode="before")
    @classmethod
    def split_commas(cls, value):
        if value is None:
            return value
        items = value if isinstance(value, (list, tuple)) else [value]
        flattened = []
        for item in items:
            if isinstance(item, str):
                flattened.extend(part.strip() for part in item.split(",") if part.strip())
            else:
                flattened.append(item)
        return flattened

    @field_validator("state")
    @classmethod
    def state_codes(cls, value):
        for code in value:
            if len(code) != 2 or not code.isalnum():
                raise ValueError(f"{code!r} is not a two-letter state code, like VA")
        return [code.upper() for code in value]

    @model_validator(mode="after")
    def years_in_order(self):
        if self.since > self.until:
            raise ValueError(f"since ({self.since}) is after until ({self.until})")
        return self

    @property
    def years(self):
        return list(range(self.since, self.until + 1))

    def where(self):
        """(SQL condition on breaches aliased b, its parameters). Every value
        travels as a parameter; the field names come from this class, never
        from the request."""
        clauses = ["b.year BETWEEN @since AND @until"]
        parameters = [ScalarQueryParameter("since", "INT64", self.since),
                      ScalarQueryParameter("until", "INT64", self.until)]
        for name, (column, holds_list) in FILTER_COLUMNS.items():
            values = getattr(self, name)
            if values:
                clauses.append(f"EXISTS (SELECT 1 FROM UNNEST({column}) AS item WHERE item IN UNNEST(@{name}))"
                               if holds_list else f"{column} IN UNNEST(@{name})")
                parameters.append(ArrayQueryParameter(name, "STRING", [str(v) for v in values]))
        if self.sec is not None:
            clauses.append("b.disclosed_to_sec = @sec")
            parameters.append(ScalarQueryParameter("sec", "BOOL", self.sec))
        return " AND ".join(clauses), parameters

    def describe(self):
        """The filters in words, for a sheet that came back empty."""
        named_filters = [f"{name} {', '.join(str(v) for v in values)}"
                         for name in FILTER_COLUMNS if (values := getattr(self, name))]
        if self.sec is not None:
            named_filters.append("disclosed to the SEC" if self.sec else "not disclosed to the SEC")
        span = f"{self.since}" if self.since == self.until else f"{self.since}-{self.until}"
        return f"in {span}" + (f" with {'; '.join(named_filters)}" if named_filters else "")


class SheetQuery(Filters):
    view: ViewName = ViewName(DEFAULT_VIEW)

    # Only the before/after sheet has a row per company, so only it is capped.
    limit: Annotated[int, Field(ge=LIMIT_MINIMUM, le=LIMIT_MAXIMUM)] = SHEET_LIMIT
    # How many companies each industry contributes when no industry is named.
    per: Annotated[int, Field(ge=1, le=LIMIT_MAXIMUM)] = SHEET_PER


class BreachQuery(Filters):
    # Part of a company's name, or of a breached subsidiary's ('Sam's Club'),
    # in any case.
    company: Annotated[str | None, Field(min_length=2, max_length=100)] = None
    ticker: Annotated[str | None, Field(min_length=1, max_length=10)] = None
    limit: Annotated[int, Field(ge=1, le=BREACH_LIMIT_MAXIMUM)] = BREACH_LIMIT


class Health(BaseModel):
    status: str
    breaches: int
    company_years: int


class View(BaseModel):
    view: ViewName
    title: str
    rows: str
    columns: str
    measure: str
    description: str


class ViewCatalog(BaseModel):
    default: ViewName
    views: list[View]
    first_year: int
    last_year: int
    filters: dict[str, list[str]]


class Breach(BaseModel):
    breach_key: int
    company: str
    targets: list[str]
    ticker: str | None
    industry: str | None
    region: str | None
    state: str | None
    country: str
    city: str | None
    year: int
    disclosed_on: date
    discovered_on: date | None
    started_on: date | None
    ended_on: date | None
    attack_types: list[str]
    information_type: str | None
    information_accessed: list[str]
    records_lost: int | None
    cost_usd: float | None
    disclosed_to_sec: bool
    filing_type: str | None
    filing_url: str | None
    auditor: str | None


class BreachList(BaseModel):
    total: int
    breaches: list[Breach]


class City(BaseModel):
    city: str
    lat: float
    lon: float
    breaches: int


class Country(BaseModel):
    country: CountryName
    code: str
    breaches: int
    cities: list[City]


class BreachMap(BaseModel):
    countries: list[Country]


@app.get("/health", response_model=Health)
def health():
    # Table metadata rather than a query: free, and it answers whether the
    # service can reach its data.
    client = database.client()
    rows = {which: client.get_table(database.table_id(which)).num_rows
            for which in (database.BREACHES, database.FUNDAMENTALS)}
    return {"status": "ok", "breaches": rows[database.BREACHES],
            "company_years": rows[database.FUNDAMENTALS]}


@app.get("/views", response_model=ViewCatalog)
def views():
    """The sheets this API draws and the names every filter takes. This is the
    request the app makes at startup to know what it can open."""
    return {
        "default": DEFAULT_VIEW,
        "views": [{"view": view, **about} for view, about in VIEWS.items()],
        "first_year": FIRST_YEAR,
        "last_year": LAST_YEAR,
        "filters": {name: list(values) for name, values in VOCABULARY.items() if name != "view"},
    }


class EmptySheet(Exception):
    """No breach matched. The endpoint turns this into a 404, which the app
    reports as a sheet it could not open."""


def attack_axis(query):
    """The attack types a sheet with an attack axis shows: the ones named, or
    all of them. A breach with several attack types counts under each one
    shown, so naming one type shows that type alone rather than every type its
    breaches also had."""
    return [str(a) for a in query.attack] if query.attack else ATTACK_TYPES


def counted(query, row_column):
    """Breaches counted by (row, attack type), for the sheets whose cells are
    counts and one of whose axes is attack type."""
    where, parameters = query.where()
    parameters.append(ArrayQueryParameter("shown", "STRING", attack_axis(query)))
    return database.query(f"""
        SELECT {row_column} AS row_key, attack, COUNT(*) AS n
        FROM {database.table(database.BREACHES)} AS b, UNNEST(b.attack_types) AS attack
        WHERE {where} AND {row_column} IS NOT NULL AND attack IN UNNEST(@shown)
        GROUP BY row_key, attack
    """, parameters)


def attack_by_year(query):
    found = counted(query, "b.year")
    counts = {(row["attack"], row["row_key"]): row["n"] for row in found}
    years = query.years
    rows = [(attack, None, [counts.get((attack, year), 0) for year in years])
            for attack in attack_axis(query)
            if any((attack, year) in counts for year in years)]
    if not rows:
        return None
    return sheetcsv.render("Attack Type / Year", [str(year) for year in years], rows)


def industry_by_attack(query):
    found = counted(query, "b.division")
    counts = {(row["row_key"], row["attack"]): row["n"] for row in found}
    attacks = attack_axis(query)
    rows = [(industry, industry, [counts.get((industry, attack), 0) for attack in attacks])
            for industry in DIVISION_NAMES
            if any((industry, attack) in counts for attack in attacks)]
    if not rows:
        return None
    return sheetcsv.render("Industry / Attack Type", attacks, rows, colored=True)


def offset_title(label, offset):
    """'Assets -2' ... 'Assets 0' ... 'Assets +2'. One group's titles share
    'Assets ' and nothing more, which is what the app takes the group's name
    from."""
    return f"{label} {offset:+d}" if offset else f"{label} 0"


def before_after(query):
    """One row per breached company, measured around its first breach that
    passes the filters. The companies are the largest by assets in that year;
    with no industry named, the largest few from each industry, so the sheet is
    a comparison rather than a leaderboard of banks."""
    where, parameters = query.where()
    parameters += [ScalarQueryParameter("limit", "INT64", query.limit),
                   ScalarQueryParameter("per", "INT64", query.per),
                   ScalarQueryParameter("before", "INT64", -min(OFFSETS)),
                   ScalarQueryParameter("after", "INT64", max(OFFSETS))]
    per_industry = ("QUALIFY ROW_NUMBER() OVER (PARTITION BY division "
                    "ORDER BY size DESC NULLS LAST, ticker) <= @per") if not query.industry else ""
    fundamentals = database.table(database.FUNDAMENTALS)
    figures = ", ".join(f"f.{column}" for column in FUNDAMENTALS)

    found = database.query(f"""
        WITH events AS (
            SELECT b.cik, MIN(b.year) AS year
            FROM {database.table(database.BREACHES)} AS b
            WHERE {where}
            GROUP BY b.cik
        ),
        names AS (
            SELECT cik, ANY_VALUE(ticker) AS ticker, ANY_VALUE(division) AS division
            FROM {fundamentals}
            GROUP BY cik
        ),
        sized AS (
            SELECT e.cik, e.year, n.ticker, n.division, f.assets_musd AS size
            FROM events AS e
            JOIN names AS n USING (cik)
            LEFT JOIN {fundamentals} AS f ON f.cik = e.cik AND f.year = e.year
            WHERE TRUE
            {per_industry}
        ),
        chosen AS (
            SELECT * FROM sized
            ORDER BY size DESC NULLS LAST, ticker
            LIMIT @limit
        )
        SELECT c.ticker, c.division, f.year - c.year AS offset, {figures}
        FROM chosen AS c
        LEFT JOIN {fundamentals} AS f
          ON f.cik = c.cik AND f.year BETWEEN c.year - @before AND c.year + @after
        ORDER BY c.ticker
    """, parameters)
    if not found:
        return None

    companies = {}
    for row in found:
        company = companies.setdefault(row["ticker"], {"industry": row["division"], "figures": {}})
        if row["offset"] is not None:
            company["figures"][row["offset"]] = row

    columns = [(column, offset) for column in FUNDAMENTALS for offset in OFFSETS]
    rows = [(ticker, company["industry"],
             [company["figures"][offset][column] if offset in company["figures"] else None
              for column, offset in columns])
            for ticker, company in companies.items()]
    return sheetcsv.render("Company / Years from Breach",
                           [offset_title(FUNDAMENTALS[column], offset) for column, offset in columns],
                           rows, group=len(OFFSETS), colored=True)


SHEETS = {
    "attack_by_year": attack_by_year,
    "industry_by_attack": industry_by_attack,
    "before_after": before_after,
}


def sheet_csv(query):
    """One sheet, as the CSV the app parses. Every sheet the app draws comes
    through here, so the rows it shows are the tables as they stand. A sheet
    with no rows comes back None."""
    text = SHEETS[query.view.value](query)
    if text is None:
        raise EmptySheet(f"no breaches {query.describe()}")
    return text


@app.get("/sheet", response_class=PlainTextResponse)
def sheet(query: Annotated[SheetQuery, Query()]):
    try:
        return sheet_csv(query)
    except EmptySheet as empty:
        raise HTTPException(404, str(empty)) from None


def like(text):
    """A LIKE pattern matching text anywhere, with LIKE's own wildcards in it
    taken literally."""
    escaped = text.lower().replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_")
    return f"%{escaped}%"


@app.get("/breaches", response_model=BreachList)
def breaches(query: Annotated[BreachQuery, Query()]):
    """Individual breaches, newest first: the detail no sheet draws — who, when,
    what was taken, and where it was reported. A search that finds nothing is
    an answer, not an error."""
    where, parameters = query.where()
    if query.company:
        where += (" AND (LOWER(b.company) LIKE @company"
                  " OR EXISTS (SELECT 1 FROM UNNEST(b.targets) AS t WHERE LOWER(t) LIKE @company))")
        parameters.append(ScalarQueryParameter("company", "STRING", like(query.company)))
    if query.ticker:
        where += " AND b.ticker = @ticker"
        parameters.append(ScalarQueryParameter("ticker", "STRING", query.ticker.upper()))
    parameters.append(ScalarQueryParameter("limit", "INT64", query.limit))

    found = database.query(f"""
        SELECT b.*, b.division AS industry, COUNT(*) OVER () AS total
        FROM {database.table(database.BREACHES)} AS b
        WHERE {where}
        ORDER BY b.disclosed_on DESC, b.breach_key
        LIMIT @limit
    """, parameters)
    listed = [{field: row[field] for field in Breach.model_fields} for row in found]
    return {"total": found[0]["total"] if found else 0, "breaches": listed}


@app.get("/map", response_model=BreachMap)
def breach_map(query: Annotated[Filters, Query()]):
    """Where the breached companies are headquartered: every country holding a
    breach that passes the filters, most breaches first, each with one dot per
    city, most breaches first. A breach counts once, in its country and in its
    city; the one breach whose city the export left blank counts in its country
    alone. A map of nothing is an answer, not an error."""
    where, parameters = query.where()
    found = database.query(f"""
        SELECT b.country, b.city, b.lat, b.lon, COUNT(*) AS n
        FROM {database.table(database.BREACHES)} AS b
        WHERE {where}
        GROUP BY b.country, b.city, b.lat, b.lon
    """, parameters)

    countries = {}
    for row in found:
        country = countries.setdefault(row["country"], {
            "country": row["country"], "code": COUNTRIES[row["country"]], "breaches": 0, "cities": []})
        country["breaches"] += row["n"]
        if row["city"] is not None:
            country["cities"].append({"city": row["city"], "lat": row["lat"], "lon": row["lon"],
                                      "breaches": row["n"]})
    for country in countries.values():
        country["cities"].sort(key=lambda city: (-city["breaches"], city["city"]))
    return {"countries": sorted(countries.values(), key=lambda c: (-c["breaches"], c["country"]))}
