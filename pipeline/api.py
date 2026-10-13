from datetime import date
from enum import StrEnum
from typing import Annotated

from fastapi import FastAPI, HTTPException, Query, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse, PlainTextResponse
from google.cloud.bigquery import ArrayQueryParameter, ScalarQueryParameter
from pydantic import BaseModel, Field, field_validator, model_validator

import database
import metros
import sheetcsv
from metrics import (
    ATTACK_TYPES,
    BREACH_LIMIT,
    BREACH_LIMIT_MAXIMUM,
    DIVISION_NAMES,
    FIRST_YEAR,
    INFORMATION_ACCESSED,
    INFORMATION_TYPES,
    LAST_YEAR,
    MARKETS,
    METRICS,
    OTHER_METRO_COLOR,
    REGION_COLORS,
    RELATIONSHIPS,
    US,
    US_REGIONS,
    region_title,
)


def named(name, values):
    return StrEnum(name, {value: value for value in values})


# Every filter that names a category takes only the names metrics.py lists, so
# nothing a caller typed reaches a query except as a checked parameter value.
DivisionName = named("DivisionName", DIVISION_NAMES)
AttackType = named("AttackType", ATTACK_TYPES)
InformationType = named("InformationType", INFORMATION_TYPES)
Accessed = named("Accessed", INFORMATION_ACCESSED)
Relationship = named("Relationship", RELATIONSHIPS)
RegionName = named("RegionName", US_REGIONS)
MarketName = named("MarketName", MARKETS)

