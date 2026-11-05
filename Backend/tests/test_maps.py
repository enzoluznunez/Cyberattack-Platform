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


def triangles_of(v, t):
    return shapely.unary_union([Polygon([(v[2 * i], v[2 * i + 1]) for i in t[k:k + 3]])
                                for k in range(0, len(t), 3)])


def test_the_region_colours_cover_the_national_land_exactly(drawn):
    """No white land left between the colours, and no colour past the coast."""
    nation = by_name(drawn)["United States"]
    country = triangles_of(nation["vertices"], nation["triangles"])
    colours = [triangles_of(f["vertices"], f["triangles"]) for f in nation["fills"]]
    assert [f["region"] for f in nation["fills"]] == US_REGIONS
    assert shapely.unary_union(colours).symmetric_difference(country).area < 1.0
    for i, a in enumerate(colours):
        for b in colours[i + 1:]:
            assert a.intersection(b).area < 1.0


def test_each_regions_cell_mesh_is_its_cell(drawn):
    for m in drawn["maps"][1:]:
        rings = [Polygon(list(zip(r[0::2], r[1::2]))) for r in m["cell"]]
        cell = shapely.make_valid(shapely.unary_union(rings))
        drawn_cell = triangles_of(m["cellMesh"]["vertices"], m["cellMesh"]["triangles"])
        assert drawn_cell.symmetric_difference(cell).area < 1e-3 * cell.area, m["name"]


def test_no_lake_is_left_a_speck(drawn):
    for m in drawn["maps"]:
        west, south, east, north = m["bounds"]
        detail = max(east - west, north - south) * maps.DETAIL
        rings = [Polygon(list(zip(r[0::2], r[1::2]))) for r in m["outlines"]]
        for i, ring in enumerate(rings):
            # A lake is a ring inside another; islands are kept by visible().
            if not any(j != i and other.contains(ring.representative_point()) and other.area > ring.area
                       for j, other in enumerate(rings)):
                continue
            assert ring.area >= maps.SPECK_MM2 * detail ** 2 * 0.99, (m["name"], ring.area)
