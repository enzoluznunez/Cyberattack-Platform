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

# The countries breached companies are headquartered in, most breaches first,
# by the name they are filtered and drawn under and the ISO 3166 code their map
# is filed under. Puerto Rico is drawn on the United States' map, as the export
# files it under a US region.
COUNTRIES = {
    "United States": "US",
    "United Kingdom": "GB",
    "Japan": "JP",
    "Canada": "CA",
    "Ireland": "IE",
    "Brazil": "BR",
    "Netherlands": "NL",
    "India": "IN",
    "Bermuda": "BM",
    "Luxembourg": "LU",
    "China": "CN",
    "Cayman Islands": "KY",
    "Mexico": "MX",
    "Germany": "DE",
    "Argentina": "AR",
    "Taiwan": "TW",
    "South Africa": "ZA",
    "Italy": "IT",
    "Israel": "IL",
    "Belgium": "BE",
    "Finland": "FI",
    "Cyprus": "CY",
    "South Korea": "KR",
    "Hong Kong": "HK",
    "Singapore": "SG",
    "Colombia": "CO",
    "Indonesia": "ID",
    "Spain": "ES",
    "Denmark": "DK",
    "Switzerland": "CH",
    "Panama": "PA",
}

# A foreign headquarters' state code is EDGAR's code for its country. US and
# Canadian ones are a state or province, and their region says which country.
FOREIGN_STATE_CODES = {
    "X0": "United Kingdom",
    "M0": "Japan",
    "L2": "Ireland",
    "D5": "Brazil",
    "P7": "Netherlands",
    "K7": "India",
    "D0": "Bermuda",
    "N4": "Luxembourg",
    "F4": "China",
    "E9": "Cayman Islands",
    "O5": "Mexico",
    "2M": "Germany",
    "C1": "Argentina",
    "F5": "Taiwan",
    "T3": "South Africa",
    "L6": "Italy",
    "L3": "Israel",
    "C9": "Belgium",
    "H9": "Finland",
    "G4": "Cyprus",
    "M5": "South Korea",
    "K3": "Hong Kong",
    "U0": "Singapore",
    "F8": "Colombia",
    "K8": "Indonesia",
    "U3": "Spain",
    "G7": "Denmark",
    "V8": "Switzerland",
    "R1": "Panama",
}


def country_of(region, state_code):
    """The country a headquarters is in, or None for a foreign state code this
    file does not name."""
    if region and region.startswith("US "):
        return "United States"
    if region == "Canada":
        return "Canada"
    return FOREIGN_STATE_CODES.get(state_code)


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
# breach outside them, so a year filter on this range never drops one.
FIRST_YEAR = 2005
LAST_YEAR = 2023

# How many breaches one /breaches answer lists at most. The assistant reads
# them out, so this is a listening budget.
BREACH_LIMIT = 10
BREACH_LIMIT_MAXIMUM = 50

# The amounts a lookup can filter, rank and measure breaches by, {name: column}:
# the six figures a sheet draws, in dollars (stock price per share), and what
# the breach itself lost and cost, which most breaches do not report.
AMOUNTS = {
    "revenue": "revenue_usd",
    "net_income": "net_income_usd",
    "assets": "assets_usd",
    "book_value": "book_value_usd",
    "market_cap": "market_cap_usd",
    "stock_price": "stock_price_usd",
    "records_lost": "records_lost",
    "cost": "cost_usd",
}

# How many groups one /query answer lists at most, and by default.
QUERY_LIMIT = 25
QUERY_LIMIT_MAXIMUM = 100

# The app shows the United States alone. Its regions are the export's own, in
# the order the maps are swiped through: west to east, each region placed by
# the mean longitude of its breaches.
US = "United States"
US_REGIONS = [
    "US West",
    "US Southwest",
    "US Midwest",
    "US Southeast",
    "US Mid Atlantic",
    "US New England",
]

# Which region every state is drawn in. The export files each breach under a
# region, and clean.py refuses one that disagrees with this. Six states hold no
# breach, so the export never names their region; they are drawn with the
# region around them: Maine and Vermont in New England, West Virginia in the
# Mid Atlantic with Virginia and Kentucky, Mississippi in the Southeast,
# South Dakota in the Midwest, Wyoming in the West.
STATE_REGIONS = {
    **dict.fromkeys(["AK", "CA", "CO", "HI", "ID", "MT", "NV", "OR", "UT", "WA", "WY"], "US West"),
    **dict.fromkeys(["AZ", "NM", "OK", "TX"], "US Southwest"),
    **dict.fromkeys(["IA", "IL", "IN", "KS", "MI", "MN", "ND", "NE", "OH", "SD", "WI"], "US Midwest"),
    **dict.fromkeys(["AL", "AR", "FL", "GA", "LA", "MO", "MS", "NC", "PR", "SC", "TN"], "US Southeast"),
    **dict.fromkeys(["DC", "DE", "KY", "MD", "NJ", "NY", "PA", "VA", "WV"], "US Mid Atlantic"),
    **dict.fromkeys(["CT", "MA", "ME", "NH", "RI", "VT"], "US New England"),
}

# What a region is called where the app shows it.
def region_title(region):
    return region.removeprefix("US ")


# The six figures a region's sheet shows for each breach, by the column they
# are stored under and the title they are drawn with. All are the breach
# export's snapshot taken at the breach: plain dollars, the share price in
# dollars per share.
METRICS = {
    "revenue_usd": "Revenue",
    "net_income_usd": "Net Income",
    "assets_usd": "Assets",
    "book_value_usd": "Book Value",
    "market_cap_usd": "Market Cap",
    "stock_price_usd": "Stock Price",
}

# How the app draws a map, which metros.group measures pins against: each map
# is fitted into a box this many metres on a side, and a one-breach pin is this
# many metres across its radius on every map. They are MapCarousel's mapBox and
# oneBreachRadius, as the scene sets them; test_metros checks the two agree.
# Raising the pin's size merges more cities into each metro.
MAP_BOX_METRES = 0.4
ONE_BREACH_METRES = 0.004

# The colours the maps and sheets are drawn in, pins and bars alike.
PALETTE = [
    "#2a78d6",  # blue
    "#eb6834",  # orange
    "#1baf7a",  # green
    "#9b4dca",  # purple
    "#eda100",  # yellow
    "#e34948",  # red
    "#12a3b4",  # teal
    "#e87ba4",  # pink
    "#8c6d31",  # brown
    "#9acd32",  # lime
]

# Each region colours its ten largest metros, on the map and the sheet alike,
# and draws the rest black. One metro is one colour on every map, and two
# metros sharing a region never share one.
METRO_COLORS = PALETTE
OTHER_METRO_COLOR = "#000000"

# The national map and its sheet colour each region instead, as metro colours
# repeat from one region to the next.
REGION_COLORS = dict(zip(US_REGIONS, PALETTE))

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
