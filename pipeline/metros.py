"""Metros, and the colour each is drawn in.

A metro is what the map draws as one pin: every city starts as its own circle,
its area its breaches, and a circle whose centre lies inside a larger one is
merged into it, until no centre lies inside another circle. The circles are
measured as the app draws them on the region's own map, so a metro is what that
map can show apart, and metros never reach over a region's border. clean.py
groups the cities once, into each breach's 'metro'; the API colours them from
what the table holds. Both halves are plain Python: the API's container has no
pandas.

    grouped = group(cities, one_breach_km)   {city: metro}
    palette = colors(counts)                 {metro: colour or None}
"""

import math

from metrics import METRO_COLORS, US_REGIONS


def group(cities, one_breach_km):
    """{city: metro} for cities given as {city: (x, y, breaches, population,
    region)}, x and y in the maps' kilometres, and one_breach_km the radius a
    one-breach pin is drawn at on each region's map, in those kilometres; a
    city of n breaches is sqrt(n) times wider.

    Within each region the largest circle takes in the first smaller one whose
    centre lies inside it, and the merged circle stands at its cities' centre,
    weighted by breaches, as wide as their breaches together; then the largest
    goes again, until nothing more merges. Ties go by name, so the grouping
    never depends on the order the cities arrive in. A metro is named after its
    most populous city, which is the name people know it by: Burlington, MA has
    the most breaches around Boston, but the metro is Boston's. Between cities
    of one size it is named after the one it grew from."""
    metro = {}
    for region in sorted({c[4] for c in cities.values()}):
        unit = one_breach_km[region]
        circles = [{"members": [name], "x": c[0], "y": c[1], "n": max(c[2], 1)}
                   for name, c in cities.items() if c[4] == region]
        while _merge_one(circles, unit):
            pass
        for circle in circles:
            members = circle["members"]
            name = max(members, key=lambda c: (cities[c][3], cities[c][2]))
            metro.update(dict.fromkeys(members, name))
    return metro


def radius(breaches, one_breach_km):
    return one_breach_km * math.sqrt(max(breaches, 1))


def _merge_one(circles, unit):
    """Merges the first circle found inside a larger one, largest first; False
    when there is none."""
    circles.sort(key=lambda m: (-m["n"], min(m["members"])))
    for i, big in enumerate(circles):
        reach = radius(big["n"], unit)
        for small in circles[i + 1:]:
            if math.hypot(small["x"] - big["x"], small["y"] - big["y"]) >= reach:
                continue
            n = big["n"] + small["n"]
            big["x"] = (big["x"] * big["n"] + small["x"] * small["n"]) / n
            big["y"] = (big["y"] * big["n"] + small["y"] * small["n"]) / n
            big["n"] = n
            big["members"] += small["members"]
            circles.remove(small)
            return True
    return False


def colors(counts, palette=METRO_COLORS):
    """{metro: colour, or None for black} from {(metro, region): breaches}.

    Each region's largest metros, as many as there are colours, are coloured,
    counted by their breaches in that region. A metro is one colour everywhere,
    and two metros that share a region never share a colour, so where a metro
    reaches over a border its colour stays clear of the other side's. Colours
    go out largest metro first. A metro every colour is taken from, by the
    metros it shares regions with, is black."""
    present = {}
    for (metro, region), n in counts.items():
        present.setdefault(region, {})[metro] = n

    chosen = set()
    for region in US_REGIONS:
        ranked = sorted(present.get(region, {}).items(), key=lambda item: (-item[1], item[0]))
        chosen.update(metro for metro, _ in ranked[:len(palette)])

    regions_of = {}
    for (metro, region), n in counts.items():
        regions_of.setdefault(metro, set()).add(region)
    total = {}
    for (metro, _), n in counts.items():
        total[metro] = total.get(metro, 0) + n

    assigned = {}
    for metro in sorted(chosen, key=lambda m: (-total[m], m)):
        neighbours = {other for region in regions_of[metro] for other in present[region]}
        taken = {assigned[other] for other in neighbours if assigned.get(other)}
        free = [color for color in palette if color not in taken]
        assigned[metro] = free[0] if free else None
    return {metro: assigned.get(metro) for metro in regions_of}
