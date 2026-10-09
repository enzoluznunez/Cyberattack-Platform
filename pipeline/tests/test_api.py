"""Every endpoint through FastAPI's test client, against the test dataset, with
each sheet checked cell by cell against the same count done in pandas."""

import pandas as pd
import pytest

import sheetcsv
from metrics import (
    ATTACK_TYPES,
    BREACH_LIMIT,
    COUNTRIES,
    DEFAULT_VIEW,
    DIVISION_COLORS,
    DIVISION_NAMES,
    FIRST_YEAR,
    FUNDAMENTALS,
    LAST_YEAR,
    OFFSETS,
    SHEET_PER,
    VIEWS,
)


def grid(text):
    """A sheet as (directives, header, {row label: [cells]})."""
    directives, header, rows = sheetcsv.read(text)
    return directives, header, {row[0]: row[1:] for row in rows}


def test_health_counts_both_tables(client, breaches, fundamentals):
    assert client.get("/health").json() == {
        "status": "ok", "breaches": len(breaches), "company_years": len(fundamentals)}


def test_views_lists_every_sheet(client):
    body = client.get("/views").json()
    assert body["default"] == DEFAULT_VIEW
    assert [view["view"] for view in body["views"]] == list(VIEWS)
    assert body["filters"]["attack"] == ATTACK_TYPES
    assert body["filters"]["industry"] == DIVISION_NAMES
    assert (body["first_year"], body["last_year"]) == (FIRST_YEAR, LAST_YEAR)


def test_the_default_sheet_is_attacks_by_year(client):
    assert client.get("/sheet").text == client.get(f"/sheet?view={DEFAULT_VIEW}").text


def test_attacks_by_year_counts_every_breach_under_each_of_its_attacks(client, attacks):
    directives, header, rows = grid(client.get("/sheet?view=attack_by_year").text)
    assert header == ["Attack Type / Year", *(str(y) for y in range(FIRST_YEAR, LAST_YEAR + 1))]
    assert directives == {}

    counts = attacks.groupby(["attack", "year"]).size()
    assert list(rows) == [a for a in ATTACK_TYPES if a in counts.index.get_level_values(0)]
    for attack, cells in rows.items():
        expected = [str(counts.get((attack, year), 0)) for year in range(FIRST_YEAR, LAST_YEAR + 1)]
        assert cells == expected, attack


def test_a_count_of_nothing_is_zero_not_blank(client):
    _, _, rows = grid(client.get("/sheet?view=attack_by_year").text)
    assert "" not in [cell for cells in rows.values() for cell in cells]


def test_industries_by_attack_matches_a_crosstab(client, attacks):
    directives, header, rows = grid(client.get("/sheet?view=industry_by_attack").text)
    assert header == ["Industry / Attack Type", *ATTACK_TYPES]

    counts = attacks.dropna(subset=["division"]).groupby(["division", "attack"]).size()
    assert list(rows) == [d for d in DIVISION_NAMES if d in counts.index.get_level_values(0)]
    for industry, cells in rows.items():
        assert cells == [str(counts.get((industry, attack), 0)) for attack in ATTACK_TYPES], industry

    # Each row arrives with its industry and that industry's colour.
    assert directives["industry"] == list(rows)
    assert directives["color"] == [DIVISION_COLORS[industry] for industry in rows]


def test_naming_an_attack_type_shows_that_type_alone(client, attacks):
    """A breach that was Malware and Phishing passes an attack=Malware filter,
    but its Phishing half is not what was asked about."""
    _, _, rows = grid(client.get("/sheet?view=attack_by_year&attack=Malware").text)
    assert list(rows) == ["Malware"]
    _, header, rows = grid(client.get("/sheet?view=industry_by_attack&attack=Ransomware,Phishing").text)
    assert header[1:] == ["Ransomware", "Phishing"]


def test_years_narrow_the_columns(client, attacks):
    _, header, rows = grid(client.get("/sheet?since=2019&until=2021").text)
    assert header[1:] == ["2019", "2020", "2021"]
    in_years = attacks[attacks["year"].between(2019, 2021)]
    assert sum(int(c) for cells in rows.values() for c in cells) == len(in_years)


