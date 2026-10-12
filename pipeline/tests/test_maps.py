"""The maps in the app: every country the data names has one, and every city
the data places lands on its country's land."""

import json

import pytest
import shapely
from shapely.geometry import Polygon

import maps
import places
from metrics import COUNTRIES


@pytest.fixture(scope="module")
def drawn():
    return {m["country"]: m for m in json.loads(maps.TARGET.read_text())["maps"]}


def land(m):
    v, t = m["vertices"], m["triangles"]
    triangles = [Polygon([(v[2 * i], v[2 * i + 1]) for i in t[k:k + 3]]) for k in range(0, len(t), 3)]
    return shapely.STRtree(triangles)


def test_every_country_has_a_map(drawn):
    assert set(drawn) == set(COUNTRIES)
    assert all(drawn[name]["code"] == code for name, code in COUNTRIES.items())


def test_every_place_lands_on_its_country(drawn):
    """Within a millimetre of the coast on the app's map, which simplifying it
    may move: a 400th of the country's longer side, and never under 5 km."""
    trees, off = {}, []
    for (country, _, city), (place, lat, lon) in places.load().items():
        m = drawn[country]
        frame = maps.frame_of(lon, lat, m["frames"])
        assert frame is not None, f"{place} is in no frame of {country}'s map"
        point = shapely.Point(maps.place(lon, lat, frame))
        tree = trees.setdefault(country, land(m))
        nearest = tree.geometries[tree.nearest(point)]
        xs, ys = m["vertices"][0::2], m["vertices"][1::2]
        allowed = max(max(max(xs) - min(xs), max(ys) - min(ys)) / 400.0, 5.0)
        if nearest.distance(point) > allowed:
            off.append((country, city, round(nearest.distance(point), 1)))
    assert off == []
