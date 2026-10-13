"""Metros: which cities are one place, and the colour each is drawn in. Plain
Python on made-up cities, so these need no data."""

from metrics import METRO_COLORS, PALETTE
import metros


import re
from pathlib import Path

from metrics import MAP_BOX_METRES, ONE_BREACH_METRES

WEST = "US West"
SCENE = Path(__file__).resolve().parents[2] / "Assets/Scenes/File Reader.unity"


def city(x, y, breaches, population=1, region=WEST):
    return (x, y, breaches, population, region)


def test_a_smaller_circle_whose_centre_is_inside_a_larger_one_merges_into_it():
    # A 9-breach pin of unit 10 reaches 30 km; B's centre is 20 km off, C's 40.
    cities = {"A": city(0, 0, 9), "B": city(20, 0, 1), "C": city(40, 0, 1)}
    grouped = metros.group(cities, {WEST: 10})
    assert grouped["B"] == "A" and grouped["C"] == "C"


def test_a_merged_circle_grows_and_can_take_in_more():
    """A and B together are 16 breaches, 40 km wide at their centre 2 km from
    A: C at 40 km is just inside it, though it was outside A alone."""
    cities = {"A": city(0, 0, 15), "B": city(30, 0, 1), "C": city(40, 0, 1)}
    grouped = metros.group(cities, {WEST: 10})
    assert grouped["B"] == grouped["C"] == "A"


def test_circles_that_only_overlap_stay_apart():
    """Neither centre lies inside the other, so both pins are still seen."""
    cities = {"A": city(0, 0, 4), "B": city(30, 0, 4)}
    assert metros.group(cities, {WEST: 10}) == {"A": "A", "B": "B"}


def test_metros_never_reach_over_a_regions_border():
    cities = {"A": city(0, 0, 9), "B": city(1, 0, 1, region="US Southwest")}
    grouped = metros.group(cities, {WEST: 10, "US Southwest": 10})
    assert grouped == {"A": "A", "B": "B"}


def test_each_region_measures_pins_at_its_own_maps_scale():
    cities = {"A": city(0, 0, 1), "B": city(15, 0, 1)}
    assert metros.group(cities, {WEST: 10})["B"] == "B"
    assert metros.group(cities, {WEST: 20})["B"] == "A"


def test_a_metro_is_named_after_its_most_populous_city():
    cities = {"Burlington": city(10, 0, 7, 24_000), "Boston": city(0, 0, 6, 650_000)}
    assert metros.group(cities, {WEST: 10}) == {"Burlington": "Boston", "Boston": "Boston"}


def test_the_pins_are_measured_as_the_scene_draws_them():
    """metrics mirrors MapCarousel's mapBox and oneBreachRadius as the scene
    sets them; a change to either side alone would group metros the app does
    not draw."""
    scene = SCENE.read_text()
    box = re.search(r"mapBox: \{x: ([\d.]+), y: ([\d.]+)\}", scene)
    pin = re.search(r"oneBreachRadius: ([\d.]+)", scene)
    assert float(box[1]) == float(box[2]) == MAP_BOX_METRES
    assert float(pin[1]) == ONE_BREACH_METRES


def test_each_region_colours_its_ten_largest_metros_and_blacks_the_rest():
    counts = {(f"m{i}", "US West"): 100 - i for i in range(12)}
    colors = metros.colors(counts)
    assert [colors[f"m{i}"] for i in range(10)] == METRO_COLORS == PALETTE
    assert colors["m10"] is None and colors["m11"] is None


def test_a_metro_across_a_border_keeps_one_colour_unlike_its_neighbours():
    counts = {("New York", "US Mid Atlantic"): 160, ("New York", "US New England"): 12,
              ("Boston", "US New England"): 55, ("Philadelphia", "US Mid Atlantic"): 40}
    colors = metros.colors(counts, PALETTE)
    assert colors["New York"] == PALETTE[0]
    assert colors["Boston"] != colors["New York"]
    assert colors["Philadelphia"] not in (colors["New York"], None)
