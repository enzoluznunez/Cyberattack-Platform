"""The one list holds together: every division has a colour, the divisions
cover every SIC code once, and every view names a sheet the API can draw."""

import api
from metrics import (
    COUNTRIES,
    FOREIGN_STATE_CODES,
    DEFAULT_VIEW,
    DIVISION_COLORS,
    DIVISION_NAMES,
    VIEWS,
    division,
    division_bounds,
)


def test_every_division_has_its_own_colour():
    assert set(DIVISION_COLORS) == set(DIVISION_NAMES)
    assert len(set(DIVISION_COLORS.values())) == len(DIVISION_COLORS)


def test_divisions_are_contiguous_and_cover_every_code():
    bounds = division_bounds()
    for (_, _, upper), (_, lower, _) in zip(bounds, bounds[1:]):
        assert upper == lower
    for code in range(0, 10000):
        name = division(code)
        assert name in DIVISION_NAMES


def test_every_view_is_a_sheet_the_api_draws():
    assert set(VIEWS) == set(api.SHEETS)
    assert DEFAULT_VIEW in VIEWS


def test_every_foreign_code_names_a_listed_country():
    assert set(FOREIGN_STATE_CODES.values()) <= set(COUNTRIES)


def test_every_country_has_its_own_map_code():
    codes = list(COUNTRIES.values())
    assert len(set(codes)) == len(codes)
    assert all(len(code) == 2 and code.isupper() for code in codes)