@pytest.mark.parametrize("query, keep", [
    ("sec=true", lambda b: b["disclosed_to_sec"]),
    ("sec=false", lambda b: ~b["disclosed_to_sec"]),
    ("region=US West,Canada", lambda b: b["region"].isin(["US West", "Canada"])),
    ("information=Financial", lambda b: b["information_type"] == "Financial"),
    ("accessed=SSN", lambda b: b["information_accessed"].map(lambda items: "SSN" in items)),
    ("relationship=Subsidiary / Affiliate",
     lambda b: b["target_relationships"].map(lambda items: "Subsidiary / Affiliate" in items)),
    ("state=va", lambda b: b["state"] == "VA"),
    ("country=Japan,Canada", lambda b: b["country"].isin(["Japan", "Canada"])),
    ("industry=Finance, Insurance, Real Estate&industry=Retail Trade",
     lambda b: b["division"].isin(["Finance, Insurance, Real Estate", "Retail Trade"])),
])
def test_a_filter_counts_exactly_the_breaches_it_names(client, attacks, query, keep):
    _, _, rows = grid(client.get(f"/sheet?view=attack_by_year&{query}").text)
    assert sum(int(c) for cells in rows.values() for c in cells) == int(keep(attacks).sum())


def test_an_industry_name_with_commas_is_one_name(client):
    _, _, rows = grid(client.get("/sheet?view=industry_by_attack&industry=Finance, Insurance, Real Estate").text)
    assert list(rows) == ["Finance, Insurance, Real Estate"]


def test_before_and_after_lines_up_each_company_with_its_first_breach(client, breaches, fundamentals):
    directives, header, rows = grid(client.get("/sheet?view=before_after").text)
    assert directives["group"] == [str(len(OFFSETS))]
    assert header[1:6] == ["Assets -2", "Assets -1", "Assets 0", "Assets +1", "Assets +2"]
    assert len(header) == 1 + len(FUNDAMENTALS) * len(OFFSETS)
    assert list(rows) == sorted(rows)

    first = breaches.groupby("cik")["year"].min()
    by_ticker = fundamentals.drop_duplicates("cik").set_index("ticker")["cik"]
    figures = fundamentals.set_index(["cik", "year"])
    for ticker in list(rows)[:10]:
        cik = by_ticker[ticker]
        expected = []
        for column in FUNDAMENTALS:
            for offset in OFFSETS:
                key = (cik, first[cik] + offset)
                value = figures[column].get(key) if key in figures.index else None
                expected.append("" if value is None or pd.isna(value) else f"{value:.4f}")
        assert rows[ticker] == expected, ticker


def test_before_and_after_takes_a_few_from_every_industry(client, fundamentals):
    directives, _, rows = grid(client.get("/sheet?view=before_after").text)
    per_industry = pd.Series(directives["industry"]).value_counts()
    assert per_industry.max() <= SHEET_PER
    assert set(per_industry.index) == set(fundamentals["division"].dropna())


def test_before_and_after_in_one_industry_is_its_largest_companies(client, breaches, fundamentals):
    directives, _, rows = grid(client.get("/sheet?view=before_after&industry=Retail Trade&limit=3").text)
    assert set(directives["industry"]) == {"Retail Trade"}
    assert len(rows) == 3

    first = breaches[breaches["division"] == "Retail Trade"].groupby("cik")["year"].min().rename("year")
    sized = first.reset_index().merge(fundamentals, on=["cik", "year"], how="left")
    sized = sized.merge(fundamentals.drop_duplicates("cik")[["cik", "ticker"]], on="cik", suffixes=("_x", ""))
    largest = sized.sort_values(["assets_musd", "ticker"], ascending=[False, True], na_position="last")
    assert sorted(rows) == sorted(largest["ticker"].head(3))


def test_an_empty_sheet_says_what_was_asked(client):
    response = client.get("/sheet?industry=Mining&attack=Credential Stuffing")
    assert response.status_code == 404
    assert response.json()["detail"] == "no breaches in 2004-2024 with industry Mining; attack Credential Stuffing"


