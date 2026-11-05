"""Read the two exports as they were delivered and type them.

The exports are the seed the whole dataset grows from. They live in a private
Cloud Storage bucket, verbatim, so anything cleaning drops is still there to
ask about. Nothing here writes anywhere: rebuild.py reads them, cleans them
and publishes the result.

    raw = read_raw()            both exports, every value text
    breaches, fundamentals = usable(*raw)

usable() is the whole definition of what the tables hold.
"""

import io
import os
import re
from functools import cache
from pathlib import Path

import pandas as pd

import maps
import metros
import places
from metrics import (
    ATTACK_TYPES,
    FIRST_YEAR,
    INFORMATION_ACCESSED,
    INFORMATION_TYPES,
    LAST_YEAR,
    MARKETS,
    METRICS,
    REGIONS,
    RELATIONSHIPS,
    STATE_REGIONS,
    US,
    country_of,
    division,
)

# Where the exports are: a gs:// prefix or a local directory holding both files.
RAW_SOURCE = os.environ.get("RAW_SOURCE", "gs://cyberattack-platform-raw")
BREACHES_FILE = "breach_events.csv"
FUNDAMENTALS_FILE = "company_fundamentals.csv"

# What the exports write where they have no figure.
MISSING = {"", "."}


def column(heading):
    """'Date of Breach Disclosure' -> date_of_breach_disclosure. The one place
    the exports' headings are turned into identifiers."""
    return re.sub(r"_+", "_", re.sub(r"[^a-z0-9]+", "_", heading.lower())).strip("_")


@cache
def storage_client():
    # Imported here: only a read from Cloud Storage needs it.
    from google.cloud import storage

    return storage.Client()


def read_bytes(source, name):
    if source.startswith("gs://"):
        bucket, _, prefix = source.removeprefix("gs://").partition("/")
        path = f"{prefix.strip('/')}/{name}" if prefix.strip("/") else name
        return storage_client().bucket(bucket).blob(path).download_as_bytes()
    return (Path(source) / name).read_bytes()


def read_one(source, name):
    """One export, every value as the text it was written as. Nothing is parsed
    or guessed at here, so what counts as missing is decided in one place."""
    raw = pd.read_csv(io.BytesIO(read_bytes(source, name)), dtype=str,
                      keep_default_na=False, na_filter=False)
    raw.columns = [column(h) for h in raw.columns]
    return raw


def read_raw(source=RAW_SOURCE):
    return read_one(source, BREACHES_FILE), read_one(source, FUNDAMENTALS_FILE)


def text(values):
    return values.map(lambda v: None if v.strip() in MISSING else v.strip())


def number(values):
    return pd.to_numeric(text(values), errors="raise").astype("float64")


def integer(values):
    """Whole numbers the export sometimes writes as '7374.0'."""
    numbers = number(values)
    if (numbers.dropna() % 1 != 0).any():
        raise ValueError(f"{values.name} holds a fraction where a whole number belongs")
    return numbers.astype("Int64")


def date(values):
    parsed = pd.to_datetime(text(values), format="%Y-%m-%d", errors="raise")
    return parsed.map(lambda d: None if pd.isna(d) else d.date())


def items(values):
    """'|Malware|Phishing|' -> ['Malware', 'Phishing']; an empty cell is []."""
    return values.map(lambda cell: [item.strip() for item in cell.strip().strip("|").split("|")
                                    if item.strip()])


def reject_unknown(values, found, vocabulary):
    """An unknown name stops the rebuild rather than reaching a table no sheet
    will ever count it in."""
    unknown = set(found) - set(vocabulary)
    if unknown:
        raise ValueError(f"{values.name} holds {sorted(unknown)}, which metrics.py does not name")


def listed(values, vocabulary):
    """items(), where every item has to be one the vocabulary names."""
    found = items(values)
    reject_unknown(values, (item for row in found for item in row), vocabulary)
    return found


def known(values, vocabulary):
    cleaned = text(values)
    reject_unknown(values, cleaned.dropna(), vocabulary)
    return cleaned


def company_sic(breaches_raw, fundamentals_raw):
    """One SIC code per company, keyed by CIK. The breach export's code wins:
    it is the SEC's own assignment, and where the two exports disagree (one
    company in eleven) the fundamentals export is the one that is off — it files
    Berkshire Hathaway under 9997, a conglomerate, which would make it Public
    Administration. The fundamentals export fills in the companies the breach
    export left without one."""
    def by_cik(raw, column):
        return (pd.DataFrame({"cik": integer(raw["cik"]), "sic": integer(raw[column])})
                .dropna().drop_duplicates("cik").set_index("cik")["sic"])

    return by_cik(breaches_raw, "sic_code").combine_first(by_cik(fundamentals_raw, "sic"))


def industry(cik, sic):
    """(SIC code, division) per row, for a column of CIKs."""
    codes = cik.map(sic).astype("Int64")
    return codes, codes.map(lambda code: division(int(code)) if pd.notna(code) else None)


