"""Draw the United States and its six regions for the app's maps, once, into the app.

    python maps.py                       rewrite Assets/Resources/Maps/us.json
    python maps.py --preview data/maps   also draw every map to a PNG there, to look at

The outlines are Natural Earth's 1:10m states and lakes (public domain),
downloaded into data/ (which git ignores):

    data/naturalearth-states/ne_10m_admin_1_states_provinces.shp
        https://naciscdn.org/naturalearth/10m/cultural/ne_10m_admin_1_states_provinces.zip
    data/naturalearth-lakes/ne_10m_lakes.shp
        https://naciscdn.org/naturalearth/10m/physical/ne_10m_lakes.zip

A state's outline takes in its lakes, so the lakes large enough to see are cut
back out of it: the Great Lakes, the Great Salt Lake.

There is one projection, the national map's, flat and in kilometres: Lambert
azimuthal equal-area, centred on the lower 48, so an area on the map is its
area on the ground. Alaska, Hawaii and Puerto Rico are moved into the space
below the lower 48, as US maps usually draw them, and Alaska is shrunk to fit.

Each region's map is the national map cut along the region's borders, in the
national map's own kilometres: a city is at one place on every map, and a dot
reaching over a border is cut by it. The West keeps the Alaska and Hawaii
insets, and the Southeast Puerto Rico's. Each map is simplified to what it can
show once the app has fitted it to the panel.

The app places each city's dot from the 'frames': a frame is a projection
centre, the box of longitudes and latitudes it covers, and the scale and
offset that move it into place. The file also lists a few points projected
here, which the app checks itself against.
"""

import argparse
import json
import math
import sys
from pathlib import Path

import shapefile
import shapely
from shapely.geometry import MultiPolygon, Polygon, shape
from shapely.ops import linemerge, unary_union

from metrics import MAP_BOX_METRES, ONE_BREACH_METRES, STATE_REGIONS, US_REGIONS, region_title

STATES = Path("data/naturalearth-states/ne_10m_admin_1_states_provinces.shp")
LAKES = Path("data/naturalearth-lakes/ne_10m_lakes.shp")
TARGET = Path(__file__).resolve().parents[1] / "Assets/Resources/Maps/us.json"

# Kilometres, the sphere's radius by its mean.
RADIUS = 6371.0088

# Each frame is a box of longitude and latitude (west, south, east, north) with
# the scale and the place, in the lower 48's kilometres, its centre is moved
# to; the first frame whose box holds a point draws it. Alaska's box runs past
# 180°, where the Aleutians cross it; its longitudes are taken west of -180
# rather than east of 180.
FRAMES = [
    # Alaska
    {"box": [-190.0, 50.0, -129.0, 72.0], "center": [-152.0, 63.0], "scale": 0.35, "to": [-1750.0, -1450.0]},
    # Hawaii
    {"box": [-161.0, 18.5, -154.5, 22.5], "center": [-157.5, 20.5], "scale": 1.0, "to": [-700.0, -1550.0]},
    # Puerto Rico
    {"box": [-68.0, 17.5, -65.0, 18.7], "center": [-66.5, 18.2], "scale": 1.0, "to": [1550.0, -1600.0]},
    # The lower 48
    {"box": [-126.0, 24.0, -66.0, 50.0], "center": [-96.0, 38.0], "scale": 1.0, "to": [0.0, 0.0]},
]

# The places the maps are checked by: the app projects these itself and warns
# when it lands somewhere else. New York, Anchorage, Honolulu, San Juan.
CHECKS = [(-74.006, 40.7143), (-149.9003, 61.2181), (-157.8583, 21.3069), (-66.0691, 18.4222)]

# How much of a map's longer side one simplification step may move the
# coast: a 400th is about a millimetre once the app fits the map to the panel.
DETAIL = 1 / 400.0


