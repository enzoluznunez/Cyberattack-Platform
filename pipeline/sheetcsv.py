"""The one definition of the sheet CSV the Unity app reads.

/sheet renders every sheet through this, so the format the app parses has one
definition here rather than one per caller.
"""

import csv
import io

from metrics import DIVISION_COLORS, UNKNOWN_DIVISION_COLOR


def directive(name, values):
    """A directive line whose body is itself CSV, so a value holding a comma —
    'Finance, Insurance, Real Estate' — arrives quoted rather than splitting into
    three. The parser reads it back with the same CSV rules it reads rows with."""
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


def render(corner, columns, rows, group=1, colored=False):
    """corner: the header's first cell, 'Rows / Columns', which the app reads
    its two axis titles from. columns: the column titles. rows: (label,
    industry, values) per row, values one per column, None where nothing was
    reported.

    '#group' tells the parser how many neighbouring columns belong to one
    figure; it takes their shared title prefix as the group's name, so the
    titles of one group have to differ only at the end. '#industry' and
    '#color' carry one value per row, in row order, so a row arrives already
    knowing its industry and the colour it is drawn in. They travel with the
    sheet rather than being looked up afterwards because the only key a row
    carries is its label. A sheet whose rows are not companies or industries
    leaves them out, and the app draws its rows uncoloured.

    A count is a whole number and is written as one; a figure is written to
    four places. Unreported is an empty cell, never zero.
    """
    rows = list(rows)

    buffer = io.StringIO()
    if group > 1:
        buffer.write(directive("group", [group]))
    if colored:
        buffer.write(directive("industry", [industry or "" for _, industry, _ in rows]))
        buffer.write(directive("color", [DIVISION_COLORS.get(industry, UNKNOWN_DIVISION_COLOR)
                                         for _, industry, _ in rows]))
    writer = csv.writer(buffer, lineterminator="\n")
    writer.writerow([corner, *columns])
    for label, _, values in rows:
        writer.writerow([label, *(cell(value) for value in values)])
    return buffer.getvalue()


def cell(value):
    if value is None:
        return ""
    if isinstance(value, int):
        return str(value)
    return f"{value:.4f}"