@pytest.mark.parametrize("path, said", [
    ("/sheet?view=pie_chart", "view: 'pie_chart' is not one of: attack_by_year, industry_by_attack, before_after"),
    ("/sheet?attack=Hacking", "attack: 'Hacking' is not one of: " + ", ".join(ATTACK_TYPES)),
    ("/sheet?industry=Atlantis", "industry: 'Atlantis' is not one of: " + ", ".join(DIVISION_NAMES)),
    ("/sheet?since=2020&until=2010", "since (2020) is after until (2010)"),
    ("/sheet?state=Virginia", "state: 'Virginia' is not a two-letter state code, like VA"),
    ("/map?country=Atlantis", "country: 'Atlantis' is not one of: " + ", ".join(COUNTRIES)),
    ("/sheet?bogus=1", "bogus: Extra inputs are not permitted (got '1')"),
])
def test_a_rejection_is_a_sentence(client, path, said):
    response = client.get(path)
    assert response.status_code == 422
    assert response.json()["detail"] == said


def test_years_outside_the_data_are_refused(client):
    assert client.get(f"/sheet?since={FIRST_YEAR - 1}").status_code == 422
    assert client.get(f"/sheet?until={LAST_YEAR + 1}").status_code == 422


def test_breaches_finds_a_subsidiary_by_name(client):
    body = client.get("/breaches?company=sam's club").json()
    assert body["total"] >= 1
    assert all(any("sam's club" in t.lower() for t in b["targets"]) or "sam's club" in b["company"].lower()
               for b in body["breaches"])


def test_breaches_lists_the_newest_first_and_counts_them_all(client, breaches):
    body = client.get("/breaches?attack=Ransomware").json()
    ransomware = breaches[breaches["attack_types"].map(lambda items: "Ransomware" in items)]
    assert body["total"] == len(ransomware)
    assert len(body["breaches"]) == BREACH_LIMIT
    dates = [b["disclosed_on"] for b in body["breaches"]]
    assert dates == sorted(dates, reverse=True)
    assert dates[0] == ransomware["disclosed_on"].max().isoformat()


def test_breaches_by_ticker(client, breaches):
    body = client.get("/breaches?ticker=wmt&limit=50").json()
    assert body["total"] == int((breaches["ticker"] == "WMT").sum())
    assert {b["ticker"] for b in body["breaches"]} == {"WMT"}


def test_a_search_with_like_wildcards_takes_them_literally(client):
    assert client.get("/breaches?company=%25%25").json() == {"total": 0, "breaches": []}


def test_the_map_counts_every_breach_once_in_its_country(client, breaches):
    countries = client.get("/map").json()["countries"]
    expected = breaches["country"].value_counts()
    assert {c["country"]: c["breaches"] for c in countries} == expected.to_dict()
    assert {c["country"]: c["code"] for c in countries} == {c: COUNTRIES[c] for c in expected.index}


def test_the_map_lists_countries_and_cities_most_breaches_first(client):
    countries = client.get("/map").json()["countries"]
    assert countries[0]["country"] == "United States"
    assert [c["breaches"] for c in countries] == sorted((c["breaches"] for c in countries), reverse=True)
    for country in countries:
        counts = [city["breaches"] for city in country["cities"]]
        assert counts == sorted(counts, reverse=True)


def test_a_city_dot_counts_the_breaches_headquartered_there(client, breaches):
    countries = {c["country"]: c for c in client.get("/map").json()["countries"]}
    for name, country in countries.items():
        mine = breaches[breaches["country"] == name]
        expected = mine.dropna(subset=["city"]).groupby("city").size().to_dict()
        assert {city["city"]: city["breaches"] for city in country["cities"]} == expected
    new_york = next(c for c in countries["United States"]["cities"] if c["city"] == "New York, NY")
    assert new_york["breaches"] == int((breaches["city"] == "New York, NY").sum())


def test_the_map_takes_the_sheets_filters(client, breaches):
    body = client.get("/map?attack=Ransomware&since=2020").json()
    kept = breaches[breaches["attack_types"].map(lambda items: "Ransomware" in items) & (breaches["year"] >= 2020)]
    assert {c["country"]: c["breaches"] for c in body["countries"]} == kept["country"].value_counts().to_dict()
    assert [c["country"] for c in client.get("/map?country=Japan").json()["countries"]] == ["Japan"]


def test_a_map_of_nothing_is_an_answer(client):
    response = client.get("/map?industry=Mining&attack=Credential Stuffing")
    assert response.status_code == 200
    assert response.json() == {"countries": []}


def test_breaches_say_where_the_company_is(client):
    toyota = client.get("/breaches?company=toyota").json()["breaches"][0]
    assert (toyota["country"], toyota["city"]) == ("Japan", "Toyota")
