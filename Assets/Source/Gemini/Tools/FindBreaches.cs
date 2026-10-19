using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Google.GenAI.Types;

// The filters a breach lookup takes: what every database tool (FindBreaches,
// QueryBreaches) and every sheet tool's 'where' names breaches by. Every name
// is checked against the generated contract, the same lists the server
// validates against, so a misheard name is caught here instead of costing a
// round trip. Every breach is at a US company: the database serves no other
// country.
public class BreachFilters {

    [Optional, Limits(CyberContract.FirstYear, CyberContract.LastYear)]
    [Doc("The first year to include, by the year breaches were disclosed.")]
    public int? since;

    [Optional, Limits(CyberContract.FirstYear, CyberContract.LastYear)]
    [Doc("The last year to include, inclusive.")]
    public int? until;

    [Optional]
    [Doc("Industries of the breached companies. A breach in any of them counts.")]
    public string[] industry;

    [Optional]
    [Doc("Attack types. A breach of any of them counts.")]
    public string[] attack;

    [Optional]
    [Doc("The kind of information a breach exposed. A breach of any of these kinds counts.")]
    public string[] information;

    [Optional]
    [Doc("Particular information a breach exposed, such as SSN or Password. A breach that exposed any of them counts.")]
    public string[] accessed;

    [Optional]
    [Doc("Whether the breached entity was the listed company itself ('Parent') or one of its subsidiaries.")]
    public string[] relationship;

    [Optional]
    [Doc("US regions of the companies' headquarters.")]
    public string[] region;

    [Optional]
    [Doc("Stock markets the companies are listed on.")]
    public string[] market;

    [Optional]
    [Doc("Two-letter state codes of the companies' headquarters, such as VA.")]
    public string[] state;

    [Optional]
    [Doc("true for only breaches disclosed to the SEC, false for only those that were not.")]
    public bool? sec;

    [Optional]
    [Doc("Metros, by the names the sheets and map give them, such as 'New York, NY'.")]
    public string[] metro;

    [Optional]
    [Doc("Cities of the companies' headquarters, such as 'Seattle'.")]
    public string[] city;

    [Optional]
    [Doc("Part of a company's name, or of a breached subsidiary's or brand's, such as 'Equifax' or " +
         "'Sam's Club'. Case does not matter.")]
    public string company;

    [Optional]
    [Doc("A stock ticker, such as WMT.")]
    public string ticker;

    // Bounds on an amount, inclusive. Dollars are whole dollars: 10 million is
    // 10000000. A breach that does not report an amount fails a bound on it.
    [Optional, Doc("Smallest revenue, in dollars (10 million is 10000000).")] public double? revenue_min;
    [Optional, Doc("Largest revenue, in dollars.")] public double? revenue_max;
    [Optional, Doc("Smallest net income, in dollars; negative is a loss.")] public double? net_income_min;
    [Optional, Doc("Largest net income, in dollars; 0 keeps only losses and break-evens.")] public double? net_income_max;
    [Optional, Doc("Smallest assets, in dollars.")] public double? assets_min;
    [Optional, Doc("Largest assets, in dollars.")] public double? assets_max;
    [Optional, Doc("Smallest book value, in dollars; negative is possible.")] public double? book_value_min;
    [Optional, Doc("Largest book value, in dollars.")] public double? book_value_max;
    [Optional, Doc("Smallest market cap, in dollars.")] public double? market_cap_min;
    [Optional, Doc("Largest market cap, in dollars.")] public double? market_cap_max;
    [Optional, Doc("Smallest stock price, in dollars per share.")] public double? stock_price_min;
    [Optional, Doc("Largest stock price, in dollars per share.")] public double? stock_price_max;
    [Optional, Doc("Fewest records lost. Most breaches do not report it, and those fail this bound.")] public double? records_lost_min;
    [Optional, Doc("Most records lost.")] public double? records_lost_max;
    [Optional, Doc("Smallest cost of the breach, in dollars. Most breaches do not report it, and those fail this bound.")] public double? cost_min;
    [Optional, Doc("Largest cost of the breach, in dollars.")] public double? cost_max;

