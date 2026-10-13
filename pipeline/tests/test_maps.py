"""The maps in the app: the United States and each of its regions, and every
US city the data places lands on both its national and its regional map."""

import json

import pytest
import shapely
from shapely.geometry import Polygon

import maps
import places
from metrics import STATE_REGIONS, US, US_REGIONS, region_title


@pytest.fixture(scope="module")
def drawn():
    return json.loads(maps.TARGET.read_text())


def by_name(drawn):
    return {m["name"]: m for m in drawn["maps"]}


def land(m):
    v, t = m["vertices"], m["triangles"]
    triangles = [Polygon([(v[2 * i], v[2 * i + 1]) for i in t[k:k + 3]]) for k in range(0, len(t), 3)]
    return shapely.STRtree(triangles)


def test_the_nation_comes_first_then_the_regions_west_to_east(drawn):
    assert [m["name"] for m in drawn["maps"]] == ["United States", *(region_title(r) for r in US_REGIONS)]
    assert [m["region"] for m in drawn["maps"]] == [None, *US_REGIONS]


def test_the_national_map_draws_the_borders_between_regions(drawn):
    assert len(by_name(drawn)["United States"]["borders"]) >= 5


def test_every_place_lands_on_its_maps(drawn):
    """On the national map and on its region's, within a millimetre of the
    coast once the app has fitted the map: a 400th of the map's longer side,
    and never under 5 km."""
    maps_by_region = {m["region"]: m for m in drawn["maps"]}
    trees, off = {}, []
    for (country, state, city), (place, lat, lon) in places.load().items():
        if country != US:
            continue
        frame = maps.frame_of(lon, lat)
        assert frame is not None, f"{place} is in no frame"
        point = shapely.Point(maps.place(lon, lat, frame))
        for m in (maps_by_region[None], maps_by_region[STATE_REGIONS[state]]):
            tree = trees.setdefault(m["name"], land(m))
            nearest = tree.geometries[tree.nearest(point)]
            west, south, east, north = m["bounds"]
            allowed = max(max(east - west, north - south) / 400.0, 5.0)
            if nearest.distance(point) > allowed:
                off.append((m["name"], place, round(nearest.distance(point), 1)))
    assert off == []


def test_the_regions_cells_cut_the_plane_without_overlapping(drawn):
    cells = []
    for m in drawn["maps"][1:]:
        rings = [list(zip(r[0::2], r[1::2])) for r in m["cell"]]
        cell = shapely.make_valid(shapely.unary_union([Polygon(r) for r in rings]))
        cells.append(cell)
    for i, a in enumerate(cells):
        for b in cells[i + 1:]:
            assert a.intersection(b).area < 0.01 * min(a.area, b.area)
