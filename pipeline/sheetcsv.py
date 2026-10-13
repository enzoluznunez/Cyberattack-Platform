"""The one definition of the sheet CSV the Unity app reads.

/sheet renders every sheet through this, so the format the app parses has one
definition here rather than one per caller.
"""

import csv
import io


def directive(name, values):
    """A directive line whose body is itself CSV, so a value holding a comma —
    'New York, NY' — arrives quoted rather than splitting in two. The parser
    reads it back with the same CSV rules it reads rows with."""
    body = io.StringIO()
    csv.writer(body, lineterminator="").writerow(values)
    return f"#{name} {body.getvalue()}\n"


def split(text):
    """A rendered sheet as (directives, body): the leading '#' lines parsed into
    {name: [values]}, and the CSV that follows. The reader half of render, so a
    caller reads a sheet the way the app does instead of counting lines — adding
    a directive then costs nothing on this side."""
    lines = text.splitlines(keepends=True)
    directives = {}
    at = 0
    for line in lines:
        if not line.startswith("#"):
            break
        name, _, body = line[1:].strip().partition(" ")
        directives[name] = next(csv.reader([body])) if body else []
        at += 1
    return directives, "".join(lines[at:])


def read(text):
    """A rendered sheet as (directives, header, rows) — split() plus the CSV read
    every caller was doing for itself. The body goes through csv.reader rather
    than str.split because company names hold commas and render quotes them, so
    splitting on the delimiter cuts one of those names in half."""
    directives, body = split(text)
    rows = [row for row in csv.reader(io.StringIO(body)) if row]
    return directives, rows[0] if rows else [], rows[1:]


def render(corner, columns, rows):
    """corner: the header's first cell, 'Rows / Columns', which the app reads
    its two axis titles from. columns: the column titles. rows: (label, metro,
    city, colour, values, heights) per row, values one per column and None
    where nothing was reported, heights one per column in (0, 1] or None alike.

    The cells are the figures themselves, so everything that reads the sheet —
    the tooltip, the statistics, the assistant — reads dollars. A bar's height
    is not its figure but its place among every US breach: '#heights N' says
    the last N columns of the header are those heights, one per figure column,
    in the same order. '#metro', '#city' and '#color' carry one value per row,
    in row order, so a row arrives already knowing its metro, the city whose
    pin it stands for on the map, and the colour both are drawn in. They travel
    with the sheet because the only key a row carries is its label.

    A figure is written to four places. Unreported is an empty cell, never
    zero, and the app leaves a gap there.
    """
    rows = list(rows)

    buffer = io.StringIO()
    buffer.write(directive("heights", [len(columns)]))
    buffer.write(directive("metro", [metro or "" for _, metro, _, _, _, _ in rows]))
    buffer.write(directive("city", [city or "" for _, _, city, _, _, _ in rows]))
    buffer.write(directive("color", [color for _, _, _, color, _, _ in rows]))
    writer = csv.writer(buffer, lineterminator="\n")
    writer.writerow([corner, *columns, *columns])
    for label, _, _, _, values, heights in rows:
        writer.writerow([label, *(cell(v) for v in values), *(cell(h) for h in heights)])
    return buffer.getvalue()


def cell(value):
    if value is None:
        return ""
    if isinstance(value, int):
        return str(value)
    return f"{value:.4f}"
