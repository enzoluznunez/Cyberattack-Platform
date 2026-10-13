"""Every endpoint through FastAPI's test client, against the test dataset, with
each answer checked against the same thing worked out in pandas."""

import pandas as pd
import pytest

import api
import sheetcsv
from metrics import (
    ATTACK_TYPES,
    BREACH_LIMIT,
    DIVISION_NAMES,
    FIRST_YEAR,
    LAST_YEAR,
    METRICS,
    OTHER_METRO_COLOR,
    REGION_COLORS,
    US,
    US_REGIONS,
    region_title,
)


@pytest.fixture(scope="module")
def us(breaches):
    return breaches[breaches["country"] == US]


def sheet(client, region):
    """A region's sheet, or with region None the national one, as (directives,
    figure titles, rows), each row (label, figures, heights) as the text cells
    it arrived as."""
    path = "/sheet" if region is None else f"/sheet?region={region}"
    directives, header, rows = sheetcsv.read(client.get(path).text)
    n = int(directives["heights"][0])
    return directives, header[1:1 + n], [(row[0], row[1:1 + n], row[1 + n:]) for row in rows]


def test_health_counts_both_tables(client, breaches, fundamentals):
    assert client.get("/health").json() == {
        "status": "ok", "breaches": len(breaches), "company_years": len(fundamentals)}


@pytest.mark.parametrize("region", US_REGIONS)
def test_a_region_sheet_has_a_row_for_every_breach_in_it(client, us, region):
    directives, titles, rows = sheet(client, region)
    assert titles == list(METRICS.values())
    assert len(rows) == int((us["region"] == region).sum())
    assert len(directives["metro"]) == len(directives["city"]) == len(directives["color"]) == len(rows)


def test_a_pins_rows_stand_together(client):
    directives, _, _ = sheet(client, "US West")
    cities = directives["city"]
    blocks = [c for i, c in enumerate(cities) if i == 0 or cities[i - 1] != c]
    assert len(blocks) == len(set(blocks))


def test_the_cells_are_the_figures_and_unreported_is_blank(client, us):
    _, _, rows = sheet(client, "US New England")
    here = us[us["region"] == "US New England"]
    figures = sorted("" if pd.isna(v) else f"{v:.4f}" for v in here["revenue_usd"])
    assert sorted(cells[0] for _, cells, _ in rows) == figures
    for _, cells, heights in rows:
        assert [c == "" for c in cells] == [h == "" for h in heights]


def test_a_height_is_a_place_among_every_us_breach(client, us):
    """The largest figure in the country stands 1 tall, wherever it is, and a
    height is the share of breaches at or below its figure."""
    for column, title in METRICS.items():
        figures = us[column].dropna()
        largest = us.loc[figures.idxmax(), "region"]
        _, titles, rows = sheet(client, largest)
        at = titles.index(title)
        assert max(float(h[at]) for _, _, h in rows if h[at]) == 1.0
        for _, cells, heights in rows[:5]:
            if cells[at]:
                expected = (figures <= float(cells[at]) + 1e-4).sum() / len(figures)
                assert float(heights[at]) == pytest.approx(expected, abs=1e-4)


def test_a_metros_rows_stand_together_coloured_largest_first(client):
    directives, _, _ = sheet(client, "US Mid Atlantic")
    metros, colors = directives["metro"], directives["color"]
    blocks = [m for i, m in enumerate(metros) if i == 0 or metros[i - 1] != m]
    assert len(blocks) == len(set(blocks))
    assert metros[0] == "New York, NY"
    first_grey = colors.index(OTHER_METRO_COLOR)
    assert OTHER_METRO_COLOR not in colors[:first_grey]
    assert set(colors[first_grey:]) == {OTHER_METRO_COLOR}


def test_a_metro_is_one_colour_on_every_sheet_and_map(client):
    seen = {}
    for region in US_REGIONS:
        directives, _, _ = sheet(client, region)
        for metro, color in zip(directives["metro"], directives["color"]):
            if metro:
                assert seen.setdefault(metro, color) == color, metro
    for city in client.get("/map").json()["cities"]:
        assert seen[city["metro"]] == city["color"], city["city"]


def test_each_region_colours_its_ten_largest_metros(client):
    for region in US_REGIONS:
        directives, _, _ = sheet(client, region)
        coloured = {m for m, c in zip(directives["metro"], directives["color"]) if c != OTHER_METRO_COLOR}
        expected = min(10, len(set(directives["metro"])))
        assert len(coloured) == len(set(directives["color"]) - {OTHER_METRO_COLOR}) == expected, region


def test_the_national_sheet_holds_every_us_breach_region_by_region(client, us):
    directives, titles, rows = sheet(client, None)
    assert titles == list(METRICS.values())
    assert len(rows) == len(us)
    blocks = [m for i, m in enumerate(directives["metro"]) if i == 0 or directives["metro"][i - 1] != m]
    assert blocks == [region_title(region) for region in US_REGIONS]


def test_the_national_sheet_is_coloured_as_the_national_map(client):
    directives, _, _ = sheet(client, None)
    regions = {r["title"]: r["color"] for r in client.get("/map").json()["regions"]}
    assert all(regions[m] == c for m, c in zip(directives["metro"], directives["color"]))