# The names each filter takes, which a rejection lists so the assistant can say
# what would have worked.
VOCABULARY = {
    "industry": DIVISION_NAMES,
    "attack": ATTACK_TYPES,
    "information": INFORMATION_TYPES,
    "accessed": INFORMATION_ACCESSED,
    "relationship": RELATIONSHIPS,
    "region": US_REGIONS,
    "market": MARKETS,
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
    """Which breaches count, of those headquartered in the United States: the
    app shows no other country. Every filter narrows. A filter that takes
    names takes several, and a breach passes when it matches any of them."""

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
        clauses = ["b.country = @us", "b.year BETWEEN @since AND @until"]
        parameters = [ScalarQueryParameter("us", "STRING", US),
                      ScalarQueryParameter("since", "INT64", self.since),
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
        """The filters in words."""
        named_filters = [f"{name} {', '.join(str(v) for v in values)}"
                         for name in FILTER_COLUMNS if (values := getattr(self, name))]
        if self.sec is not None:
            named_filters.append("disclosed to the SEC" if self.sec else "not disclosed to the SEC")
        span = f"{self.since}" if self.since == self.until else f"{self.since}-{self.until}"
        return f"in {span}" + (f" with {'; '.join(named_filters)}" if named_filters else "")


class SheetQuery(BaseModel):
    """One region's sheet, or with no region the whole United States'. It takes
    no filters: a bar's height is its place among every US breach, so a sheet
    of fewer breaches would measure them against the same whole."""

    model_config = {"extra": "forbid"}

    region: RegionName | None = None


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


class Region(BaseModel):
    region: RegionName
    title: str
    color: str
    breaches: int


class City(BaseModel):
    city: str
    region: RegionName
    metro: str
    color: str
    lat: float
    lon: float
    breaches: int


class BreachMap(BaseModel):
    regions: list[Region]
    cities: list[City]


@app.get("/health", response_model=Health)
def health():
    # Table metadata rather than a query: free, and it answers whether the
    # service can reach its data.
    client = database.client()
    rows = {which: client.get_table(database.table_id(which)).num_rows
            for which in (database.BREACHES, database.FUNDAMENTALS)}
    return {"status": "ok", "breaches": rows[database.BREACHES],
            "company_years": rows[database.FUNDAMENTALS]}


def us_breaches():
    """Every US breach, with what the maps and sheets draw of it. One query
    serves both: the whole country is twelve hundred rows, and a bar's height
    and a metro's colour are both measured against all of it."""
    figures = ", ".join(f"b.{column}" for column in METRICS)
    return database.query(f"""
        SELECT b.breach_key, b.company, b.ticker, b.disclosed_on, b.region,
               b.city, b.lat, b.lon, b.metro, {figures}
        FROM {database.table(database.BREACHES)} AS b
        WHERE b.country = @us
    """, [ScalarQueryParameter("us", "STRING", US)])


def metro_colors(rows):
    """{metro: colour} for every metro the rows hold; black for the metros no
    region has a colour left for."""
    counts = {}
    for row in rows:
        if row["metro"] is not None:
            key = (row["metro"], row["region"])
            counts[key] = counts.get(key, 0) + 1
    return {metro: color or OTHER_METRO_COLOR for metro, color in metros.colors(counts).items()}


def ranks(rows, column):
    """{breach_key: its figure's place among every breach that reports one}, in
    (0, 1]: the share of them at or below it, so the largest is 1 and equal
    figures stand equally tall. A breach without the figure has no place."""
    values = sorted(row[column] for row in rows if row[column] is not None)
    if not values:
        return {}
    at_or_below = {}
    for i, value in enumerate(values, 1):
        at_or_below[value] = i
    return {row["breach_key"]: at_or_below[row[column]] / len(values)
            for row in rows if row[column] is not None}


def breach_label(row):
    """What a breach's row is called: its company's ticker, or its name where
    it has none, and the day it was disclosed, which tells one company's
    breaches apart."""
    return f"{row['ticker'] or row['company']} ({row['disclosed_on'].isoformat()})"


def region_sheet(rows, region):
    """One row per breach in the region, its six figures and their heights;
    with region None, every US breach. Within a region rows run metro by
    metro, largest metro first, so a metro's bars stand together; the black
    metros follow. Within a metro they run city by city, so lighting one pin's
    breaches lights one block, and within a city oldest first.

    The national sheet runs region by region, west to east, each as its own
    sheet runs, and is coloured as the national map is: a row's category and
    colour are its region's, so the regions' bars match their pins. None when
    there is no breach to draw."""
    placed = {column: ranks(rows, column) for column in METRICS}
    color = metro_colors(rows)
    here = [row for row in rows if region is None or row["region"] == region]
    if not here:
        return None

    size = {}
    for row in here:
        size[row["metro"]] = size.get(row["metro"], 0) + 1

    def order(row):
        metro = row["metro"]
        grey = metro is None or color[metro] == OTHER_METRO_COLOR
        return (US_REGIONS.index(row["region"]), grey, -size[metro], metro or "", row["city"] or "",
                row["disclosed_on"], row["breach_key"])

    def category(row):
        if region is None:
            return region_title(row["region"]), REGION_COLORS[row["region"]]
        return row["metro"], color.get(row["metro"], OTHER_METRO_COLOR)

    lines = []
    for row in sorted(here, key=order):
        name, shade = category(row)
        lines.append((breach_label(row), name, row["city"], shade,
                      [row[column] for column in METRICS],
                      [placed[column].get(row["breach_key"]) for column in METRICS]))
    return sheetcsv.render("Breach / Figure", list(METRICS.values()), lines)


@app.get("/sheet", response_class=PlainTextResponse)
def sheet(query: Annotated[SheetQuery, Query()]):
    region = query.region.value if query.region else None
    text = region_sheet(us_breaches(), region)
    if text is None:
        raise HTTPException(404, f"no breaches in {region_title(region) if region else 'the United States'}")
    return text


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


def breach_map(rows):
    """The regions, west to east, and every city with a breach: its region, its
    metro, and the colour its pin and its bars share. The national map colours
    a pin by its region instead, which the regions carry. A breach the export
    gave no city counts in its region but has no pin."""
    color = metro_colors(rows)
    regions = {region: 0 for region in US_REGIONS}
    cities = {}
    for row in rows:
        regions[row["region"]] += 1
        if row["city"] is None:
            continue
        city = cities.setdefault(row["city"], {
            "city": row["city"], "region": row["region"], "metro": row["metro"],
            "color": color[row["metro"]], "lat": row["lat"], "lon": row["lon"], "breaches": 0})
        city["breaches"] += 1
    return {
        "regions": [{"region": region, "title": region_title(region), "color": REGION_COLORS[region],
                     "breaches": n} for region, n in regions.items()],
        "cities": sorted(cities.values(), key=lambda c: (-c["breaches"], c["city"])),
    }


@app.get("/map", response_model=BreachMap)
def us_map():
    return breach_map(us_breaches())