    // The filters that take names, with the names each takes and the field
    // holding them. The schema lists the names so the model picks from them
    // rather than guessing a spelling.
    private static readonly (string field, string[] names, Func<BreachFilters, string[]> given)[] Vocabulary = {
        ("industry", CyberContract.Industries, f => f.industry),
        ("attack", CyberContract.AttackTypes, f => f.attack),
        ("information", CyberContract.InformationTypes, f => f.information),
        ("accessed", CyberContract.InformationAccessed, f => f.accessed),
        ("relationship", CyberContract.Relationships, f => f.relationship),
        ("region", CyberContract.Regions, f => f.region),
        ("market", CyberContract.Markets, f => f.market),
    };

    public static Schema Named(Schema schema) {
        foreach ((string field, string[] names, _) in Vocabulary)
            if (schema.Properties.TryGetValue(field, out Schema property) && property.Items != null)
                property.Items.Enum = new List<string>(names);
        return schema;
    }

    // Lists the names a single-name argument takes, from the contract, so the
    // model picks one rather than guessing.
    public static Schema Lists(Schema schema, string field, string[] names) {
        if (schema.Properties.TryGetValue(field, out Schema property)) property.Enum = new List<string>(names);
        return schema;
    }

    // 'value' in the contract's own spelling, or why it is not one of 'names'.
    public static string Canonical(ref string value, string field, string[] names) {
        if (value == null) return null;
        string given = value.Trim();
        string found = Array.Find(names, n => string.Equals(n, given, StringComparison.OrdinalIgnoreCase));
        if (found == null) return $"'{value}' is not one of the {field} names: {string.Join(", ", names)}.";
        value = found;
        return null;
    }

    // Puts every name into the contract's own spelling, or says why one cannot
    // be: the reason lists what would have worked.
    public string Check() {
        if (since.HasValue && until.HasValue && since > until)
            return $"'since' ({since}) is after 'until' ({until}).";
        foreach (int? year in new[] { since, until })
            if (year.HasValue && (year < CyberContract.FirstYear || year > CyberContract.LastYear))
                return $"Breaches run from {CyberContract.FirstYear} to {CyberContract.LastYear}; {year} is outside them.";

        foreach ((string field, string[] names, var values) in Vocabulary) {
            string[] given = values(this);
            if (given == null) continue;
            for (int i = 0; i < given.Length; i++) {
                given[i] ??= "";
                string bad = Canonical(ref given[i], field, names);
                if (bad != null) return bad;
            }
        }

        if (state != null)
            for (int i = 0; i < state.Length; i++) {
                string code = state[i]?.Trim() ?? "";
                if (code.Length != 2) return $"'{state[i]}' is not a two-letter state code, like VA.";
                state[i] = code.ToUpperInvariant();
            }
        return null;
    }

    // The filters as query parameters, each name its own parameter, so a name
    // holding a comma ('Finance, Insurance, Real Estate') stays one name.
    public string Query() {
        var query = new StringBuilder();
        if (since.HasValue) query.Append("&since=").Append(since.Value);
        if (until.HasValue) query.Append("&until=").Append(until.Value);
        foreach ((string field, _, var values) in Vocabulary)
            Append(query, field, values(this));
        Append(query, "state", state);
        if (sec.HasValue) query.Append("&sec=").Append(sec.Value ? "true" : "false");
        Append(query, "metro", metro);
        Append(query, "city", city);
        if (!string.IsNullOrWhiteSpace(company)) Append(query, "company", new[] { company.Trim() });
        if (!string.IsNullOrWhiteSpace(ticker)) Append(query, "ticker", new[] { ticker.Trim() });
        foreach (string amount in CyberContract.Amounts)
            foreach (string bound in new[] { "_min", "_max" }) {
                var value = (double?)typeof(BreachFilters).GetField(amount + bound).GetValue(this);
                if (value.HasValue)
                    query.Append('&').Append(amount).Append(bound).Append('=')
                         .Append(value.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            }
        return query.ToString();
    }

    // The query string, without its leading '&', for a path that has no other parameters.
    public string QueryAlone() => Query().TrimStart('&');

    private static void Append(StringBuilder query, string field, string[] values) {
        if (values == null) return;
        foreach (string value in values)
            query.Append('&').Append(field).Append('=').Append(Uri.EscapeDataString(value));
    }

}

public sealed class FindBreaches : DatabaseTool<FindBreaches.Args> {

    public class Args : BreachFilters {
        [Optional]
        [Doc("How to list them: newest first (the default), oldest first, or by an amount, largest first.")]
        public string sort;

