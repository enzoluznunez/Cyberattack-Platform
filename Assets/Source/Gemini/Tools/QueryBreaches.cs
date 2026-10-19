using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Google.GenAI.Types;

// Counts, totals, averages, medians and extremes over the whole database,
// whole or group by group: the questions no one sheet answers, because they
// are about what breaches were (an attack type, an industry, what was taken)
// or reach past the open sheet's place.
public sealed class QueryBreaches : DatabaseTool<QueryBreaches.Args> {

    public class Args : BreachFilters {
        [Optional]
        [Doc("Split the answer into groups and rank them: by industry, attack type, information type, " +
             "information accessed, relationship, region, state, metro, city, market, year, company, ticker, " +
             "sec (disclosed to the SEC or not), auditor or filing type. Leave it out for one answer over every " +
             "breach that matches.")]
        public string group;

        [Optional]
        [Doc("What to work out: 'count' of breaches (the default), 'companies' (how many different companies), " +
             "or the 'sum', 'average', 'median', 'min' or 'max' of the amount 'of' names.")]
        public string measure;

        [Optional]
        [Doc("The amount to measure, for every measure but count and companies.")]
        public string of;

        [Optional]
        [Doc("'desc' ranks the groups largest first (the default), 'asc' smallest first.")]
        public string order;

        [Optional, Limits(1, CyberContract.QueryLimitMaximum), DefaultsTo(CyberContract.QueryLimitDefault)]
        [Doc("How many groups to list. How many there are in all comes back regardless.")]
        public int? limit;
    }

    public override FunctionDeclaration Declaration => new FunctionDeclaration {
        Name = "QueryBreaches",
        Description = "Answer a question about the whole breach database with a number, whatever sheet is open: " +
                      "how many breaches or companies match, or the sum, average, median, smallest or largest of " +
                      "any amount over them, optionally split into groups and ranked. Filter by anything a breach " +
                      "is, including bounds on any amount. Examples: 'how many ransomware breaches were there in " +
                      "2021?' (attack, since, until); 'which industry had the most breaches?' (group industry); " +
                      "'average revenue of breached banks' (industry, measure average, of revenue); 'which state " +
                      "lost the most records?' (group state, measure sum, of records_lost). 'matched' is how many " +
                      "breaches pass the filters; each group gives 'value', its 'breaches' and 'companies', how " +
                      "many 'reported' the amount, and for min and max the 'holder' breach. A breach counts in every " +
                      "group its list names, so attack-type groups can add up to more than 'matched'. Amounts are " +
                      "dollars, per share for stock price, records for records_lost; most breaches report no cost " +
                      "or records lost, so say how many reported. To see the breaches themselves, use FindBreaches.",
        Parameters = BreachFilters.Lists(BreachFilters.Lists(BreachFilters.Lists(
            BreachFilters.Named(ToolArguments.Schema(typeof(Args))),
            "group", CyberContract.Groups), "measure", CyberContract.Measures), "of", CyberContract.Amounts)
    };

