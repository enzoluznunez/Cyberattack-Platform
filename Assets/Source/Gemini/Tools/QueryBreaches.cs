using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Google.GenAI.Types;
using UnityEngine;

// The filters every breach query takes, shared by OpenSheet and FindBreaches so
// the two narrow by the same names in the same way. Every name is checked
// against the generated contract, the same lists the server validates against,
// so a misheard name is caught here instead of costing a round trip.
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
    [Doc("Regions of the companies' headquarters.")]
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

    public bool Any =>
        since.HasValue || until.HasValue || sec.HasValue || (state != null && state.Length > 0) ||
        Array.Exists(Vocabulary, v => v.given(this) is { Length: > 0 });

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

    // The filters in words, for the name of a sheet they narrowed.
    public string Summary() {
        var parts = new List<string>();
        if (since.HasValue || until.HasValue) {
            int first = since ?? CyberContract.FirstYear, last = until ?? CyberContract.LastYear;
            parts.Add(first == last ? $"{first}" : $"{first}–{last}");
        }
        foreach ((_, _, var values) in Vocabulary)
            if (values(this) is { Length: > 0 } given) parts.Add(string.Join(", ", given));
        if (state != null && state.Length > 0) parts.Add(string.Join(", ", state));
        if (sec.HasValue) parts.Add(sec.Value ? "disclosed to the SEC" : "not disclosed to the SEC");
        return string.Join("; ", parts);
    }
}

public sealed class ListViews : Function {

    public override FunctionDeclaration Declaration => new FunctionDeclaration {
        Name = "ListViews",
        Description = "List the sheets the breach database can draw — what each one's rows, columns and bars " +
                      "are — and the names every filter takes: the industries, attack types, kinds of " +
                      "information, regions and markets, and the years breaches run over. Each sheet is already " +
                      "listed as a dataset, unfiltered. Call this before OpenSheet or FindBreaches when you need a " +
                      "filter's exact names, or when the user asks what data there is."
    };

    protected override Task<Dictionary<string, object>> Execute(Dictionary<string, object> args) =>
        CyberApi.Fetch("ListViews", "/views", (root, result) => {
            var views = new List<object>();
            foreach (JsonElement view in root.GetProperty("views").EnumerateArray())
                views.Add(new Dictionary<string, object> {
                    { "view", view.GetProperty("view").GetString() },
                    { "title", view.GetProperty("title").GetString() },
                    { "rows", view.GetProperty("rows").GetString() },
                    { "columns", view.GetProperty("columns").GetString() },
                    { "bars", view.GetProperty("measure").GetString() },
                    { "about", view.GetProperty("description").GetString() }
                });
            result["views"] = views;
            result["default"] = root.GetProperty("default").GetString();
            result["years"] = $"{root.GetProperty("first_year").GetInt32()}–{root.GetProperty("last_year").GetInt32()}";

            var filters = new Dictionary<string, object>();
            foreach (JsonProperty filter in root.GetProperty("filters").EnumerateObject())
                filters[filter.Name] = CyberApi.Strings(filter.Value);
            result["filters"] = filters;
        });
}

public sealed class OpenSheet : AgenticTool {

    public class Args : BreachFilters {
        [Doc("Which sheet to draw, as ListViews names it.")]
        public string view;

        [Optional,
         Limits(CyberContract.LimitMinimum, CyberContract.LimitMaximum),
         DefaultsTo(CyberContract.LimitDefault)]
        [Doc("before_after only: how many companies to show, largest first.")]
        public int? limit;

        [Optional,
         Limits(1, CyberContract.LimitMaximum),
         DefaultsTo(CyberContract.PerDefault)]
        [Doc("before_after only, when no industry is named: how many companies each industry contributes.")]
        public int? per;
    }

    public override FunctionDeclaration Declaration => new FunctionDeclaration {
        Name = "OpenSheet",
        Description = "Draw one of the breach database's sheets, narrowed by filters, as a new dataset in the room: " +
                      "only ransomware, only 2018 to 2023, only breaches that exposed SSNs, only one industry, and so " +
                      "on. Filters choose which breaches are counted; they never change what the rows and columns " +
                      "are. With no filters this switches to the sheet already listed rather than drawing a copy. " +
                      "The open dataset is left as it is, and ListDatasets will show the new one afterwards.",
        Parameters = WithViews(BreachFilters.Named(ParametersFor(typeof(Args))))
    };

    private static Schema WithViews(Schema schema) {
        schema.Properties["view"].Enum = new List<string>(CyberContract.Views);
        return schema;
    }