def located(raw):
    """(country, place, lat, lon) per breach, for its headquarters. Every
    breach has a country. A breach with a city has that city's place from
    places.csv, which is what the maps draw one dot for; a city places.csv does
    not list stops the rebuild, as an unknown name does. The one breach the
    export gives no city stays off the maps' dots but counts in its country."""
    known_places = places.load()

    country = pd.Series([country_of(r.strip(), s.strip()) for r, s in zip(raw["region"], raw["state_code"])],
                        index=raw.index, dtype="object", name="country")
    if country.isna().any():
        codes = sorted(set(raw.loc[country.isna(), "state_code"].str.strip()))
        raise ValueError(f"state_code holds {codes}, a country metrics.py does not name")

    keys = [(c, s.strip(), places.city_key(city)) for c, s, city in zip(country, raw["state_code"], raw["city"])]
    unknown = sorted({k for k in keys if k[2] is not None and k not in known_places})
    if unknown:
        raise ValueError(f"city holds {unknown}, which places.csv does not list")

    found = [known_places.get(k) if k[2] is not None else None for k in keys]
    return (country,
            pd.Series([f[0] if f else None for f in found], index=raw.index, dtype="object"),
            pd.Series([f[1] if f else None for f in found], index=raw.index, dtype="float64"),
            pd.Series([f[2] if f else None for f in found], index=raw.index, dtype="float64"))


# The ticker each breach the export gives no ticker traded under when it was
# disclosed, looked up by hand from its company's name, with where each came
# from; a blank ticker means the company traded under none then.
TICKERS_FILE = Path(__file__).with_name("tickers.csv")


def looked_up_tickers(path=TICKERS_FILE):
    """{breach_key: ticker, or None when there was none} from tickers.csv."""
    listed_ = pd.read_csv(path, dtype=str, keep_default_na=False)
    return {int(key): ticker.strip() or None for key, ticker in zip(listed_["breach_key"], listed_["ticker"])}


def with_tickers(frame, looked_up):
    """The breaches with a ticker: the export's, or for a breach it gives none,
    the one tickers.csv looked up. A breach whose company traded under no
    ticker when it was disclosed is left out of every table. A blank ticker
    tickers.csv does not list stops the rebuild, so a new export's gaps are
    looked up rather than silently dropped."""
    blank = frame["ticker"].isna()
    unlisted = sorted(set(frame.loc[blank, "breach_key"]) - set(looked_up))
    if unlisted:
        raise ValueError(f"breaches {unlisted} have no ticker, and tickers.csv does not list them")
    filled = frame["ticker"].where(~blank, frame["breach_key"].map(looked_up))
    return frame.assign(ticker=filled)[filled.notna()].reset_index(drop=True)


def breaches(raw, sic):
    """One row per breach, typed. The ID numbers, phone number, address lines,
    duplicate state names and the second auditor block are left behind in the
    export: nothing draws, filters or reads them."""
    cik = integer(raw["cik"])
    year = integer(raw["fyear"])
    disclosed_on = date(raw["date_of_breach_disclosure"])

    # The export's fiscal year is the year the breach was disclosed. The year
    # sheets count by it, so a breach whose two dates disagree is a question
    # for the export, not something to settle here.
    if (disclosed_on.map(lambda d: d.year) != year).any():
        raise ValueError("a breach's fyear differs from the year it was disclosed")

    disclosed_to_sec = raw["disclosed_to_sec"].str.strip()
    if not disclosed_to_sec.isin(["Yes", "No"]).all():
        raise ValueError("disclosed_to_sec holds something other than Yes or No")

    codes, divisions = industry(cik, sic)
    country, city, lat, lon = located(raw)
    frame = pd.DataFrame({
        "breach_key": integer(raw["breach_key"]),
        "cik": cik,
        "company": text(raw["public_company_name"]),
        "targets": items(raw["target_name"]),
        "target_relationships": listed(raw["target_relationship_to_parent"], RELATIONSHIPS),
        "ticker": text(raw["ticker"]),
        "market": known(raw["market"], MARKETS),
        "state": text(raw["state_code"]),
        "region": known(raw["region"], REGIONS),
        "country": country,
        "city": city,
        "lat": lat,
        "lon": lon,
        "sic": codes,
        "sic_description": text(raw["sic_code_description"]),
        "naics": integer(raw["naics_code"]),
        "division": divisions,
        "year": year,
        "disclosed_on": disclosed_on,
        "discovered_on": date(raw["date_of_breach_discovery"]),
        "started_on": date(raw["breach_start_date"]),
        "ended_on": date(raw["breach_end_date"]),
        "cost_usd": number(raw["cost"]),
        "records_lost": integer(raw["number_of_records_lost"]),
        "information_type": known(raw["type_of_information"], INFORMATION_TYPES),
        "attack_types": listed(raw["type_of_attack"], ATTACK_TYPES),
        "information_accessed": listed(raw["information_accessed"], INFORMATION_ACCESSED),
        "disclosed_to_sec": disclosed_to_sec == "Yes",
        "filing_url": text(raw["filing"]),
        "filing_date": date(raw["filing_date"]),
        "filing_type": text(raw["filing_type"]),
        "auditor": text(raw["auditor_breach_date"]),
        "market_cap_usd": number(raw["market_cap"]),
        "revenue_usd": number(raw["revenue"]),
        "net_income_usd": number(raw["earnings_net_income"]),
        "assets_usd": number(raw["assets"]),
        "book_value_usd": number(raw["book_value"]),
        "stock_price_usd": number(raw["stock_price"]),
        "stock_price_on": date(raw["stock_price_date"]),
        "financials_on": date(raw["financials_date"]),
    })
    frame = with_tickers(frame, looked_up_tickers())
    frame = complete(frame)
    frame["metro"] = metro_of(frame)

    # Checked on the breaches kept: the years every table holds.
    if frame["year"].min() < FIRST_YEAR or frame["year"].max() > LAST_YEAR:
        raise ValueError(f"breaches run {frame['year'].min()}-{frame['year'].max()}, outside "
                         f"metrics.FIRST_YEAR-LAST_YEAR ({FIRST_YEAR}-{LAST_YEAR})")

    us = frame["country"] == US
    drawn = frame.loc[us, "state"].map(STATE_REGIONS)
    if drawn.isna().any():
        raise ValueError(f"state holds {sorted(set(frame.loc[us, 'state'][drawn.isna()]))}, "
                         "which metrics.STATE_REGIONS does not place in a region")
    if (drawn != frame.loc[us, "region"]).any():
        raise ValueError("a US breach's region differs from the one metrics.STATE_REGIONS gives its state")

    if frame["breach_key"].isna().any() or frame["breach_key"].duplicated().any():
        raise ValueError("every breach needs its own breach_key")
    if (frame["attack_types"].map(len) == 0).any():
        raise ValueError("a breach lists no attack type, not even 'Not Disclosed'")
    return frame.sort_values("breach_key").reset_index(drop=True)