def project(lon, lat, lon0, lat0):
    """Lambert azimuthal equal-area on the sphere, in kilometres east and north
    of (lon0, lat0). The app's MapAtlas.Project is the same formula."""
    p, l = math.radians(lat), math.radians(lon - lon0)
    p0 = math.radians(lat0)
    k = math.sqrt(2.0 / (1.0 + math.sin(p0) * math.sin(p) + math.cos(p0) * math.cos(p) * math.cos(l)))
    return (RADIUS * k * math.cos(p) * math.sin(l),
            RADIUS * k * (math.cos(p0) * math.sin(p) - math.sin(p0) * math.cos(p) * math.cos(l)))


def place(lon, lat, frame):
    x, y = project(lon, lat, *frame["center"])
    s = frame["scale"]
    return frame["to"][0] + s * x, frame["to"][1] + s * y


def unwrap(lon, frame):
    """The longitude as the frame's box takes it: west of -180 for a box that
    reaches past it."""
    return lon - 360.0 if frame["box"][0] < -180 and lon > 0 else lon


def in_box(lon, lat, frame):
    box = frame["box"]
    return box[0] <= unwrap(lon, frame) <= box[2] and box[1] <= lat <= box[3]


def frame_of(lon, lat, frames=FRAMES):
    """The frame a point is drawn in: the first whose box holds it, or None."""
    return next((f for f in frames if in_box(lon, lat, f)), None)


def read_states(path):
    """{state code: outline in longitude and latitude}. Natural Earth files
    Puerto Rico as its own country, with no postal code."""
    reader = shapefile.Reader(str(path))
    found = {}
    for record, geometry in zip(reader.records(), reader.shapes()):
        r = record.as_dict()
        if r["adm0_a3"] == "USA":
            found[r["postal"]] = shape(geometry.__geo_interface__)
        elif r["adm0_a3"] == "PRI":
            found["PR"] = shape(geometry.__geo_interface__)
    return found


def read_lakes(path):
    reader = shapefile.Reader(str(path))
    return shapely.STRtree([shape(s.__geo_interface__).buffer(0) for s in reader.shapes()])


def without_lakes(geometry, lakes):
    near = lakes.geometries.take(lakes.query(geometry))
    return geometry.difference(unary_union(near)) if len(near) else geometry


def polygons(geometry):
    if geometry.is_empty:
        return []
    if isinstance(geometry, Polygon):
        return [geometry]
    return [g for g in getattr(geometry, "geoms", []) if isinstance(g, Polygon)]


def projected(polygon):
    """A polygon of longitudes and latitudes, in its frame's kilometres, or
    None when no frame holds it. A polygon is drawn in one frame."""
    frame = frame_of(*polygon.representative_point().coords[0])
    if frame is None:
        return None

    def ring(coords):
        return [place(unwrap(lon, frame), lat, frame) for lon, lat in coords]

    return Polygon(ring(polygon.exterior.coords), [ring(r.coords) for r in polygon.interiors])


def region_land(states, lakes, region):
    """A region's land, flat and in the national map's kilometres. An island
    no frame holds is left off: the northwestern Hawaiian chain runs a thousand
    kilometres past the main islands, as specks."""
    codes = [code for code, r in STATE_REGIONS.items() if r == region]
    geometry = without_lakes(unary_union([states[code] for code in codes]), lakes)
    parts = [p for p in (projected(p) for p in polygons(geometry)) if p is not None]
    return shapely.make_valid(unary_union(parts))


def detail_of(geometry):
    west, south, east, north = geometry.bounds
    return max(east - west, north - south) * DETAIL


# Islands and lakes smaller than this many square kilometres are left off a
# map whose detail is coarser: a few kilometres across, so Oahu, Nantucket and
# the Florida Keys' larger islands stay on every map and specks go.
SMALLEST_KM2 = 150.0


def visible(geometry, detail):
    """The geometry without the islands and lakes too small to see at this
    detail: they would only add triangles."""
    smallest = min(25 * detail ** 2, SMALLEST_KM2)
    kept = [Polygon(p.exterior, [r for r in p.interiors if Polygon(r).area >= smallest])
            for p in polygons(geometry) if p.area >= smallest]
    return unary_union(kept)


