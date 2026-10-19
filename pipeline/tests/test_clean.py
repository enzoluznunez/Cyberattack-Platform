"""Cleaning: what the tables hold, checked against the exports themselves."""

import math

import pandas as pd
import pytest

import clean
import maps
import metros
import places
from metrics import COUNTRIES, FIRST_YEAR, LAST_YEAR, METRICS


@pytest.fixture(scope="module")
def sic(raw):
    return clean.company_sic(*raw)


def incomplete(raw):
    """The export's breach keys missing any of the six figures a sheet draws."""
    blank = pd.concat([raw[0][column].str.strip().isin(clean.MISSING) for column in FIGURE_COLUMNS], axis=1)
    return set(raw[0].loc[blank.any(axis=1), "breach_key"].str.strip().astype(int))


# The export's columns for the six figures.
FIGURE_COLUMNS = ["revenue", "earnings_net_income", "assets", "book_value", "market_cap", "stock_price"]


def test_only_breaches_with_a_ticker_and_every_figure_survive_cleaning(raw, breaches):
    """The breaches whose company traded under no ticker when they were
    disclosed, which tickers.csv names, and those missing any of the six
    figures are left out; every other one is kept."""
    none = {key for key, ticker in clean.looked_up_tickers().items() if ticker is None}
    every = set(raw[0]["breach_key"].str.strip().astype(int))
    assert set(breaches["breach_key"]) == every - none - incomplete(raw)
    assert breaches["breach_key"].is_unique


def test_every_breach_reports_every_figure(breaches):
    assert breaches[list(METRICS)].notna().all().all()


@pytest.fixture(scope="module")
def kept(raw, breaches):
    """The export's breach rows for the breaches cleaning keeps: those it
    leaves out, for having no ticker or a missing figure, are checked above,
    not by every count."""
    keys = set(breaches["breach_key"])
    return raw[0][raw[0]["breach_key"].str.strip().astype(int).isin(keys)]


def test_every_breach_has_a_ticker(breaches):
    assert breaches["ticker"].notna().all()
    # A share class follows a dot: BRK.A, BF.B.
    assert breaches["ticker"].str.fullmatch(r"[A-Z]{1,5}(\.[A-Z])?").all()


def test_a_blank_ticker_is_filled_from_tickers_csv(raw, breaches):
    """Every kept breach the export gave no ticker carries the one tickers.csv
    looked up for it, such as Google Inc.'s GOOG in 2010."""
    blank = set(raw[0].loc[raw[0]["ticker"].str.strip().isin(clean.MISSING), "breach_key"].str.strip().astype(int))
    looked_up = clean.looked_up_tickers()
    ticker = breaches.set_index("breach_key")["ticker"]
    filled = blank & set(ticker.index)
    assert filled
    assert all(ticker[key] == looked_up[key] for key in filled)
    assert ticker[1] == "GOOG"


def test_a_blank_ticker_tickers_csv_does_not_list_stops_the_rebuild():
    frame = pd.DataFrame({"breach_key": [1, 2], "ticker": ["AAPL", None]})
    with pytest.raises(ValueError, match=r"breaches \[2\]"):
        clean.with_tickers(frame, {})
    assert list(clean.with_tickers(frame, {2: None})["breach_key"]) == [1]
    assert list(clean.with_tickers(frame, {2: "MSFT"})["ticker"]) == ["AAPL", "MSFT"]


def test_one_row_per_company_year(raw, fundamentals):
    assert not fundamentals.duplicated(["cik", "year"]).any()
    assert len(fundamentals) == len(raw[1].drop_duplicates(["cik", "fyear"]))


def test_pipe_lists_become_lists():
    cells = pd.Series(["|Malware|Phishing|", "|Ransomware|", ""], name="type_of_attack")
    assert clean.items(cells).tolist() == [["Malware", "Phishing"], ["Ransomware"], []]


def test_a_list_cell_holds_what_the_export_listed(kept, breaches):
    by_key = breaches.set_index("breach_key")
    row = kept.iloc[0]
    expected = [item for item in row["type_of_attack"].strip("|").split("|") if item]
    assert by_key.loc[int(row["breach_key"]), "attack_types"] == expected


def test_an_unknown_attack_type_stops_the_rebuild(raw, sic):
    changed = raw[0].copy()
    changed.loc[0, "type_of_attack"] = "|Hacking|"
    with pytest.raises(ValueError, match="Hacking"):
        clean.breaches(changed, sic)


