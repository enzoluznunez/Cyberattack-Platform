"""Draw each country's outline for the app's maps, once, into the app.

    python maps.py                       rewrite Assets/Resources/Maps/countries.json
    python maps.py --preview data/maps   also draw every map to a PNG there, to look at

The outlines are Natural Earth's 1:10m countries (public domain), downloaded
into data/ (which git ignores):

    data/naturalearth/ne_10m_admin_0_countries.shp
        https://naciscdn.org/naturalearth/10m/cultural/ne_10m_admin_0_countries.zip
    data/naturalearth-lakes/ne_10m_lakes.shp
        https://naciscdn.org/naturalearth/10m/physical/ne_10m_lakes.zip

A country's outline takes in its lakes, so the lakes large enough to see are
cut back out of it: the Great Lakes, Lake Geneva.

Every map is flat and in kilometres, and every map shares one scale: the app
draws a kilometre the same length on all of them, so a larger country is a
larger map. Each country is projected on its own Lambert azimuthal equal-area
projection, centred on it, so its area on the map is its area on the ground.

The United States is drawn as US maps usually are: Alaska, Hawaii and Puerto
Rico are moved into the space below the lower 48, and Alaska is shrunk to fit.
That one map breaks the shared scale, and only in those insets. Everywhere
else, islands far from a country's mainland (the Netherlands' Caribbean
islands, Spain's Canaries) are left off, as are islands too small to see.

The app places each city's dot with the same projection, from the 'frames'
each country lists: a frame is a projection centre, the box of longitudes and
latitudes it covers, and the scale and offset that move it into place. The file
also lists a few points projected here, which the app checks itself against.
"""

import argparse
import json
import math
import sys
from pathlib import Path

import shapefile
import shapely
from shapely.geometry import MultiPolygon, Polygon, shape
from shapely.ops import unary_union

from metrics import COUNTRIES

SOURCE = Path("data/naturalearth/ne_10m_admin_0_countries.shp")
LAKES = Path("data/naturalearth-lakes/ne_10m_lakes.shp")
TARGET = Path(__file__).resolve().parents[1] / "Assets/Resources/Maps/countries.json"

# Kilometres, the sphere's radius by its mean.
RADIUS = 6371.0088

# Natural Earth's code for each country, and any part drawn with it: the
# island of Cyprus is drawn whole.
SHAPES = {
    "US": ["USA", "PRI"], "GB": ["GBR"], "JP": ["JPN"], "CA": ["CAN"], "IE": ["IRL"],
    "BR": ["BRA"], "NL": ["NLD"], "IN": ["IND"], "BM": ["BMU"], "LU": ["LUX"], "CN": ["CHN"],
    "KY": ["CYM"], "MX": ["MEX"], "DE": ["DEU"], "AR": ["ARG"], "TW": ["TWN"], "ZA": ["ZAF"],
    "IT": ["ITA"], "IL": ["ISR"], "BE": ["BEL"], "FI": ["FIN"], "CY": ["CYP", "CYN"],
    "KR": ["KOR"], "HK": ["HKG"], "SG": ["SGP"], "CO": ["COL"], "ID": ["IDN"], "ES": ["ESP"],
    "DK": ["DNK"], "CH": ["CHE"], "PA": ["PAN"],
}

# The United States' insets, as boxes of longitude and latitude (west, south,
# east, north) with the scale and the place, in the lower 48's kilometres,
# their centre is moved to. Alaska's box runs past 180°, where the Aleutians
# cross it; its longitudes are taken west of -180 rather than east of 180.
US_INSETS = [
    {"name": "Alaska", "box": [-190.0, 50.0, -129.0, 72.0], "center": [-152.0, 63.0], "scale": 0.35,
     "to": [-1750.0, -1450.0]},
    {"name": "Hawaii", "box": [-161.0, 18.5, -154.5, 22.5], "center": [-157.5, 20.5], "scale": 1.0,
     "to": [-700.0, -1550.0]},
    {"name": "Puerto Rico", "box": [-68.0, 17.5, -65.0, 18.7], "center": [-66.5, 18.2], "scale": 1.0,
     "to": [1550.0, -1600.0]},
]
US_MAINLAND = {"box": [-126.0, 24.0, -66.0, 50.0], "center": [-96.0, 38.0], "scale": 1.0, "to": [0.0, 0.0]}