def mesh(land):
    """(vertices, triangles, outlines) for a flat mesh: vertices as x, y pairs
    in kilometres, triangles as vertex indexes, each outline a closed ring."""
    index, vertices, triangles = {}, [], []

    def vertex(x, y):
        key = (round(x, 3), round(y, 3))
        if key not in index:
            index[key] = len(vertices) // 2
            vertices.extend(key)
        return index[key]

    for polygon in polygons(land):
        for triangle in shapely.constrained_delaunay_triangles(polygon).geoms:
            a, b, c = triangle.exterior.coords[:3]
            # Counter-clockwise seen from above, so every face points up.
            if (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]) < 0:
                b, c = c, b
            triangles.extend([vertex(*a), vertex(*b), vertex(*c)])

    return vertices, triangles, rings(land)


def rings(geometry):
    found = []
    for polygon in polygons(geometry):
        for r in [polygon.exterior, *polygon.interiors]:
            found.append([round(v, 3) for xy in r.coords[:-1] for v in xy])
    return found


def lines(geometry):
    """Every line in a geometry, as flat x, y lists."""
    if geometry.is_empty:
        return []
    merged = linemerge(geometry) if geometry.geom_type in ("MultiLineString", "GeometryCollection") else geometry
    parts = getattr(merged, "geoms", [merged])
    return [[round(v, 3) for xy in p.coords for v in xy] for p in parts if p.geom_type == "LineString"]


def nearest_regions(lands):
    """The plane cut between the regions, each point going to the region
    whose land is nearest: a region's own land, and the sea and lakes nearer it
    than any other. A dot standing out over the coast stays whole, and one
    reaching over a border is cut where the border runs, and on along the
    line halfway between the two regions' shores."""
    points, owners = [], []
    for region, land in lands.items():
        # Just inside the region, so two regions sharing a border never share
        # a point, and the line halfway between them is the border.
        inner = land.buffer(-2.0)
        for boundary in [p.exterior for p in polygons(inner)] + [r for p in polygons(inner) for r in p.interiors]:
            steps = max(int(boundary.length / 10.0), 4)
            for k in range(steps):
                points.append(boundary.interpolate(k / steps, normalized=True))
                owners.append(region)

    west, south, east, north = unary_union(list(lands.values())).bounds
    frame = shapely.box(west - 1000, south - 1000, east + 1000, north + 1000)
    cells = shapely.voronoi_polygons(shapely.MultiPoint(points), extend_to=frame)
    tree = shapely.STRtree(points)
    owned = {region: [] for region in lands}
    for cell in cells.geoms:
        inside = tree.query(cell, predicate="contains")
        if len(inside):
            owned[owners[inside[0]]].append(cell)

    every = unary_union(list(lands.values()))
    cells = {region: unary_union([unary_union(owned[region]).difference(every), land])
             for region, land in lands.items()}
    # The cut leaves slivers where two borders nearly meet; a cell is its
    # large pieces, without holes, so the app can clip a dot by one ring.
    return {region: unary_union([Polygon(p.exterior) for p in polygons(cell) if p.area > 1000.0])
            for region, cell in cells.items()}


def one_breach_km(document=None):
    """{region: the radius a one-breach pin is drawn at on that region's map,
    in the maps' kilometres}: the app fits each map into a box MAP_BOX_METRES on
    a side and draws a one-breach pin ONE_BREACH_METRES wide on every map, so
    the larger the region, the more ground a pin covers on it. Read from the
    maps as drawn, TARGET, unless a document is given."""
    document = document or json.loads(TARGET.read_text())
    found = {}
    for m in document["maps"]:
        if m["region"] is None:
            continue
        west, south, east, north = m["bounds"]
        metres_per_km = min(MAP_BOX_METRES / (east - west), MAP_BOX_METRES / (north - south))
        found[m["region"]] = ONE_BREACH_METRES / metres_per_km
    return found


def check_points():
    return [[lon, lat, *[round(v, 3) for v in place(lon, lat, frame_of(lon, lat))]] for lon, lat in CHECKS]