def test_the_year_is_the_year_of_disclosure(breaches):
    assert (breaches["disclosed_on"].map(lambda d: d.year) == breaches["year"]).all()


def test_a_fiscal_year_that_disagrees_with_the_disclosure_stops_the_rebuild(raw, sic):
    changed = raw[0].copy()
    changed.loc[0, "fyear"] = str(int(changed.loc[0, "fyear"]) + 1)
    with pytest.raises(ValueError, match="fyear"):
        clean.breaches(changed, sic)


def test_a_breach_outside_the_year_range_stops_the_rebuild(raw, kept, sic):
    changed = raw[0].copy()
    at = kept.index[0]
    changed.loc[at, "fyear"] = str(LAST_YEAR + 1)
    changed.loc[at, "date_of_breach_disclosure"] = f"{LAST_YEAR + 1}-01-15"
    with pytest.raises(ValueError, match="outside"):
        clean.breaches(changed, sic)


def test_the_year_range_is_the_data_range(breaches):
    """metrics.FIRST_YEAR and LAST_YEAR are the year sheet's columns; the data
    filling them exactly means no column is empty for want of data."""
    assert breaches["year"].min() == FIRST_YEAR
    assert breaches["year"].max() == LAST_YEAR


def test_unreported_is_missing_not_zero(kept, breaches):
    blank = kept["number_of_records_lost"].str.strip().isin(["", "."]).sum()
    assert breaches["records_lost"].isna().sum() == blank


def test_a_repeated_company_year_keeps_the_fuller_row(fundamentals):
    """Brookfield is reported twice for 2004: once with net income, once
    without and with different assets. The row with net income is kept."""
    row = fundamentals[(fundamentals["cik"] == 1001085) & (fundamentals["year"] == 2004)]
    assert row[["assets_musd", "net_income_musd", "price_close"]].values.tolist() == [[20010.0, 688.0, 36.01]]


def test_two_equally_full_rows_that_disagree_stop_the_rebuild(raw, sic):
    changed = raw[1].copy()
    twin = changed.iloc[[0]].copy()
    twin["at"] = "123456"
    with pytest.raises(ValueError, match="equally fully"):
        clean.fundamentals(pd.concat([changed, twin], ignore_index=True), sic)


def test_figures_keep_their_units(raw, fundamentals):
    """The fundamentals export reports millions, and the column says so."""
    first = raw[1].iloc[0]
    row = fundamentals[(fundamentals["cik"] == int(first["cik"])) & (fundamentals["year"] == int(first["fyear"]))]
    assert row["assets_musd"].iloc[0] == float(first["at"])


def test_the_breach_exports_industry_wins(breaches, fundamentals):
    """The fundamentals export files Berkshire Hathaway under SIC 9997, which
    would make it Public Administration; the breach export's 6331 makes it
    insurance, and both tables say so."""
    berkshire = breaches[breaches["company"] == "BERKSHIRE HATHAWAY INC"]
    assert set(berkshire["division"]) == {"Finance, Insurance, Real Estate"}
    cik = berkshire["cik"].iloc[0]
    assert set(fundamentals[fundamentals["cik"] == cik]["division"]) == {"Finance, Insurance, Real Estate"}


def test_a_company_has_one_industry_in_both_tables(breaches, fundamentals):
    both = breaches[["cik", "division"]].drop_duplicates().merge(
        fundamentals[["cik", "division"]].drop_duplicates(), on="cik", suffixes=("_b", "_f"))
    assert (both["division_b"] == both["division_f"]).all()


def test_only_companies_no_export_classifies_lack_an_industry(raw, kept, breaches):
    fundamentals_raw = raw[1]
    no_code = kept["sic_code"].str.strip() == ""
    unclassified = kept[no_code & ~kept["cik"].isin(fundamentals_raw["cik"])]
    assert breaches["division"].isna().sum() == len(unclassified)


def test_every_breach_has_a_country(kept, breaches):
    assert breaches["country"].isin(list(COUNTRIES)).all()
    us = kept["region"].str.strip().str.startswith("US ")
    assert (breaches["country"] == "United States").sum() == us.sum()


