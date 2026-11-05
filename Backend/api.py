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
    AMOUNTS,
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
    QUERY_LIMIT,
    QUERY_LIMIT_MAXIMUM,
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
Amount = named("Amount", AMOUNTS)

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

# Filters on a place by its name as /map gives it, in any case. Never split on
# commas: a metro's name holds one ('New York, NY').
PLACE_COLUMNS = {
    "metro": "b.metro",
    "city": "b.city",
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

    # Places by the names /map gives them; repeat the parameter to name several.
    metro: list[str] = []
    city: list[str] = []

    # Part of a company's name, or of a breached subsidiary's ('Sam's Club'),
    # in any case.
    company: Annotated[str | None, Field(min_length=2, max_length=100)] = None
    ticker: Annotated[str | None, Field(min_length=1, max_length=10)] = None

    # Inclusive bounds on an amount (metrics.AMOUNTS), in dollars, or records
    # for records_lost. A breach that does not report the amount fails a bound
    # on it.
    revenue_min: float | None = None
    revenue_max: float | None = None
    net_income_min: float | None = None
    net_income_max: float | None = None
    assets_min: float | None = None
    assets_max: float | None = None
    book_value_min: float | None = None
    book_value_max: float | None = None
    market_cap_min: float | None = None
    market_cap_max: float | None = None
    stock_price_min: float | None = None
    stock_price_max: float | None = None
    records_lost_min: float | None = None
    records_lost_max: float | None = None
    cost_min: float | None = None
    cost_max: float | None = None

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
        for name in AMOUNTS:
            low, high = getattr(self, f"{name}_min"), getattr(self, f"{name}_max")
            if low is not None and high is not None and low > high:
                raise ValueError(f"{name}_min ({low:g}) is above {name}_max ({high:g})")
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
        for name, column in PLACE_COLUMNS.items():
            values = getattr(self, name)
            if values:
                clauses.append(f"LOWER({column}) IN UNNEST(@{name})")
                parameters.append(ArrayQueryParameter(name, "STRING", [v.strip().lower() for v in values]))
        if self.company:
            clauses.append("(LOWER(b.company) LIKE @company"
                           " OR EXISTS (SELECT 1 FROM UNNEST(b.targets) AS t WHERE LOWER(t) LIKE @company))")
            parameters.append(ScalarQueryParameter("company", "STRING", like(self.company)))
        if self.ticker:
            clauses.append("b.ticker = @ticker")
            parameters.append(ScalarQueryParameter("ticker", "STRING", self.ticker.strip().upper()))
        for name, column in AMOUNTS.items():
            for bound, test in (("min", ">="), ("max", "<=")):
                value = getattr(self, f"{name}_{bound}")
                if value is not None:
                    clauses.append(f"b.{column} {test} @{name}_{bound}")
                    parameters.append(ScalarQueryParameter(f"{name}_{bound}", "FLOAT64", value))
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
    """Which sheet: one metro's, by the name /map gives it ('New York, NY'), one
    region's, or with neither the whole United States. It takes no filters: a
    bar's height is its place among every US breach, so a sheet of fewer
    breaches would measure them against the same whole."""

    model_config = {"extra": "forbid"}

    metro: Annotated[str | None, Field(min_length=1)] = None
    region: RegionName | None = None

    @model_validator(mode="after")
    def one_place(self):
        if self.metro is not None and self.region is not None:
            raise ValueError("name a metro or a region, not both; name neither for the whole United States")
        return self


# How /breaches lists what it finds: newest or oldest first, or by an amount.
BreachSort = named("BreachSort", ["newest", "oldest", *AMOUNTS])
Order = named("Order", ["desc", "asc"])


class BreachQuery(Filters):
    limit: Annotated[int, Field(ge=1, le=BREACH_LIMIT_MAXIMUM)] = BREACH_LIMIT
    # An amount sorts largest first, or smallest first with order=asc; a
    # breach that does not report it comes last either way.
    sort: BreachSort = BreachSort.newest
    order: Order = Order.desc


# What /query can group breaches by: {name: (SQL on breaches aliased b, whether
# it holds a list)}. A breach counts once in every group its list names, so a
# breach that was both malware and ransomware is in both.
GROUPS = {
    **FILTER_COLUMNS,
    "metro": ("b.metro", False),
    "city": ("b.city", False),
    "year": ("CAST(b.year AS STRING)", False),
    "company": ("b.company", False),
    "ticker": ("b.ticker", False),
    "sec": ("IF(b.disclosed_to_sec, 'disclosed to the SEC', 'not disclosed to the SEC')", False),
    "auditor": ("b.auditor", False),
    "filing": ("b.filing_type", False),
}
GroupName = named("GroupName", GROUPS)

# What /query measures each group by. Count and companies need no amount; the
# rest are of the amount 'of' names, over the breaches that report it.
MEASURES = {
    "count": "COUNT(DISTINCT breach_key)",
    "companies": "COUNT(DISTINCT cik)",
    "sum": "SUM(amount)",
    "average": "AVG(amount)",
    "median": "ANY_VALUE(median)",
    "min": "MIN(amount)",
    "max": "MAX(amount)",
}
Measure = named("Measure", MEASURES)


class AnswerQuery(Filters):
    group: GroupName | None = None
    measure: Measure = Measure.count
    of: Amount | None = None
    order: Order = Order.desc
    limit: Annotated[int, Field(ge=1, le=QUERY_LIMIT_MAXIMUM)] = QUERY_LIMIT

    @model_validator(mode="after")
    def amount_named(self):
        needs = self.measure not in (Measure.count, Measure.companies)
        if needs and self.of is None:
            raise ValueError(f"a {self.measure} needs 'of', the amount to take it of: {', '.join(AMOUNTS)}")
        return self


class CompanyQuery(BaseModel):
    """One company's figures year by year, by its ticker or part of its name."""

    model_config = {"extra": "forbid"}

    ticker: Annotated[str | None, Field(min_length=1, max_length=10)] = None
    company: Annotated[str | None, Field(min_length=2, max_length=100)] = None

    @model_validator(mode="after")
    def one_company(self):
        if (self.ticker is None) == (self.company is None):
            raise ValueError("name the company by its ticker or by its name, one of the two")
        return self


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
    metro: str | None
    revenue_usd: float | None
    net_income_usd: float | None
    assets_usd: float | None
    book_value_usd: float | None
    market_cap_usd: float | None
    stock_price_usd: float | None


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
    # How many of them report at least one figure, and so stand on its
    # metro's sheet.
    charted: int


class BreachMap(BaseModel):
    regions: list[Region]
    cities: list[City]


class Holder(BaseModel):
    """The breach that holds a group's smallest or largest amount."""
    company: str
    ticker: str | None
    disclosed_on: date
    amount: float


class Group(BaseModel):
    # None when the answer is not grouped, or for breaches the grouped column
    # leaves empty.
    key: str | None
    value: float | None
    breaches: int
    companies: int
    # How many of its breaches report the amount measured, when there is one.
    reported: int
    holder: Holder | None = None


class Answer(BaseModel):
    matched: int
    groups_total: int
    groups: list[Group]


class Match(BaseModel):
    total: int
    labels: list[str]


class CompanyYear(BaseModel):
    ticker: str
    company: str | None
    industry: str | None
    year: int
    assets_usd: float | None
    net_income_usd: float | None
    stock_price_usd: float | None


class CompanyHistory(BaseModel):
    years: list[CompanyYear]


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


def charted(row):
    """Whether a breach reports all six figures, and so stands on a sheet.
    Cleaning keeps no other breach, so every one does; this holds the sheets
    to that should a breach missing a figure ever reach the table."""
    return all(row[column] is not None for column in METRICS)


def breach_label(row):
    """What a breach's row is called: its company's ticker and the day it was
    disclosed, which tells one company's breaches apart."""
    return f"{row['ticker']} {row['disclosed_on']:%Y/%m/%d}"


def metro_order(rows):
    """{metro: its place} for every metro, largest first by its breaches, as
    the app lists them: a region's sheet runs metro by metro in this order."""
    counts = {}
    for row in rows:
        if row["metro"] is not None:
            counts[row["metro"]] = counts.get(row["metro"], 0) + 1
    return {metro: i for i, metro in enumerate(sorted(counts, key=lambda m: (-counts[m], m)))}


def place_sheet(rows, here, colour_of):
    """One row per breach in 'here' that reports a figure, its six figures and
    their heights, in the colour colour_of(row) gives. Rows run region by
    region west to east, then metro by metro largest first, then city by city,
    so a metro's and a city's breaches stand together, and within a city
    oldest first. None when no such breach is there."""
    placed = {column: ranks(rows, column) for column in METRICS}
    here = [row for row in here if charted(row)]
    if not here:
        return None

    region_at = {region: i for i, region in enumerate(US_REGIONS)}
    metro_at = metro_order(rows)
    last = len(metro_at)
    here.sort(key=lambda row: (region_at.get(row["region"], len(region_at)),
                               metro_at.get(row["metro"], last), row["city"] or "",
                               row["disclosed_on"], row["breach_key"]))
    lines = [(breach_label(row), row["metro"], row["city"], colour_of(row),
              [row[column] for column in METRICS],
              [placed[column].get(row["breach_key"]) for column in METRICS])
             for row in here]
    return sheetcsv.render("Breach / Figure", list(METRICS.values()), lines)


def metro_coloured_sheet(rows, keep):
    """The breaches keep(row) picks, each in its metro's colour, as a metro's
    pin and a region's map draw them."""
    color = metro_colors(rows)
    return place_sheet(rows, [row for row in rows if keep(row)],
                       lambda row: color.get(row["metro"], OTHER_METRO_COLOR))


def us_sheet(rows):
    """Every US breach, each in its region's colour, as the national map fills
    its regions."""
    return place_sheet(rows, rows, lambda row: REGION_COLORS.get(row["region"], OTHER_METRO_COLOR))


@app.get("/sheet", response_class=PlainTextResponse)
def sheet(query: Annotated[SheetQuery, Query()]):
    rows = us_breaches()
    if query.metro is not None:
        text = metro_coloured_sheet(rows, lambda row: row["metro"] == query.metro)
        if text is None:
            raise HTTPException(404, f"there is no metro called '{query.metro}' with a breach that reports a "
                                     "figure; /map lists the metros and how many of their breaches do")
        return text
    text = (metro_coloured_sheet(rows, lambda row: row["region"] == query.region)
            if query.region is not None else us_sheet(rows))
    if text is None:
        raise HTTPException(404, f"no breach in {query.region or 'the United States'} reports a figure")
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
    parameters.append(ScalarQueryParameter("limit", "INT64", query.limit))
    if query.sort in (BreachSort.newest, BreachSort.oldest):
        order = "b.disclosed_on " + ("DESC" if query.sort == BreachSort.newest else "ASC")
    else:
        order = f"b.{AMOUNTS[query.sort]} {query.order.upper()} NULLS LAST, b.disclosed_on DESC"

    found = database.query(f"""
        SELECT b.*, b.division AS industry, COUNT(*) OVER () AS total
        FROM {database.table(database.BREACHES)} AS b
        WHERE {where}
        ORDER BY {order}, b.breach_key
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
            "color": color[row["metro"]], "lat": row["lat"], "lon": row["lon"], "breaches": 0, "charted": 0})
        city["breaches"] += 1
        city["charted"] += charted(row)
    return {
        "regions": [{"region": region, "title": region_title(region), "color": REGION_COLORS[region],
                     "breaches": n} for region, n in regions.items()],
        "cities": sorted(cities.values(), key=lambda c: (-c["breaches"], c["city"])),
    }


@app.get("/map", response_model=BreachMap)
def us_map():
    return breach_map(us_breaches())


def answer_sql(query):
    """(SQL, parameters) for one /query. Every value travels as a parameter;
    the column and the aggregate come from GROUPS, MEASURES and AMOUNTS, keyed
    by names the request was checked against."""
    where, parameters = query.where()
    amount = f"b.{AMOUNTS[query.of]}" if query.of else "CAST(NULL AS FLOAT64)"
    table = database.table(database.BREACHES)
    if query.group is None:
        key, joined, partition = "CAST(NULL AS STRING)", "", ""
    else:
        column, holds_list = GROUPS[query.group]
        key = "item" if holds_list else column
        joined = f" LEFT JOIN UNNEST({column}) AS item" if holds_list else ""
        partition = "PARTITION BY key"
    median = f", PERCENTILE_CONT(amount, 0.5) OVER ({partition}) AS median" if query.measure == Measure.median else ""
    # The breach holding the smallest or largest amount, for min and max.
    holder = ""
    if query.measure in (Measure.min, Measure.max):
        direction = "ASC" if query.measure == Measure.min else "DESC"
        holder = (", ARRAY_AGG(IF(amount IS NULL, NULL, STRUCT(company, ticker, disclosed_on, amount)) "
                  f"IGNORE NULLS ORDER BY amount {direction} LIMIT 1)[SAFE_OFFSET(0)] AS holder")
    parameters.append(ScalarQueryParameter("limit", "INT64", query.limit))
    sql = f"""
        WITH matched AS (
            SELECT b.breach_key, b.cik, b.company, b.ticker, b.disclosed_on, {key} AS key, {amount} AS amount
            FROM {table} AS b{joined}
            WHERE {where}
        ), measured AS (
            SELECT *{median} FROM matched
        )
        SELECT key, {MEASURES[query.measure]} AS value, COUNT(DISTINCT breach_key) AS breaches,
               COUNT(DISTINCT cik) AS companies, COUNT(amount) AS reported{holder},
               COUNT(*) OVER () AS groups_total,
               (SELECT COUNT(*) FROM {table} AS b WHERE {where}) AS matched
        FROM measured
        GROUP BY key
        ORDER BY value {query.order.upper()} NULLS LAST, key
        LIMIT @limit
    """
    return sql, parameters


@app.get("/query", response_model=Answer)
def answer(query: Annotated[AnswerQuery, Query()]):
    """Counts, totals, averages, medians and extremes over the breaches the
    filters keep, whole or group by group, largest first (order=asc for
    smallest). What the sheets cannot answer on their own: anything about
    breaches by what they were, or across places the open sheet does not hold."""
    rows = database.query(*answer_sql(query))
    if not rows:
        return {"matched": 0, "groups_total": 0, "groups": []}
    groups = [{"key": row["key"], "value": row["value"], "breaches": row["breaches"],
               "companies": row["companies"], "reported": row["reported"],
               "holder": row.get("holder")}
              for row in rows]
    return {"matched": rows[0]["matched"], "groups_total": rows[0]["groups_total"], "groups": groups}


@app.get("/match", response_model=Match)
def match(query: Annotated[Filters, Query()]):
    """Every breach the filters keep, by the label its row carries on a sheet
    (breach_label), however many: what the app needs to act on those rows."""
    where, parameters = query.where()
    figures = ", ".join(f"b.{column}" for column in METRICS)
    rows = database.query(f"""
        SELECT b.ticker, b.disclosed_on, {figures}
        FROM {database.table(database.BREACHES)} AS b
        WHERE {where}
        ORDER BY b.disclosed_on, b.breach_key
    """, parameters)
    return {"total": len(rows), "labels": [breach_label(row) for row in rows if charted(row)]}


@app.get("/fundamentals", response_model=CompanyHistory)
def fundamentals(query: Annotated[CompanyQuery, Query()]):
    """A company's figures year by year, breached or not: its assets and net
    income (the export's millions, given here in dollars) and its year-end
    share price. Several companies when a name matches several."""
    if query.ticker:
        test, value = "f.ticker = @named", query.ticker.strip().upper()
    else:
        test, value = "LOWER(f.company) LIKE @named", like(query.company)
    rows = database.query(f"""
        SELECT f.ticker, f.company, f.division AS industry, f.year,
               f.assets_musd * 1e6 AS assets_usd, f.net_income_musd * 1e6 AS net_income_usd,
               f.price_close AS stock_price_usd
        FROM {database.table(database.FUNDAMENTALS)} AS f
        WHERE {test}
        ORDER BY f.company, f.ticker, f.year
        LIMIT 200
    """, [ScalarQueryParameter("named", "STRING", value)])
    return {"years": [dict(row.items()) for row in rows]}