        [Optional, Values("desc", "asc")]
        [Doc("With an amount to sort by: 'asc' lists the smallest first instead.")]
        public string order;

        [Optional,
         Limits(1, CyberContract.BreachLimitMaximum),
         DefaultsTo(CyberContract.BreachLimitDefault)]
        [Doc("How many breaches to list. The count of every match comes back regardless.")]
        public int? limit;
    }

    public override FunctionDeclaration Declaration => new FunctionDeclaration {
        Name = "FindBreaches",
        Description = "Look up individual breaches anywhere in the database, whatever sheet is open: which company, " +
                      "which subsidiary or brand was hit, when it was disclosed and discovered, the attack type, " +
                      "what information was exposed, how many records, the cost where it was reported, where it " +
                      "was reported, its metro, and the company's six figures at the breach. Filter by anything " +
                      "a breach is, including bounds on any amount, and list them newest first or by an amount " +
                      "('the five largest breaches by records lost', 'ransomware at companies with revenue over " +
                      "10 billion'). For counts, totals, averages or rankings of groups, use QueryBreaches " +
                      "instead. 'total' is how many breaches match, however many are listed. Most breaches " +
                      "report no cost and many no record count; say so rather than guessing one.",
        Parameters = BreachFilters.Lists(BreachFilters.Named(ToolArguments.Schema(typeof(Args))),
            "sort", CyberContract.BreachSorts)
    };

    protected override async Task<Dictionary<string, object>> Query(Args bound) {
        string bad = bound.Check() ?? BreachFilters.Canonical(ref bound.sort, "sort", CyberContract.BreachSorts);
        if (bad != null) return CyberApi.Fail(bad);

        string parameters = bound.Query();
        if (bound.limit.HasValue) parameters += $"&limit={bound.limit.Value}";
        if (bound.sort != null) parameters += "&sort=" + bound.sort;
        if (bound.order != null) parameters += "&order=" + bound.order;
        string query = "/breaches?" + parameters.TrimStart('&');

        return await CyberApi.Fetch("FindBreaches", query, (root, result) => {
            result["total"] = root.GetProperty("total").GetInt32();
            var breaches = new List<object>();
            foreach (JsonElement b in root.GetProperty("breaches").EnumerateArray())
                breaches.Add(new Dictionary<string, object> {
                    { "company", CyberApi.Text(b, "company") },
                    { "breached", CyberApi.Strings(b.GetProperty("targets")) },
                    { "ticker", CyberApi.Text(b, "ticker") },
                    { "industry", CyberApi.Text(b, "industry") },
                    { "headquarters", Headquarters(b) },
                    { "metro", CyberApi.Text(b, "metro") },
                    { "disclosed", CyberApi.Text(b, "disclosed_on") },
                    { "discovered", CyberApi.Text(b, "discovered_on") },
                    { "attackTypes", CyberApi.Strings(b.GetProperty("attack_types")) },
                    { "informationType", CyberApi.Text(b, "information_type") },
                    { "informationAccessed", CyberApi.Strings(b.GetProperty("information_accessed")) },
                    { "recordsLost", CyberApi.Number(b, "records_lost") },
                    { "costUsd", CyberApi.Number(b, "cost_usd") },
                    { "disclosedToSec", b.GetProperty("disclosed_to_sec").GetBoolean() },
                    { "reportedIn", CyberApi.Text(b, "filing_type") },
                    { "figures", Figures(b) }
                });
            result["breaches"] = breaches;
        }).ConfigureAwait(false);
    }

    // The company's six figures at the breach, as its sheet row holds them.
    private static Dictionary<string, object> Figures(JsonElement breach) => new Dictionary<string, object> {
        { "revenue", CyberApi.Number(breach, "revenue_usd") },
        { "netIncome", CyberApi.Number(breach, "net_income_usd") },
        { "assets", CyberApi.Number(breach, "assets_usd") },
        { "bookValue", CyberApi.Number(breach, "book_value_usd") },
        { "marketCap", CyberApi.Number(breach, "market_cap_usd") },
        { "stockPrice", CyberApi.Number(breach, "stock_price_usd") }
    };

    // 'Boston, MA', or the state alone when the export gave no city.
    private static string Headquarters(JsonElement breach) {
        string city = CyberApi.Text(breach, "city");
        return string.IsNullOrEmpty(city) ? CyberApi.Text(breach, "state") : city;
    }
}
