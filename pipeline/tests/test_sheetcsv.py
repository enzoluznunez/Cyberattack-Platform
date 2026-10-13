"""The sheet CSV, written and read back the way the app reads it."""

import sheetcsv


def rendered():
    return sheetcsv.render("Breach / Figure", ["Revenue", "Assets"], [
        ("TJX (2007-01-22)", "Boston, MA", "Framingham, MA", "#eb6834", [17.5, None], [0.75, None]),
        ("Acme (2020-03-01)", None, None, "#8a8a8a", [3, 2.0], [0.1, 0.2]),
    ])


def test_figures_then_their_heights_with_a_directive_saying_how_many():
    directives, header, rows = sheetcsv.read(rendered())
    assert directives["heights"] == ["2"]
    assert header == ["Breach / Figure", "Revenue", "Assets", "Revenue", "Assets"]
    assert rows[0] == ["TJX (2007-01-22)", "17.5000", "", "0.7500", ""]


def test_whole_numbers_are_whole_and_unreported_is_blank():
    _, _, rows = sheetcsv.read(rendered())
    assert rows[1][1:3] == ["3", "2.0000"]


def test_a_metro_with_a_comma_survives_the_round_trip():
    directives, _, _ = sheetcsv.read(rendered())
    assert directives["metro"] == ["Boston, MA", ""]
    assert directives["city"] == ["Framingham, MA", ""]
    assert directives["color"] == ["#eb6834", "#8a8a8a"]
