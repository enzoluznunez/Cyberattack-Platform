using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Google.GenAI.Types;

// The filters a breach lookup takes. Every name is checked against the
// generated contract, the same lists the server validates against, so a
// misheard name is caught here instead of costing a round trip. Every breach
// is at a US company: the database serves no other country.
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
                string canonical = Array.Find(names, n => string.Equals(n, given[i]?.Trim(), StringComparison.OrdinalIgnoreCase));
                if (canonical == null)
                    return $"'{given[i]}' is not one of the {field} names: {string.Join(", ", names)}.";
                given[i] = canonical;
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
        return query.ToString();
    }

    private static void Append(StringBuilder query, string field, string[] values) {
        if (values == null) return;
        foreach (string value in values)
            query.Append('&').Append(field).Append('=').Append(Uri.EscapeDataString(value));
    }

}

public sealed class FindBreaches : Function {

    public class Args : BreachFilters {
        [Optional]
        [Doc("Part of a company's name, or of a breached subsidiary's or brand's, such as 'Equifax' or " +
             "'Sam's Club'. Case does not matter.")]
        public string company;

        [Optional]
        [Doc("A stock ticker, such as WMT.")]
        public string ticker;

        [Optional,
         Limits(1, CyberContract.BreachLimitMaximum),
         DefaultsTo(CyberContract.BreachLimitDefault)]
        [Doc("How many breaches to list, newest first. The count of every match comes back regardless.")]
        public int? limit;
    }

    public override FunctionDeclaration Declaration => new FunctionDeclaration {
        Name = "FindBreaches",
        Description = "Look up individual breaches, newest first: which company, which subsidiary or brand was hit, " +
                      "when it was disclosed and discovered, the attack type, what information was exposed, how " +
                      "many records, the cost where it was reported, and where it was reported. This is the detail " +
                      "no sheet draws. Call it for questions about particular breaches or companies, such as " +
                      "'what happened at Equifax?' or 'what was the biggest ransomware attack in 2021?', and for " +
                      "which companies a sheet's rows are. 'total' is " +
                      "how many breaches match, however many are listed. Most breaches report no cost and many no " +
                      "record count; say so rather than guessing one.",
        Parameters = BreachFilters.Named(ToolArguments.Schema(typeof(Args)))
    };

    protected override async Task<Dictionary<string, object>> Execute(Dictionary<string, object> args) {
        var bound = ToolArguments.Bind(typeof(Args), args, out string bindError) as Args;
        if (bound == null) return CyberApi.Fail(bindError);

        string bad = bound.Check();
        if (bad != null) return CyberApi.Fail(bad);

        string parameters = bound.Query();
        if (!string.IsNullOrWhiteSpace(bound.company))
            parameters += "&company=" + Uri.EscapeDataString(bound.company.Trim());
        if (!string.IsNullOrWhiteSpace(bound.ticker))
            parameters += "&ticker=" + Uri.EscapeDataString(bound.ticker.Trim());
        if (bound.limit.HasValue) parameters += $"&limit={bound.limit.Value}";
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
                    { "disclosed", CyberApi.Text(b, "disclosed_on") },
                    { "discovered", CyberApi.Text(b, "discovered_on") },
                    { "attackTypes", CyberApi.Strings(b.GetProperty("attack_types")) },
                    { "informationType", CyberApi.Text(b, "information_type") },
                    { "informationAccessed", CyberApi.Strings(b.GetProperty("information_accessed")) },
                    { "recordsLost", CyberApi.Number(b, "records_lost") },
                    { "costUsd", CyberApi.Number(b, "cost_usd") },
                    { "disclosedToSec", b.GetProperty("disclosed_to_sec").GetBoolean() },
                    { "reportedIn", CyberApi.Text(b, "filing_type") }
                });
            result["breaches"] = breaches;
        }).ConfigureAwait(false);
    }

    // 'Boston, MA', or the state alone when the export gave no city.
    private static string Headquarters(JsonElement breach) {
        string city = CyberApi.Text(breach, "city");
        return string.IsNullOrEmpty(city) ? CyberApi.Text(breach, "state") : city;
    }
}
