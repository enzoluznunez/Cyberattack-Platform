"""Where each breached city is: places.csv, and the script that drafts it.

The breach export gives every headquarters a city but no coordinates, so the
maps need a table from city to latitude and longitude. places.csv is that table,
checked in and edited by hand; clean.py joins every breach to it and refuses an
export naming a city it does not list.

    python places.py --source data     add a row for every city places.csv lacks

New rows are drafted from two free gazetteers, downloaded into data/ (which git
ignores):

    data/geonames/cities500.txt              https://download.geonames.org/export/dump/cities500.zip
                                             (GeoNames, CC BY 4.0)
    data/census/2024_Gaz_zcta_national.txt   https://www2.census.gov/geo/docs/maps-data/data/gazetteer/
                                             2024_Gazetteer/2024_Gaz_zcta_national.zip (public domain)

A city is matched by name within its state or country in GeoNames, the most
populous match winning; a US city GeoNames lacks falls back to its ZIP code's
centre. What neither finds is printed, to be added by hand. Rows already in the
file are never rewritten, so a correction made by hand survives every rerun.
"""

import argparse
import csv
import re
import sys
from pathlib import Path

PLACES_FILE = Path(__file__).with_name("places.csv")
FIELDS = ["country", "state", "city", "place", "lat", "lon", "source"]

GEONAMES = Path("geonames/cities500.txt")
ZCTA = Path("census/2024_Gaz_zcta_national.txt")


def city_key(text):
    """The export's spelling of a city, reduced to what tells two cities apart:
    'NEW YORK,' and 'New York' are one key, as are 'ST. LOUIS' and 'ST LOUIS'."""
    if text is None:
        return None
    key = re.sub(r"[.,]", " ", text.upper())
    key = re.sub(r"\s+", " ", key).strip()
    return key or None


def load(path=PLACES_FILE):
    """{(country, state, city key): (place, lat, lon)}."""
    with open(path, newline="", encoding="utf-8") as f:
        return {(row["country"], row["state"], row["city"]): (row["place"], float(row["lat"]), float(row["lon"]))
                for row in csv.DictReader(f)}


# GeoNames files Canada's provinces under numbered codes; the export uses
# EDGAR's lettered ones.
CANADA_PROVINCES = {"A0": "01", "A1": "02", "A2": "03", "A3": "04", "A4": "05", "A5": "07",
                    "A6": "08", "A7": "09", "A8": "10", "A9": "11", "B0": "12"}

# The words a city's name is abbreviated by, written out the way GeoNames does.
EXPANSIONS = {"ST": "SAINT", "FT": "FORT", "MT": "MOUNT"}


def match_names(key):
    words = key.split()
    expanded = " ".join(EXPANSIONS.get(w, w) for w in words)
    return {key, expanded}


def read_geonames(path):
    """Two lookups, {(iso, admin1 or None, name): (population, name, lat, lon)}:
    places by their own name, and places by an alternate name. Each keeps the
    most populous place per key. The alternate names are asked only after the
    own names, or a large town that lists 'Batavia' among its old names would
    stand in for the village actually called Batavia."""
    own, alternate = {}, {}
    with open(path, encoding="utf-8") as f:
        for line in f:
            p = line.rstrip("\n").split("\t")
            if p[6] != "P":
                continue
            iso, admin1, population = p[8], p[10], int(p[14] or 0)
            record = (population, p[1], float(p[4]), float(p[5]))
            for found, names in ((own, {p[1], p[2]}), (alternate, [n for n in p[3].split(",") if n])):
                for name in names:
                    k = city_key(name)
                    for admin in (admin1, None):
                        slot = (iso, admin, k)
                        if slot not in found or found[slot][0] < population:
                            found[slot] = record
    return own, alternate


def read_zcta(path):
    with open(path, encoding="utf-8") as f:
        rows = csv.reader(f, delimiter="\t")
        next(rows)
        return {r[0].strip(): (float(r[5]), float(r[6])) for r in rows}


def zip5(text):
    digits = re.sub(r"\D", "", (text or "").split("-")[0])
    return digits.zfill(5) if 3 <= len(digits) <= 5 else None


def draft(breaches_raw, geo_dir):
    from metrics import COUNTRIES, country_of

    geonames = read_geonames(geo_dir / GEONAMES)
    zcta = read_zcta(geo_dir / ZCTA)
    places = load() if PLACES_FILE.exists() else {}

    added, unresolved = [], []
    for _, row in breaches_raw.iterrows():
        country = country_of(row["region"].strip(), row["state_code"].strip())
        state, key = row["state_code"].strip(), city_key(row["city"])
        if key is None or (country, state, key) in places:
            continue

        iso = COUNTRIES[country]
        admin = state if iso == "US" else CANADA_PROVINCES.get(state) if iso == "CA" else None
        hit = next((lookup[(iso, admin, name)] for lookup in geonames for name in match_names(key)
                    if (iso, admin, name) in lookup), None)
        if hit:
            _, name, lat, lon = hit
            source = "geonames"
        elif iso == "US" and zip5(row["zip"]) in zcta:
            lat, lon = zcta[zip5(row["zip"])]
            name, source = key.title(), "zcta"
        else:
            unresolved.append((country, state, key, row["zip"].strip()))
            places[(country, state, key)] = None
            continue

        place = f"{name}, {state}" if iso == "US" else name
        places[(country, state, key)] = (place, lat, lon)
        added.append({"country": country, "state": state, "city": key, "place": place,
                      "lat": f"{lat:.4f}", "lon": f"{lon:.4f}", "source": source})

    new_file = not PLACES_FILE.exists()
    with open(PLACES_FILE, "a", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, FIELDS, lineterminator="\n")
        if new_file:
            writer.writeheader()
        writer.writerows(added)
    return added, unresolved


def main():
    import clean

    parser = argparse.ArgumentParser()
    parser.add_argument("--source", default=clean.RAW_SOURCE,
                        help="gs://bucket or a local directory holding the exports (default: %(default)s)")
    parser.add_argument("--geo", default="data", type=Path,
                        help="directory holding the gazetteers (default: %(default)s)")
    args = parser.parse_args()

    breaches_raw, _ = clean.read_raw(args.source)
    added, unresolved = draft(breaches_raw, args.geo)
    print(f"added {len(added)} places to {PLACES_FILE.name}")
    for country, state, key, zip_code in unresolved:
        print(f"  not found, add by hand: {country} / {state} / {key} (zip {zip_code or '-'})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
