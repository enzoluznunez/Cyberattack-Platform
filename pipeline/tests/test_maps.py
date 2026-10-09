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
    """Within a few kilometres of the coast, which simplifying it may move."""
    trees, off = {}, []
    for (country, _, city), (place, lat, lon) in places.load().items():
        m = drawn[country]
        frame = maps.frame_of(lon, lat, m["frames"])
        assert frame is not None, f"{place} is in no frame of {country}'s map"
        point = shapely.Point(maps.place(lon, lat, frame))
        tree = trees.setdefault(country, land(m))
        nearest = tree.geometries[tree.nearest(point)]
        if nearest.distance(point) > 5.0:
            off.append((country, city, round(nearest.distance(point), 1)))
    assert off == []
