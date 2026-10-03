"""Cleaning: what the tables hold, checked against the exports themselves."""

import pandas as pd
import pytest

import clean
from metrics import FIRST_YEAR, LAST_YEAR


@pytest.fixture(scope="module")
def sic(raw):
    return clean.company_sic(*raw)


def test_every_breach_survives_cleaning(raw, breaches):
    assert len(breaches) == len(raw[0])
    assert breaches["breach_key"].is_unique


def test_one_row_per_company_year(raw, fundamentals):
    assert not fundamentals.duplicated(["cik", "year"]).any()
    assert len(fundamentals) == len(raw[1].drop_duplicates(["cik", "fyear"]))


def test_pipe_lists_become_lists():
    cells = pd.Series(["|Malware|Phishing|", "|Ransomware|", ""], name="type_of_attack")
    assert clean.items(cells).tolist() == [["Malware", "Phishing"], ["Ransomware"], []]


def test_a_list_cell_holds_what_the_export_listed(raw, breaches):
    by_key = breaches.set_index("breach_key")
    row = raw[0].iloc[0]
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


def test_a_breach_outside_the_year_range_stops_the_rebuild(raw, sic):
    changed = raw[0].copy()
    changed.loc[0, "fyear"] = str(LAST_YEAR + 1)
    changed.loc[0, "date_of_breach_disclosure"] = f"{LAST_YEAR + 1}-01-15"
    with pytest.raises(ValueError, match="outside"):
        clean.breaches(changed, sic)


def test_the_year_range_is_the_data_range(breaches):
    """metrics.FIRST_YEAR and LAST_YEAR are the year sheet's columns; the data
    filling them exactly means no column is empty for want of data."""
    assert breaches["year"].min() == FIRST_YEAR
    assert breaches["year"].max() == LAST_YEAR


def test_unreported_is_missing_not_zero(raw, breaches):
    blank = raw[0]["number_of_records_lost"].str.strip().isin(["", "."]).sum()
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


def test_only_companies_no_export_classifies_lack_an_industry(raw, breaches):
    breaches_raw, fundamentals_raw = raw
    no_code = breaches_raw["sic_code"].str.strip() == ""
    unclassified = breaches_raw[no_code & ~breaches_raw["cik"].isin(fundamentals_raw["cik"])]
    assert breaches["division"].isna().sum() == len(unclassified)
