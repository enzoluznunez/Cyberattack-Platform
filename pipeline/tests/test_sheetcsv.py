"""The sheet CSV, written and read back the way the app reads it."""

import sheetcsv
from metrics import DIVISION_COLORS, UNKNOWN_DIVISION_COLOR


def test_a_plain_sheet_has_no_directives():
    text = sheetcsv.render("Rows / Columns", ["a", "b"], [("one", None, [1, 2])])
    assert text == "Rows / Columns,a,b\none,1,2\n"


def test_counts_are_whole_figures_have_four_places_and_unreported_is_blank():
    text = sheetcsv.render("R / C", ["n", "x", "y"], [("row", None, [3, 1.5, None])])
    assert sheetcsv.read(text)[2] == [["row", "3", "1.5000", ""]]


def test_a_name_with_commas_survives_the_round_trip():
    name = "Finance, Insurance, Real Estate"
    text = sheetcsv.render("Industry / Attack Type", ["Malware"], [(name, name, [4])], colored=True)
    directives, _, rows = sheetcsv.read(text)
    assert directives["industry"] == [name]
    assert directives["color"] == [DIVISION_COLORS[name]]
    assert rows == [[name, "4"]]


def test_an_unknown_industry_is_grey():
    text = sheetcsv.render("R / C", ["n"], [("row", "Atlantis", [1])], colored=True)
    assert sheetcsv.read(text)[0]["color"] == [UNKNOWN_DIVISION_COLOR]


def test_group_is_written_only_when_columns_are_grouped():
    assert "#group" not in sheetcsv.render("R / C", ["a"], [("r", None, [1])])
    assert sheetcsv.read(sheetcsv.render("R / C", ["a 1", "a 2"], [("r", None, [1, 2])], group=2))[0] == {
        "group": ["2"]}