    protected override async Task<Dictionary<string, object>> Query(Args bound) {
        string bad = bound.Check()
                     ?? BreachFilters.Canonical(ref bound.group, "group", CyberContract.Groups)
                     ?? BreachFilters.Canonical(ref bound.measure, "measure", CyberContract.Measures)
                     ?? BreachFilters.Canonical(ref bound.of, "amount", CyberContract.Amounts)
                     ?? BreachFilters.Canonical(ref bound.order, "order", new[] { "desc", "asc" });
        if (bad != null) return CyberApi.Fail(bad);

        string parameters = bound.Query();
        if (bound.group != null) parameters += "&group=" + bound.group;
        if (bound.measure != null) parameters += "&measure=" + bound.measure;
        if (bound.of != null) parameters += "&of=" + bound.of;
        if (bound.order == "asc") parameters += "&order=asc";
        if (bound.limit.HasValue) parameters += $"&limit={bound.limit.Value}";
        string query = "/query?" + parameters.TrimStart('&');

        return await CyberApi.Fetch("QueryBreaches", query, (root, result) => {
            result["matched"] = root.GetProperty("matched").GetInt32();
            result["measure"] = bound.measure ?? "count";
            if (bound.of != null) result["of"] = bound.of;
            if (bound.group != null) {
                result["group"] = bound.group;
                result["groupsTotal"] = root.GetProperty("groups_total").GetInt32();
            }

            var groups = new List<object>();
            foreach (JsonElement g in root.GetProperty("groups").EnumerateArray()) {
                var entry = new Dictionary<string, object> {
                    { "value", CyberApi.Number(g, "value") },
                    { "breaches", g.GetProperty("breaches").GetInt32() },
                    { "companies", g.GetProperty("companies").GetInt32() }
                };
                if (bound.group != null) entry["key"] = CyberApi.Text(g, "key") ?? "none given";
                if (bound.of != null) entry["reported"] = g.GetProperty("reported").GetInt32();
                if (g.TryGetProperty("holder", out JsonElement h) && h.ValueKind == JsonValueKind.Object)
                    entry["holder"] = new Dictionary<string, object> {
                        { "company", CyberApi.Text(h, "company") },
                        { "ticker", CyberApi.Text(h, "ticker") },
                        { "disclosed", CyberApi.Text(h, "disclosed_on") }
                    };
                groups.Add(entry);
            }

            // Ungrouped, the answer is the one value, not a list of one.
            if (bound.group == null && groups.Count == 1) {
                foreach (var kv in (Dictionary<string, object>)groups[0]) result[kv.Key] = kv.Value;
            }
            else result["groups"] = groups;
        }).ConfigureAwait(false);
    }
}

// A company's own figures year by year, breached or not: how it stood before
// and after a breach, which no sheet draws.
public sealed class CompanyHistory : DatabaseTool<CompanyHistory.Args> {

    public class Args {
        [Optional, Doc("The company's stock ticker, such as EFX. Give this or 'company'.")]
        public string ticker;

        [Optional, Doc("Part of the company's name, such as 'Equifax'. Give this or 'ticker'.")]
        public string company;
    }

    public override FunctionDeclaration Declaration => new FunctionDeclaration {
        Name = "CompanyHistory",
        Description = "A company's assets, net income and year-end stock price, year by year from 2004 to " +
                      "2023, from the database's company figures rather than any breach. Use it for how a " +
                      "company fared before and after a breach ('did Equifax's net income drop after 2017?'). " +
                      "Amounts are in dollars, the price per share. A name can match several companies; each " +
                      "row says which.",
        Parameters = ToolArguments.Schema(typeof(Args))
    };

    protected override async Task<Dictionary<string, object>> Query(Args bound) {
        bool hasTicker = !string.IsNullOrWhiteSpace(bound.ticker);
        bool hasCompany = !string.IsNullOrWhiteSpace(bound.company);
        if (hasTicker == hasCompany) return CyberApi.Fail("Give the company's 'ticker' or its 'company' name, one of the two.");

        string query = "/fundamentals?" + (hasTicker
            ? "ticker=" + System.Uri.EscapeDataString(bound.ticker.Trim())
            : "company=" + System.Uri.EscapeDataString(bound.company.Trim()));

        return await CyberApi.Fetch("CompanyHistory", query, (root, result) => {
            var years = new List<object>();
            foreach (JsonElement y in root.GetProperty("years").EnumerateArray())
                years.Add(new Dictionary<string, object> {
                    { "ticker", CyberApi.Text(y, "ticker") },
                    { "company", CyberApi.Text(y, "company") },
                    { "year", y.GetProperty("year").GetInt32() },
                    { "assets", CyberApi.Number(y, "assets_usd") },
                    { "netIncome", CyberApi.Number(y, "net_income_usd") },
                    { "stockPrice", CyberApi.Number(y, "stock_price_usd") }
                });
            result["years"] = years;
            if (years.Count == 0) result["note"] = "No company by that name or ticker has figures in the database.";
        }).ConfigureAwait(false);
    }
}
