"""The one list holds together: the divisions cover every SIC code once, every
state is in one region, and the colours go round."""

from metrics import (
    COUNTRIES,
    DIVISION_NAMES,
    FOREIGN_STATE_CODES,
    METRO_COLORS,
    REGION_COLORS,
    STATE_REGIONS,
    US_REGIONS,
    division,
    division_bounds,
)


def test_divisions_are_contiguous_and_cover_every_code():
    bounds = division_bounds()
    for (_, _, upper), (_, lower, _) in zip(bounds, bounds[1:]):
        assert upper == lower
    for code in range(0, 10000):
        assert division(code) in DIVISION_NAMES


def test_every_foreign_code_names_a_listed_country():
    assert set(FOREIGN_STATE_CODES.values()) <= set(COUNTRIES)


def test_every_state_dc_and_puerto_rico_are_in_one_region():
    assert len(STATE_REGIONS) == 52
    assert set(STATE_REGIONS.values()) == set(US_REGIONS)
    assert (STATE_REGIONS["AK"], STATE_REGIONS["HI"], STATE_REGIONS["PR"]) == ("US West", "US West", "US Southeast")


def test_ten_distinct_metro_colours_and_one_per_region():
    assert len(METRO_COLORS) == len(set(METRO_COLORS)) == 10
    assert list(REGION_COLORS) == US_REGIONS
    assert len(set(REGION_COLORS.values())) == len(US_REGIONS)
