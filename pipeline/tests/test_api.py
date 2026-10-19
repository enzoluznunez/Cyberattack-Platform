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


def sheet(client, metro=None, region=None):
    """A sheet as (directives, figure titles, rows), each row (label, figures,
    heights) as the text cells it arrived as: a metro's, a region's, or with
    neither the whole country's."""
    params = {key: value for key, value in (("metro", metro), ("region", region)) if value is not None}
    directives, header, rows = sheetcsv.read(client.get("/sheet", params=params).text)
    n = int(directives["heights"][0])
    return directives, header[1:1 + n], [(row[0], row[1:1 + n], row[1 + n:]) for row in rows]


def test_health_counts_both_tables(client, breaches, fundamentals):
    assert client.get("/health").json() == {
        "status": "ok", "breaches": len(breaches), "company_years": len(fundamentals)}


@pytest.fixture(scope="module")
def charted(us):
    """The US breaches that report every figure: the sheets' rows, which
    cleaning leaves as every US breach."""
    return us[us[list(METRICS)].notna().all(axis=1)]


def test_a_metro_sheet_has_a_row_for_every_breach_in_it_with_a_figure(client, charted):
    counts = charted.dropna(subset=["metro"]).groupby("metro").size().sort_values(ascending=False)
    for metro in [counts.index[0], counts.index[len(counts) // 2], counts.index[-1]]:
        directives, titles, rows = sheet(client, metro)
        assert titles == list(METRICS.values())
        assert len(rows) == counts[metro]
        assert set(directives["metro"]) == {metro}
        assert len(directives["city"]) == len(directives["color"]) == len(rows)


def test_a_row_reports_every_figure(client):
    _, _, rows = sheet(client)
    assert all(all(cells) and all(heights) for _, cells, heights in rows)


def test_the_map_counts_a_citys_breaches_and_those_charted(client, us, charted):
    for city in client.get("/map").json()["cities"]:
        assert city["breaches"] == int((us["city"] == city["city"]).sum()), city["city"]
        assert city["charted"] == int((charted["city"] == city["city"]).sum()), city["city"]


def test_every_us_breach_is_on_a_sheet(client, us, charted):
    assert len(charted) == len(us)
    _, _, rows = sheet(client)
    assert len(rows) == len(us)


def test_a_row_is_called_by_its_ticker_and_disclosure_day(client, charted):
    directives, _, rows = sheet(client, "New York, NY")
    here = charted[charted["metro"] == "New York, NY"]
    expected = sorted(f"{t} {d:%Y/%m/%d}" for t, d in zip(here["ticker"], here["disclosed_on"]))
    assert sorted(label for label, _, _ in rows) == expected


def test_a_citys_rows_stand_together(client):
    directives, _, _ = sheet(client, "New York, NY")
    cities = directives["city"]
    blocks = [c for i, c in enumerate(cities) if i == 0 or cities[i - 1] != c]
    assert len(blocks) == len(set(blocks))


def test_the_cells_are_the_figures_and_none_is_blank(client, charted):
    _, _, rows = sheet(client, "Boston, MA")
    here = charted[charted["metro"] == "Boston, MA"]
    figures = sorted("" if pd.isna(v) else f"{v:.4f}" for v in here["revenue_usd"])
    assert sorted(cells[0] for _, cells, _ in rows) == figures
    for _, cells, heights in rows:
        assert all(cells) and all(heights)


def test_a_height_is_a_place_among_every_us_breach(client, us):
    """The largest figure in the country stands 1 tall, wherever it is, and a
    height is the share of breaches at or below its figure."""
    for column, title in METRICS.items():
        figures = us[column].dropna()
        largest = us.loc[figures.idxmax(), "metro"]
        _, titles, rows = sheet(client, largest)
        at = titles.index(title)
        assert max(float(h[at]) for _, _, h in rows if h[at]) == 1.0
        for _, cells, heights in rows[:5]:
            if cells[at]:
                expected = (figures <= float(cells[at]) + 1e-4).sum() / len(figures)
                assert float(heights[at]) == pytest.approx(expected, abs=1e-4)


def test_a_metros_sheet_is_the_colour_of_its_pin(client):
    pins = {city["metro"]: city["color"] for city in client.get("/map").json()["cities"]}
    for metro in ["New York, NY", "San Jose, CA", "Boston, MA"]:
        directives, _, _ = sheet(client, metro)
        assert set(directives["color"]) == {pins[metro]}


def test_each_region_colours_its_ten_largest_metros_and_blacks_the_rest(client):
    cities = client.get("/map").json()["cities"]
    for region in US_REGIONS:
        size, color = {}, {}
        for city in cities:
            if city["region"] == region:
                size[city["metro"]] = size.get(city["metro"], 0) + city["breaches"]
                color[city["metro"]] = city["color"]
        ranked = sorted(size, key=lambda m: (-size[m], m))
        assert len({color[m] for m in ranked[:10]}) == min(10, len(ranked)), region
        assert OTHER_METRO_COLOR not in {color[m] for m in ranked[:10]}, region
        assert {color[m] for m in ranked[10:]} <= {OTHER_METRO_COLOR}, region


def test_a_region_sheet_has_every_breach_in_it_with_a_figure(client, charted):
    for region in US_REGIONS:
        directives, titles, rows = sheet(client, region=region)
        here = charted[charted["region"] == region]
        assert titles == list(METRICS.values())
        assert len(rows) == len(here), region
        assert len(directives["metro"]) == len(directives["color"]) == len(rows)


def test_a_region_sheet_runs_metro_by_metro_largest_first(client, us):
    region = US_REGIONS[0]
    directives, _, _ = sheet(client, region=region)
    metros = directives["metro"]
    blocks = [m for i, m in enumerate(metros) if i == 0 or metros[i - 1] != m]
    assert len(blocks) == len(set(blocks))
    size = us.groupby("metro").size()
    named = [m for m in blocks if m]
    assert named == sorted(named, key=lambda m: (-size[m], m))


def test_a_region_sheets_rows_are_their_metros_colours(client):
    pins = {city["metro"]: city["color"] for city in client.get("/map").json()["cities"]}
    directives, _, _ = sheet(client, region=US_REGIONS[0])
    for metro, color in zip(directives["metro"], directives["color"]):
        if metro:
            assert color == pins[metro]


def test_the_us_sheet_holds_every_region_in_its_colour_west_to_east(client, charted):
    directives, _, rows = sheet(client)
    assert len(rows) == len(charted)
    colors = directives["color"]
    blocks = [c for i, c in enumerate(colors) if i == 0 or colors[i - 1] != c]
    present = [r for r in US_REGIONS if (charted["region"] == r).any()]
    assert blocks == [REGION_COLORS[r] for r in present]


def test_a_sheets_heights_do_not_depend_on_which_sheet_it_is(client):
    """A breach stands as tall on its metro's sheet as on its region's and the
    country's."""
    _, _, metro_rows = sheet(client, "Boston, MA")
    _, _, region_rows = sheet(client, region="US New England")
    _, _, us_rows = sheet(client)
    region_by = {label: heights for label, _, heights in region_rows}
    us_by = {label: heights for label, _, heights in us_rows}
    for label, _, heights in metro_rows:
        assert region_by[label] == heights == us_by[label]


def test_an_unknown_metro_is_a_404(client):
    response = client.get("/sheet", params={"metro": "Atlantis, ZZ"})
    assert response.status_code == 404


@pytest.mark.parametrize("path, said", [
    ("/sheet?metro=Boston, MA&region=US West",
     "name a metro or a region, not both; name neither for the whole United States"),
    ("/sheet?region=Atlantis", "region: 'Atlantis' is not one of: " + ", ".join(US_REGIONS)),
    ("/sheet?metro=Boston, MA&since=2020", "since: Extra inputs are not permitted (got '2020')"),
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


def test_an_amount_bound_keeps_only_the_breaches_inside_it(client, us):
    body = client.get("/breaches?revenue_min=1e10&net_income_max=0&limit=50").json()
    keep = (us["revenue_usd"] >= 1e10) & (us["net_income_usd"] <= 0)
    assert body["total"] == int(keep.sum())
    assert all(b["revenue_usd"] >= 1e10 and b["net_income_usd"] <= 0 for b in body["breaches"])


def test_a_breach_that_does_not_report_an_amount_fails_a_bound_on_it(client, us):
    body = client.get("/breaches?records_lost_min=0").json()
    assert body["total"] == int(us["records_lost"].notna().sum())


def test_breaches_sort_by_an_amount_either_way(client, us):
    largest = client.get("/breaches?sort=market_cap&limit=5").json()["breaches"]
    assert [b["market_cap_usd"] for b in largest] == sorted(us["market_cap_usd"].dropna(), reverse=True)[:5]
    smallest = client.get("/breaches?sort=market_cap&order=asc&limit=5").json()["breaches"]
    assert [b["market_cap_usd"] for b in smallest] == sorted(us["market_cap_usd"].dropna())[:5]


def test_breaches_filter_by_metro_and_city_in_any_case(client, us):
    assert client.get("/breaches?metro=new york, ny").json()["total"] == int((us["metro"] == "New York, NY").sum())
    city = us["city"].dropna().iloc[0]
    assert client.get("/breaches", params={"city": city.upper()}).json()["total"] == int((us["city"] == city).sum())


@pytest.mark.parametrize("path, said", [
    ("/breaches?revenue_min=5&revenue_max=1", "revenue_min (5) is above revenue_max (1)"),
    ("/query?measure=average", "a average needs 'of', the amount to take it of: " +
     "revenue, net_income, assets, book_value, market_cap, stock_price, records_lost, cost"),
    ("/fundamentals", "name the company by its ticker or by its name, one of the two"),
])
def test_a_new_rejection_is_a_sentence(client, path, said):
    response = client.get(path)
    assert response.status_code == 422
    assert response.json()["detail"] == said


def test_a_query_counts_every_breach_it_matches(client, us):
    body = client.get("/query?attack=Ransomware").json()
    ransomware = us[us["attack_types"].map(lambda items: "Ransomware" in items)]
    assert body["matched"] == len(ransomware)
    assert body["groups"] == [{"key": None, "value": len(ransomware), "breaches": len(ransomware),
                               "companies": ransomware["cik"].nunique(), "reported": 0, "holder": None}]


def test_a_query_groups_and_ranks_largest_first(client, us):
    body = client.get("/query?group=industry&measure=average&of=revenue&limit=100").json()
    expected = us.groupby("division")["revenue_usd"].mean().sort_values(ascending=False)
    assert [g["key"] for g in body["groups"]] == list(expected.index)
    assert [g["value"] for g in body["groups"]] == pytest.approx(list(expected.values))
    assert body["groups_total"] == len(expected)


def test_a_query_on_a_list_counts_a_breach_in_each_group_it_names(client, attacks):
    body = client.get("/query?group=attack&limit=100").json()
    us_attacks = attacks[attacks["country"] == US]
    assert {g["key"]: g["value"] for g in body["groups"]} == us_attacks.groupby("attack").size().to_dict()


def test_a_query_median_and_max_name_the_holder(client, us):
    median = client.get("/query?measure=median&of=assets").json()["groups"][0]
    assert median["value"] == pytest.approx(us["assets_usd"].median())
    top = client.get("/query?measure=max&of=cost").json()["groups"][0]
    holder = us.loc[us["cost_usd"].idxmax()]
    assert top["value"] == pytest.approx(holder["cost_usd"])
    assert top["holder"]["company"] == holder["company"]
    assert top["reported"] == int(us["cost_usd"].notna().sum())


def test_a_query_limits_its_groups_but_counts_them_all(client, us):
    body = client.get("/query?group=state&limit=3&order=asc").json()
    sizes = us.groupby("state").size()
    assert len(body["groups"]) == 3
    assert body["groups_total"] == len(sizes)
    assert [g["value"] for g in body["groups"]] == sorted(sizes)[:3]


def test_match_labels_every_breach_as_its_row_is_titled(client, us):
    body = client.get("/match?revenue_min=1e11").json()
    big = us[us["revenue_usd"] >= 1e11]
    assert body["total"] == len(big)
    assert sorted(body["labels"]) == sorted(f"{t} {d:%Y/%m/%d}" for t, d in zip(big["ticker"], big["disclosed_on"]))
    _, _, rows = sheet(client)
    titles = {label for label, _, _ in rows}
    assert set(body["labels"]) <= titles


def test_fundamentals_give_a_company_year_by_year_in_dollars(client, fundamentals):
    years = client.get("/fundamentals?ticker=wmt").json()["years"]
    walmart = fundamentals[fundamentals["ticker"] == "WMT"].sort_values("year")
    assert [y["year"] for y in years] == list(walmart["year"])
    assert years[-1]["assets_usd"] == pytest.approx(walmart["assets_musd"].iloc[-1] * 1e6)