    // The download happens off the main thread in Execute; Run, on the main
    // thread, only hands the finished sheet to the scene.
    private string csv;
    private string label;
    private string listed;

    protected override async Task<Dictionary<string, object>> Execute(Dictionary<string, object> args) {
        var bound = ToolArguments.Bind(typeof(Args), args, out string bindError) as Args;
        if (bound == null) return CyberApi.Fail(bindError);

        string view = Array.Find(CyberContract.Views,
            v => string.Equals(v, bound.view?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (view == null)
            return CyberApi.Fail($"'{bound.view}' is not a sheet; use one of {string.Join(", ", CyberContract.Views)}.");

        string bad = bound.Check();
        if (bad != null) return CyberApi.Fail(bad);

        bool sized = view == "before_after" &&
                     ((bound.limit ?? CyberContract.LimitDefault) != CyberContract.LimitDefault ||
                      (bound.per ?? CyberContract.PerDefault) != CyberContract.PerDefault);

        // Unfiltered, the sheet is the one already listed: switch to it, reading
        // it first if nobody has opened it yet, rather than drawing a duplicate.
        // Filtered, the listed sheet's title names the new one.
        bool unfiltered = !bound.Any && !sized;
        string url = ViewCatalog.SheetUrl(view);
        string title = view;
        Task<bool> reading = null;
        await MainThread.Run(() => {
            var datasets = Scene.Datasets;
            int index = datasets != null ? datasets.IndexOf(url) : -1;
            if (index < 0) return;
            title = datasets.Datasets[index].label;
            if (!unfiltered) return;
            listed = url;
            if (!datasets.Datasets[index].loaded) reading = datasets.EnsureLoaded(index);
        }).ConfigureAwait(false);
        if (reading != null) await reading.ConfigureAwait(false);
        if (listed != null) return await base.Execute(args).ConfigureAwait(false);

        string query = ViewCatalog.SheetPath(view) + bound.Query();
        if (view == "before_after") {
            if (bound.limit.HasValue) query += $"&limit={bound.limit.Value}";
            if (bound.per.HasValue) query += $"&per={bound.per.Value}";
        }

        string narrowed = bound.Summary();
        label = narrowed.Length > 0 ? $"{title}: {narrowed}" : title;

        try {
            csv = await CyberApi.Get(query).ConfigureAwait(false);
        }
        catch (Exception e) {
            Debug.LogWarning($"[OpenSheet] {e.Message}");
            // A 404 is the server saying which filters matched no breach.
            return CyberApi.Fail(e is CyberApi.ApiError { Status: 404 } ? e.Message : CyberApi.Explain(e));
        }

        return await base.Execute(args).ConfigureAwait(false);
    }

    protected override void Run(Dictionary<string, object> args, Dictionary<string, object> result) {
        // The tool is a long-lived singleton, so what Execute fetched is handed
        // over and let go of here rather than pinned until the next call.
        string sheet = csv, name = label, url = listed;
        csv = null;
        label = null;
        listed = null;

        var datasets = Scene.Datasets;
        if (datasets == null) { result["error"] = "No dataset manager in the scene."; return; }

        if (url != null) {
            int index = datasets.IndexOf(url);
            if (index < 0 || !datasets.Datasets[index].loaded) {
                result["error"] = "That sheet could not be read.";
                return;
            }
            datasets.SwitchDataset(index);
            name = datasets.Datasets[index].label;
        }
        else datasets.AddDataset(sheet, name);

        // Inline CSV parses synchronously and a successful load switches to the
        // new dataset, so the parser's own shape is ready to report.
        DataSource data = datasets.Active;
        if (data == null || !data.IsLoaded || datasets.ActiveDataset.label != name) {
            result["error"] = "The sheet came back but could not be read as a dataset.";
            return;
        }

        result["opened"] = name;
        result["rowsAre"] = DataSource.Plural(DataSource.RowNoun(data));
        result["rows"] = data.RowOrder.Count;
        if (data.IsGrouped(true)) {
            result["figures"] = data.GroupCount(true);
            result["columnsPerFigure"] = new List<object>(data.SeriesTitles);
        }
        else {
            result["columnsAre"] = string.IsNullOrEmpty(data.ColumnAxisTitle)
                ? "columns" : DataSource.Plural(data.ColumnAxisTitle.ToLowerInvariant());
            result["columns"] = data.ColumnCount;
        }
        result["note"] = "Row and column numbers now refer to this dataset; read it with DescribeSheet before using numbers.";
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
                      "'what happened at Equifax?' or 'what was the biggest ransomware attack in 2021?'. 'total' is " +
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
}