def complete(frame):
    """The breaches that report every one of the six figures a sheet draws. A
    breach missing any is left out of every table, so every bar on every sheet
    stands for a reported figure and an empty place on a sheet can only be a
    line the user filtered away. It is dropped before metros are grouped, so
    the map counts only what the sheets show."""
    return frame[frame[list(METRICS)].notna().all(axis=1)].reset_index(drop=True)


def metro_of(frame):
    """Each US breach's metro, its city's pin merged with the pins it overlaps
    on its region's map by metros.group; None outside the United States and for
    a breach with no city."""
    us = frame[(frame["country"] == US) & frame["city"].notna()]
    population = places.populations()
    cities = {}
    for city, rows in us.groupby("city"):
        lon, lat = rows["lon"].iloc[0], rows["lat"].iloc[0]
        x, y = maps.place(lon, lat, maps.frame_of(lon, lat))
        cities[city] = (x, y, len(rows), population.get(city, 0), rows["region"].iloc[0])
    grouped = metros.group(cities, maps.one_breach_km())
    return pd.Series([grouped.get(city) if country == US else None
                      for country, city in zip(frame["country"], frame["city"])],
                     index=frame.index, dtype="object")


# The figures kept from the fundamentals export, by the column they are stored
# under and the export column they come from.
FIGURES = {
    "assets_musd": "at",
    "net_income_musd": "ni",
    "price_close": "prcc_f",
    "auditor_opinion": "auopic",
}


def fundamentals(raw, sic):
    """One row per company per year, typed. The export repeats 2,165 company-
    years, each as a pair where one row lacks figures the other has; the fuller
    row is the one kept. Two rows equally full that disagree would be a real
    conflict, and stop the rebuild."""
    frame = pd.DataFrame({
        "cik": integer(raw["cik"]),
        "gvkey": integer(raw["gvkey"]),
        "ticker": text(raw["ticker"]),
        "company": text(raw["company_name"]),
        "year": integer(raw["fyear"]),
        "fiscal_year_end_month": integer(raw["fyr"]),
        **{name: number(raw[source]) for name, source in FIGURES.items()},
    })

    reported = frame[list(FIGURES)].notna().sum(axis=1)
    fullest = reported == reported.groupby([frame["cik"], frame["year"]]).transform("max")
    frame = frame[fullest].drop_duplicates(["cik", "year", *FIGURES])
    if frame.duplicated(["cik", "year"]).any():
        raise ValueError("a company-year is reported twice, equally fully, with different figures")

    codes, divisions = industry(frame["cik"], sic)
    frame = frame.assign(sic=codes, division=divisions,
                         auditor_opinion=frame["auditor_opinion"].astype("Int64"))
    return frame.sort_values(["cik", "year"]).reset_index(drop=True)


def usable(breaches_raw, fundamentals_raw):
    sic = company_sic(breaches_raw, fundamentals_raw)
    return breaches(breaches_raw, sic), fundamentals(fundamentals_raw, sic)


def main():
    breaches_raw, fundamentals_raw = read_raw()
    b, f = usable(breaches_raw, fundamentals_raw)
    print(f"breaches: {len(breaches_raw)} raw -> {len(b)} rows, {b['cik'].nunique()} companies; "
          f"fundamentals: {len(fundamentals_raw)} raw -> {len(f)} company-years")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
