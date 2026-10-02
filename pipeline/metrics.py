"""The one list of names: clean.py checks the export against it, publish.py
stores by it, and the API serves by it. A value the export holds that is not
named here is an error in cleaning rather than a row that quietly never
reaches a sheet."""

# Every attack type the export uses, most common first. 'Not Disclosed' is a
# category of its own, not a missing value: a quarter of all breaches carry it,
# and a sheet that dropped them would understate every year.
ATTACK_TYPES = [
    "Unauthorized Access",
    "Malware",
    "Ransomware",
    "Phishing",
    "Misconfiguration",
    "Credential Stuffing",
    "Not Disclosed",
]

# What kind of information a breach exposed. One per breach.
INFORMATION_TYPES = [
    "Personal",
    "Financial",
    "Other",
    "Not Disclosed",
]

# The particular fields a breach exposed. A breach lists any number of these.
INFORMATION_ACCESSED = [
    "Name",
    "SSN",
    "Address",
    "Email",
    "Phone Number",
    "Credit Card",
    "Debit Card",
    "Bank Account",
    "Password",
    "User Name",
    "Intellectual Property",
    "Other",
    "Not Disclosed",
]

# Whether the breached entity was the public company itself or one of its
# subsidiaries. A breach that spans both lists both.
RELATIONSHIPS = [
    "Parent",
    "Subsidiary / Affiliate",
]

REGIONS = [
    "US Mid Atlantic",
    "US New England",
    "US Southeast",
    "US Midwest",
    "US Southwest",
    "US West",
    "Canada",
    "Foreign",
]

MARKETS = [
    "NYSE",
    "NYSE MKT",
    "NASDAQ Global Select Market",
    "NASDAQ Global Market",
    "NASDAQ Capital Market",
    "OTCQX U.S.",
    "OTCQX International",
    "OTCQX International Premier",
    "OTCQB",
    "Pink Current",
    "Pink Limited",
    "Expert Market",
]

# The years breaches were disclosed in. clean.py refuses an export with a
# breach outside them, so a year sheet built on this range never drops one.
FIRST_YEAR = 2004
LAST_YEAR = 2024

# The reported figures a company has each year, by the column they are stored
# under and the title they are drawn with. Monetary figures are in millions of
# dollars, as the fundamentals export reports them; the share price is dollars
# per share. The breach export's own snapshot figures are plain dollars, and
# their columns say so (_usd), so the two units are never one name apart.
FUNDAMENTALS = {
    "assets_musd": "Assets",
    "net_income_musd": "Net Income",
    "price_close": "Share Price",
}

# The years around a breach the before/after sheet shows, relative to the year
# it was disclosed in.
OFFSETS = [-2, -1, 0, 1, 2]

# The sheets the API draws. Each is a fixed pairing of what the rows are and
# what the columns are; filters narrow which breaches are counted, never what
# the axes mean.
VIEWS = {
    "attack_by_year": {
        "title": "Attacks by Year",
        "rows": "attack type",
        "columns": "year",
        "measure": "breaches",
        "description": "How many breaches of each attack type were disclosed each year. "
                       "A breach with two attack types counts once under each.",
    },
    "industry_by_attack": {
        "title": "Industries by Attack",
        "rows": "industry",
        "columns": "attack type",
        "measure": "breaches",
        "description": "How many breaches each industry disclosed, by attack type. "
                       "A breach with two attack types counts once under each.",
    },
    "before_after": {
        "title": "Before and After a Breach",
        "rows": "company",
        "columns": "figure by years from breach",
        "measure": "reported figures",
        "description": "Each breached company's assets and net income (millions of dollars) "
                       "and share price (dollars), from two years before its first breach "
                       "to two years after.",
    },
}

DEFAULT_VIEW = "attack_by_year"

# A hundred companies is 1,500 bars on the before/after sheet. It is the most
# the renderer is asked to draw at once: every bar is its own object with its
# own collider, so the ceiling is a rendering budget rather than a limit on
# what the database will answer.
SHEET_LIMIT = 100

# How many companies each industry contributes to a before/after sheet that
# names no industry. Ranking every breached company by size returns mostly
# manufacturers and banks, which is a leaderboard rather than a comparison; ten
# from each keeps every industry on the sheet.
SHEET_PER = 10
LIMIT_MINIMUM = 1
LIMIT_MAXIMUM = 200

# How many breaches one /breaches answer lists at most. The assistant reads
# them out, so this is a listening budget.
BREACH_LIMIT = 10
BREACH_LIMIT_MAXIMUM = 50

# Each division is a contiguous run of SIC codes: a code belongs to the first
# division whose ceiling it falls under. clean.py labels every company with it,
# and the API reads the label, so an industry means one set of companies
# everywhere.
DIVISIONS = [
    (1000, "Agriculture"),
    (1500, "Mining"),
    (1800, "Construction"),
    (4000, "Manufacturing"),
    (5000, "Transportation & Public Utilities"),
    (5200, "Wholesale Trade"),
    (6000, "Retail Trade"),
    (6800, "Finance, Insurance, Real Estate"),
    (9000, "Services"),
]

# The division holding every code above the last ceiling.
LAST_DIVISION = "Public Administration"


def division(sic):
    if sic is None:
        return None
    for ceiling, name in DIVISIONS:
        if sic < ceiling:
            return name
    return LAST_DIVISION


def division_bounds():
    """(division, lower, upper) per division, lower/upper being None at the
    open ends. Contiguous and total, so every SIC code lands in exactly one."""
    bounds = []
    floor = None
    for ceiling, name in DIVISIONS:
        bounds.append((name, floor, ceiling))
        floor = ceiling
    bounds.append((LAST_DIVISION, floor, None))
    return bounds


DIVISION_NAMES = [name for name, _, _ in division_bounds()]

# One color per division, and the app colors a company's bars by the industry it
# belongs to. Assignment follows DIVISION_NAMES rather than a row's rank, so an
# industry is the same color on every sheet it appears on and a filter that drops
# rows never repaints the survivors.
#
# Any two of these can end up side by side: the user sorts rows at will, so no
# ordering is durable and every pair has to stand on its own. Ten categorical
# colors cannot all be told apart under that — measured worst pairs are dE 2.9
# between Transportation and Agriculture for a deuteranope and 7.1 between
# Manufacturing and Mining for normal vision (OKLab x100, against gates of 8 and
# 15). So colour is a fast way to see that two rows differ in kind, not a
# reliable way to name which kind: every row is labelled, and the assistant
# names the industries on a sheet when asked. A legend is what would fix it.
# Keyed by name rather than zipped against DIVISION_NAMES by position: inserting
# a division into DIVISIONS would otherwise shift every colour below it, which is
# exactly the "same colour on every sheet" promise above failing silently.
DIVISION_COLORS = {
    "Agriculture": "#2a78d6",                       # blue
    "Mining": "#eb6834",                            # orange
    "Construction": "#12a3b4",                      # teal
    "Manufacturing": "#e34948",                     # red
    "Transportation & Public Utilities": "#9b4dca",  # purple
    "Wholesale Trade": "#1baf7a",                   # aqua
    "Retail Trade": "#eda100",                      # yellow
    "Finance, Insurance, Real Estate": "#4a3aa7",   # violet
    "Services": "#008300",                          # green
    "Public Administration": "#e87ba4",             # magenta
}

# What a division outside DIVISION_NAMES is drawn in. The ten above are every
# division division_bounds() can return, so reaching this means the database
# holds a name this file does not know — a sheet drawn in neutral grey says so
# and stays readable, where a lookup that raised would cost the whole request.
UNKNOWN_DIVISION_COLOR = "#8a8a8a"