def build(states, lakes):
    lands = {region: region_land(states, lakes, region) for region in US_REGIONS}

    # The national map: the regions simplified together, so neighbours still
    # meet along one border, and the border drawn as a line across the land.
    national_detail = detail_of(unary_union(list(lands.values())))
    together = shapely.coverage_simplify(
        [visible(lands[region], national_detail) for region in US_REGIONS], national_detail)
    simplified = dict(zip(US_REGIONS, together))
    country = unary_union(list(simplified.values()))
    borders = unary_union([simplified[a].boundary.intersection(simplified[b].boundary)
                           for i, a in enumerate(US_REGIONS) for b in US_REGIONS[i + 1:]])
    vertices, triangles, outlines = mesh(country)
    maps = [{"name": "United States", "region": None,
             "bounds": [round(v, 3) for v in country.bounds],
             "vertices": vertices, "triangles": triangles, "outlines": outlines,
             "borders": lines(borders)}]
    print(f"United States  {len(vertices) // 2:6d} vertices {len(triangles) // 3:6d} triangles")

    # Each region on its own, as detailed as its own map can show, and cut out
    # of the plane along its borders for the dots that reach over them.
    cells = nearest_regions(lands)
    for region in US_REGIONS:
        detail = detail_of(lands[region])
        land = visible(lands[region], detail).simplify(detail, preserve_topology=True)
        cell = cells[region].simplify(detail, preserve_topology=True)
        vertices, triangles, outlines = mesh(land)
        maps.append({"name": region_title(region), "region": region,
                     "bounds": [round(v, 3) for v in land.bounds],
                     "vertices": vertices, "triangles": triangles, "outlines": outlines,
                     "cell": rings(cell)})
        print(f"{region_title(region):14s} {len(vertices) // 2:6d} vertices {len(triangles) // 3:6d} triangles")

    return {"radiusKm": RADIUS, "frames": FRAMES, "checks": check_points(), "maps": maps}


def preview(document, directory):
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    from matplotlib.tri import Triangulation

    directory.mkdir(parents=True, exist_ok=True)
    for m in document["maps"]:
        xs, ys = m["vertices"][0::2], m["vertices"][1::2]
        tris = [m["triangles"][i:i + 3] for i in range(0, len(m["triangles"]), 3)]
        figure, axes = plt.subplots(figsize=(8, 6))
        axes.triplot(Triangulation(xs, ys, tris), color="#9ab", linewidth=0.2)
        for ring in m["outlines"]:
            axes.plot(ring[0::2] + ring[:1], ring[1::2] + ring[1:2], color="#234", linewidth=0.6)
        for line in m.get("borders", []):
            axes.plot(line[0::2], line[1::2], color="#c33", linewidth=0.8)
        for ring in m.get("cell", []):
            axes.plot(ring[0::2] + ring[:1], ring[1::2] + ring[1:2], color="#3a3", linewidth=0.4)
        for lon, lat, x, y in document["checks"]:
            axes.plot([x], [y], "o", color="#d33")
        west, south, east, north = m["bounds"]
        axes.set_xlim(west - 100, east + 100)
        axes.set_ylim(south - 100, north + 100)
        axes.set_aspect("equal")
        axes.set_title(m["name"])
        figure.savefig(directory / f"{m['name'].replace(' ', '_')}.png", dpi=110)
        plt.close(figure)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--states", default=STATES, type=Path)
    parser.add_argument("--lakes", default=LAKES, type=Path)
    parser.add_argument("--preview", type=Path, help="directory to draw every map into as a PNG")
    args = parser.parse_args()

    document = build(read_states(args.states), read_lakes(args.lakes))
    TARGET.parent.mkdir(parents=True, exist_ok=True)
    TARGET.write_text(json.dumps(document, separators=(",", ":")))
    print(f"wrote {TARGET} ({TARGET.stat().st_size / 1e6:.1f} MB)")
    if args.preview:
        preview(document, args.preview)
    return 0


if __name__ == "__main__":
    sys.exit(main())