def test_every_breach_with_a_city_is_placed(kept, breaches):
    blank = kept["city"].map(places.city_key).isna()
    assert breaches["city"].isna().sum() == blank.sum() <= 1
    assert breaches["lat"].notna().sum() == breaches["lon"].notna().sum() == len(breaches) - blank.sum()
    assert breaches["lat"].dropna().between(-90, 90).all()
    assert breaches["lon"].dropna().between(-180, 180).all()


def test_spellings_of_one_city_are_one_place(breaches):
    """'ST LOUIS' and 'ST. LOUIS', 'NEW YORK' and 'NEW YORK,' and 'DUBLIN 2'
    each land on one dot."""
    for place in ("St. Louis, MO", "New York, NY", "Dublin", "São Paulo"):
        at = breaches[breaches["city"] == place]
        assert len(at) > 1
        assert at[["lat", "lon"]].drop_duplicates().shape[0] == 1


def test_one_place_has_one_position():
    """A dot is one place, so a place name every spelling of it shares sits in
    one spot in its country."""
    positions = {}
    for (country, _, _), (place, lat, lon) in places.load().items():
        positions.setdefault((country, place), set()).add((lat, lon))
    assert {k: v for k, v in positions.items() if len(v) > 1} == {}


def test_a_city_places_csv_lacks_stops_the_rebuild(raw, sic):
    changed = raw[0].copy()
    changed.loc[0, "city"] = "ATLANTIS"
    with pytest.raises(ValueError, match="ATLANTIS"):
        clean.breaches(changed, sic)


def test_a_country_metrics_lacks_stops_the_rebuild(raw, sic):
    changed = raw[0].copy()
    foreign = changed.index[changed["region"].str.strip() == "Foreign"][0]
    changed.loc[foreign, "state_code"] = "Q9"
    with pytest.raises(ValueError, match="Q9"):
        clean.breaches(changed, sic)


def test_book_value_and_stock_price_are_carried(kept, breaches):
    by_key = breaches.set_index("breach_key")
    reported = kept[(kept["book_value"].str.strip() != "") & (kept["stock_price"].str.strip() != "")]
    row = reported.iloc[0]
    assert by_key.loc[int(row["breach_key"]), "book_value_usd"] == float(row["book_value"])
    assert by_key.loc[int(row["breach_key"]), "stock_price_usd"] == float(row["stock_price"])


def test_every_us_breach_with_a_city_has_a_metro(breaches):
    us = breaches["country"] == "United States"
    assert breaches.loc[us & breaches["city"].notna(), "metro"].notna().all()
    assert breaches.loc[~us, "metro"].isna().all()


def test_cities_whose_pins_overlap_on_their_regions_map_are_one_metro(breaches):
    metro = breaches.dropna(subset=["metro"]).drop_duplicates("city").set_index("city")["metro"]
    assert metro["Jersey City, NJ"] == metro["Philadelphia, PA"] == "New York, NY"
    assert metro["Woonsocket, RI"] == metro["Burlington, MA"] == "Boston, MA"
    assert metro["San Francisco, CA"] == metro["San Jose, CA"]
    # Stamford is in New England, and a metro never reaches over a border.
    assert metro["Stamford, CT"] != metro["Jersey City, NJ"]


def test_no_metros_pin_has_its_centre_inside_another_on_their_regions_map(breaches):
    """What the grouping promises the map: every pin left can be seen."""
    us = breaches.dropna(subset=["metro"])
    unit = maps.one_breach_km()
    for region, rows in us.groupby("region"):
        pins = []
        for _, members in rows.groupby("metro"):
            places_ = [maps.place(lon, lat, maps.frame_of(lon, lat)) for lon, lat in zip(members["lon"], members["lat"])]
            x = sum(p[0] for p in places_) / len(places_)
            y = sum(p[1] for p in places_) / len(places_)
            pins.append((x, y, metros.radius(len(members), unit[region])))
        for i, (x, y, r) in enumerate(pins):
            for j, (u, v, q) in enumerate(pins):
                if i != j and q <= r:
                    assert math.hypot(u - x, v - y) >= r, region


def test_a_region_that_disagrees_with_its_state_stops_the_rebuild(raw, kept, sic):
    changed = raw[0].copy()
    texan = kept.index[kept["state_code"].str.strip() == "TX"][0]
    changed.loc[texan, "region"] = "US West"
    with pytest.raises(ValueError, match="STATE_REGIONS"):
        clean.breaches(changed, sic)