# The places each map is checked by: the app projects these itself and warns
# when it lands somewhere else.
CHECKS = {
    "US": [(-74.006, 40.7143), (-149.9003, 61.2181), (-157.8583, 21.3069), (-66.0691, 18.4222)],
    "JP": [(139.6917, 35.6895)], "GB": [(-0.1257, 51.5085)], "BM": [(-64.783, 32.2949)],
}


def project(lon, lat, lon0, lat0):
    """Lambert azimuthal equal-area on the sphere, in kilometres east and north
    of (lon0, lat0). The app's MapProjection.cs is the same formula."""
    p, l = math.radians(lat), math.radians(lon - lon0)
    p0 = math.radians(lat0)
    k = math.sqrt(2.0 / (1.0 + math.sin(p0) * math.sin(p) + math.cos(p0) * math.cos(p) * math.cos(l)))
    return (RADIUS * k * math.cos(p) * math.sin(l),
            RADIUS * k * (math.cos(p0) * math.sin(p) - math.sin(p0) * math.cos(p) * math.cos(l)))


def place(lon, lat, frame):
    x, y = project(lon, lat, *frame["center"])
    s = frame["scale"]
    return frame["to"][0] + s * x, frame["to"][1] + s * y


def in_box(lon, lat, box):
    if box[0] < -180 and lon > 0:
        lon -= 360.0
    return box[0] <= lon <= box[2] and box[1] <= lat <= box[3]


def frame_of(lon, lat, frames):
    """The frame a point is drawn in: the first whose box holds it, or a frame
    with no box, which holds everything. None when no frame holds it."""
    return next((f for f in frames if "box" not in f or in_box(lon, lat, f["box"])), None)


def read_shapes(path):
    reader = shapefile.Reader(str(path))
    found = {}
    for record, geometry in zip(reader.records(), reader.shapes()):
        found[record.as_dict()["ADM0_A3"]] = shape(geometry.__geo_interface__)
    return found


def read_lakes(path):
    reader = shapefile.Reader(str(path))
    return shapely.STRtree([shape(s.__geo_interface__).buffer(0) for s in reader.shapes()])


def without_lakes(geometry, lakes):
    near = lakes.geometries.take(lakes.query(geometry))
    return geometry.difference(unary_union(near)) if len(near) else geometry


def polygons(geometry):
    return list(geometry.geoms) if isinstance(geometry, MultiPolygon) else [geometry]


def projected(polygon, frames):
    """A polygon of longitudes and latitudes, in its frame's kilometres, or
    None when no frame holds it. A polygon is drawn in one frame."""
    frame = frame_of(*polygon.representative_point().coords[0], frames)
    if frame is None:
        return None

    def ring(coords):
        out = []
        for lon, lat in coords:
            if "box" in frame and frame["box"][0] < -180 and lon > 0:
                lon -= 360.0
            out.append(place(lon, lat, frame))
        return out

    return Polygon(ring(polygon.exterior.coords), [ring(r.coords) for r in polygon.interiors])


def frames_for(code, geometry):
    if code == "US":
        insets = [{"box": i["box"], "center": i["center"], "scale": i["scale"], "to": i["to"]} for i in US_INSETS]
        return insets + [dict(US_MAINLAND)]
    # Centred on the largest landmass, so a country's mainland is drawn with
    # the least distortion.
    main = max(polygons(geometry), key=lambda p: p.area).centroid
    return [{"center": [round(main.x, 4), round(main.y, 4)], "scale": 1.0, "to": [0.0, 0.0]}]