@pytest.mark.parametrize("path, said", [
    ("/sheet?region=Canada", "region: 'Canada' is not one of: " + ", ".join(US_REGIONS)),
    ("/sheet?region=US West&since=2020", "since: Extra inputs are not permitted (got '2020')"),
    ("/breaches?attack=Hacking", "attack: 'Hacking' is not one of: " + ", ".join(ATTACK_TYPES)),
    ("/breaches?industry=Atlantis", "industry: 'Atlantis' is not one of: " + ", ".join(DIVISION_NAMES)),
    ("/breaches?since=2020&until=2010", "since (2020) is after until (2010)"),
    ("/breaches?state=Virginia", "state: 'Virginia' is not a two-letter state code, like VA"),
    ("/breaches?country=Japan", "country: Extra inputs are not permitted (got 'Japan')"),
])
def test_a_rejection_is_a_sentence(client, path, said):
    response = client.get(path)
    assert response.status_code == 422
    assert response.json()["detail"] == said


def test_years_outside_the_data_are_refused(client):
    assert client.get(f"/breaches?since={FIRST_YEAR - 1}").status_code == 422
    assert client.get(f"/breaches?until={LAST_YEAR + 1}").status_code == 422


def test_the_map_lists_the_regions_west_to_east(client, us):
    regions = client.get("/map").json()["regions"]
    assert [r["region"] for r in regions] == US_REGIONS
    assert [r["title"] for r in regions] == [region_title(r) for r in US_REGIONS]
    assert {r["region"]: r["color"] for r in regions} == REGION_COLORS
    assert {r["region"]: r["breaches"] for r in regions} == us["region"].value_counts().to_dict()


def test_a_city_pin_counts_the_breaches_headquartered_there(client, us):
    cities = client.get("/map").json()["cities"]
    expected = us.dropna(subset=["city"]).groupby("city").size().to_dict()
    assert {c["city"]: c["breaches"] for c in cities} == expected
    assert [c["breaches"] for c in cities] == sorted((c["breaches"] for c in cities), reverse=True)
    metro = us.dropna(subset=["city"]).drop_duplicates("city").set_index("city")["metro"]
    assert all(c["metro"] == metro[c["city"]] for c in cities)


def test_breaches_finds_a_subsidiary_by_name(client):
    body = client.get("/breaches?company=sam's club").json()
    assert body["total"] >= 1
    assert all(any("sam's club" in t.lower() for t in b["targets"]) or "sam's club" in b["company"].lower()
               for b in body["breaches"])


def test_breaches_lists_the_newest_first_and_counts_them_all(client, us):
    body = client.get("/breaches?attack=Ransomware").json()
    ransomware = us[us["attack_types"].map(lambda items: "Ransomware" in items)]
    assert body["total"] == len(ransomware)
    assert len(body["breaches"]) == BREACH_LIMIT
    dates = [b["disclosed_on"] for b in body["breaches"]]
    assert dates == sorted(dates, reverse=True)
    assert dates[0] == ransomware["disclosed_on"].max().isoformat()


def test_breaches_are_only_ever_american(client, breaches):
    # Weibo is in the export, but only as a Chinese breach. (Not Toyota: an
    # AutoNation dealership named for it is an American breach.)
    assert (breaches["company"] == "WEIBO Corp").any()
    assert client.get("/breaches?company=weibo").json() == {"total": 0, "breaches": []}
    body = client.get("/breaches?limit=50").json()
    assert body["total"] == int((breaches["country"] == US).sum())


def test_breaches_by_ticker(client, us):
    body = client.get("/breaches?ticker=wmt&limit=50").json()
    assert body["total"] == int((us["ticker"] == "WMT").sum())
    assert {b["ticker"] for b in body["breaches"]} == {"WMT"}


@pytest.mark.parametrize("query, keep", [
    ("sec=true", lambda b: b["disclosed_to_sec"]),
    ("region=US West,US Southwest", lambda b: b["region"].isin(["US West", "US Southwest"])),
    ("information=Financial", lambda b: b["information_type"] == "Financial"),
    ("accessed=SSN", lambda b: b["information_accessed"].map(lambda items: "SSN" in items)),
    ("state=va", lambda b: b["state"] == "VA"),
    ("industry=Finance, Insurance, Real Estate&industry=Retail Trade",
     lambda b: b["division"].isin(["Finance, Insurance, Real Estate", "Retail Trade"])),
])
def test_a_filter_counts_exactly_the_breaches_it_names(client, us, query, keep):
    assert client.get(f"/breaches?{query}").json()["total"] == int(keep(us).sum())


def test_a_search_with_like_wildcards_takes_them_literally(client):
    assert client.get("/breaches?company=%25%25").json() == {"total": 0, "breaches": []}


def test_ranks_stand_equal_figures_equally_tall():
    rows = [{"breach_key": k, "x": v} for k, v in enumerate([5.0, 1.0, 5.0, None, -2.0])]
    assert api.ranks(rows, "x") == {0: 1.0, 1: 0.5, 2: 1.0, 4: 0.25}