def outline(code, geometry):
    """The country's land, flat and in kilometres, with far-off and invisible
    islands left off and the coast simplified to what the app can show."""
    frames = frames_for(code, geometry)
    # A US island no frame holds is left off: the northwestern Hawaiian
    # chain runs a thousand kilometres past the main islands, as specks.
    parts = [p.buffer(0) for p in (projected(p, frames) for p in polygons(geometry)) if p is not None]

    area = sum(p.area for p in parts)
    size = math.sqrt(area)
    tolerance = min(max(size / 400.0, 0.02), 2.5)
    smallest = 25 * tolerance ** 2
    reach = max(0.6 * size, 50.0)

    # Grown out from the mainland, an island at a time: an island stays when
    # it is near any island already kept, so a chain (Indonesia, the Ryukyus)
    # is kept whole while a territory across an ocean is left off.
    seen = sorted((p for p in parts if p.area >= smallest), key=lambda p: -p.area)
    kept = [seen.pop(0)]
    grew = True
    while grew and seen:
        grew = False
        for p in list(seen):
            if code == "US" or any(p.distance(k) <= reach for k in kept):
                kept.append(p)
                seen.remove(p)
                grew = True

    land = unary_union(kept).simplify(tolerance, preserve_topology=True)
    # Lakes too small to see would only add triangles.
    land = unary_union([Polygon(p.exterior, [r for r in p.interiors if Polygon(r).area >= smallest])
                        for p in polygons(land)])
    return frames, land, area


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

    rings = []
    for polygon in polygons(land):
        for r in [polygon.exterior, *polygon.interiors]:
            rings.append([round(v, 3) for xy in r.coords[:-1] for v in xy])
    return vertices, triangles, rings


def build(shapes, lakes):
    maps = []
    for country, code in COUNTRIES.items():
        geometry = without_lakes(unary_union([shapes[a3] for a3 in SHAPES[code]]), lakes)
        frames, land, area = outline(code, geometry)
        vertices, triangles, rings = mesh(land)
        minx, miny, maxx, maxy = land.bounds
        checks = [[lon, lat, *[round(v, 3) for v in place(lon, lat, frame_of(lon, lat, frames))]]
                  for lon, lat in CHECKS.get(code, [])]
        maps.append({
            "country": country, "code": code, "areaKm2": round(area, 1),
            "bounds": [round(v, 3) for v in (minx, miny, maxx, maxy)],
            "frames": frames, "checks": checks,
            "vertices": vertices, "triangles": triangles, "outlines": rings,
        })
        print(f"{code} {country:16s} {maxx - minx:8.0f} x {maxy - miny:6.0f} km  "
              f"{len(vertices) // 2:6d} vertices {len(triangles) // 3:6d} triangles")
    return {"radiusKm": RADIUS, "maps": maps}


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
        for lon, lat, x, y in m["checks"]:
            axes.plot([x], [y], "o", color="#d33")
        axes.set_aspect("equal")
        axes.set_title(f"{m['country']}  ({m['areaKm2']:,.0f} km²)")
        figure.savefig(directory / f"{m['code']}.png", dpi=110)
        plt.close(figure)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", default=SOURCE, type=Path)
    parser.add_argument("--lakes", default=LAKES, type=Path)
    parser.add_argument("--preview", type=Path, help="directory to draw every map into as a PNG")
    args = parser.parse_args()

    document = build(read_shapes(args.source), read_lakes(args.lakes))
    TARGET.parent.mkdir(parents=True, exist_ok=True)
    TARGET.write_text(json.dumps(document, separators=(",", ":")))
    print(f"wrote {TARGET} ({TARGET.stat().st_size / 1e6:.1f} MB)")
    if args.preview:
        preview(document, args.preview)
    return 0


if __name__ == "__main__":
    sys.exit(main())
