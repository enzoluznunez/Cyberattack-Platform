using System.Collections.Generic;
using System.Threading.Tasks;
using Google.GenAI.Types;

public sealed class CallFilterTool : AgenticTool<CallFilterTool.Args> {

    public class Args {
        [Doc("Which axis to filter: 'column' or 'row'. Defaults to 'column'. One call filters one axis; " +
             "send a second call for the other."), Optional]
        public string axis;
        [Doc("Things to take off the sheet, by name or by 1-based position among those showing."), Optional]
        public string[] hide;
        [Doc("Things to bring back onto the sheet, by name; hidden ones have no position."), Optional]
        public string[] show;
        [Doc("Show only these and hide every other one on the axis. Use this for 'just show me X and Y'; " +
             "it replaces the filter rather than adding to it."), Optional]
        public string[] only;
        [Doc("Clear the filter on this axis and put everything back on the sheet."), Optional]
        public bool? showAll;
        [Doc("Pick rows by what their breaches are, looked up in the breach database: anything a breach is " +
             "(attack type, industry, year, place, what was taken) and bounds on any amount, in dollars. " +
             "'Show only breaches at companies losing money' is {net_income_max: 0}. Rows only; " +
             "'whereDoes' says what happens to them."), Optional]
        public BreachFilters where;
        [Doc("With 'where': 'only' leaves just the matching rows on the sheet (the default), 'hide' takes them " +
             "off, 'show' brings them back."), Values("only", "hide", "show"), Optional]
        public string whereDoes;

        // The breaches 'where' named, looked up before the call runs.
        internal BreachMatch matched;
    }

    protected override bool EditsAreOutcome => true;

    public override FunctionDeclaration Declaration => new FunctionDeclaration {
        Name = "CallFilterTool",
        Description = "Take columns, or rows with 'axis' 'row', off the sheet or bring them back: 'hide' and " +
                      "'show' change particular ones, 'only' leaves just those named, 'showAll' clears the axis. " +
                      "Nothing else moves: a hidden line's place, title and all, is left empty, and the user pokes " +
                      "it to bring the line back. No other tool can read a hidden line. One call is one undo " +
                      "entry, and at least one row and one column always stay.",
        Parameters = WithNames(ParametersFor(typeof(Args)))
    };

    protected override Task<string> Prepare(Args args) => LookUp(args.where, m => args.matched = m);

    protected override void Run(Args args, Dictionary<string, object> result) {
        var filter = Scene.Filter;
        if (!EnsureToolSelected(ToolType.Filter, filter, result)) return;

        var data = Scene.Data;
        if (data == null || !data.IsLoaded) { result["error"] = "No dataset is open."; return; }

        bool showAll = args.showAll ?? false;
        bool only = args.only != null && args.only.Length > 0;
        bool named = (args.hide != null && args.hide.Length > 0) || (args.show != null && args.show.Length > 0);

        if (args.where != null) {
            if (showAll || only || named) {
                result["error"] = "'where' picks the rows itself; send it without 'hide', 'show', 'only' or 'showAll'.";
                return;
            }
            FilterMatching(args, filter, data, result);
            return;
        }

        bool rows;
        if (!ReadAxis(args.axis, out rows, result)) return;

        if (!showAll && !only && !named) {
            string nouns = DataSource.Plural(DataSource.FilterNoun(data, rows));
            NeedChoice(result, rows ? "rows" : "columns",
                filter.Names(rows),
                $"Say which {nouns} to hide or show.");
            return;
        }

        if (only && (named || showAll)) {
            result["error"] = "'only' already says what the sheet should hold; do not send 'hide', 'show' or 'showAll' with it.";
            return;
        }

        var hidden = new HashSet<int>(showAll
            ? new List<int>()
            : data.HiddenInOrder(rows));

        if (only) {
            List<int> keep = new List<int>();
            if (!Resolve(filter, rows, args.only, result, keep)) return;

            hidden.Clear();
            var kept = new HashSet<int>(keep);
            List<int> all = data.FilterLinesInOrder(rows);
            for (int i = 0; i < all.Count; i++)
                if (!kept.Contains(all[i])) hidden.Add(all[i]);
        }
        else {
            var toHide = new List<int>();
            var toShow = new List<int>();
            if (!Resolve(filter, rows, args.hide, result, toHide)) return;
            if (!Resolve(filter, rows, args.show, result, toShow)) return;

            for (int i = 0; i < toHide.Count; i++) hidden.Add(toHide[i]);
            for (int i = 0; i < toShow.Count; i++) hidden.Remove(toShow[i]);
        }

        // A false return with nothing to say is a filter that was already in
        // force; the timeline shows that as 'changed' false and reads back below.
        bool applied = filter.Apply(rows, new List<int>(hidden), out string refusal);

        if (!applied && refusal != null) {
            result["error"] = refusal;
            return;
        }

        Report(data, rows, result);
    }

    // The columns unless the caller says otherwise, which is what a filter meant
    // before the tool could reach the other axis. 'metric' and 'company' are
    // still understood, from when every sheet was companies by metrics.
    private static bool ReadAxis(string axis, out bool rows, Dictionary<string, object> result) {
        rows = false;
        if (string.IsNullOrWhiteSpace(axis)) return true;

        switch (axis.Trim().ToLowerInvariant()) {
            case "company": case "companies": rows = true; return true;
            case "metric": case "metrics": return true;
        }
        if (TryParseAxis(axis, out bool columns)) { rows = !columns; return true; }
        result["error"] = $"'{axis.Trim()}' is not an axis; say 'column' or 'row'.";
        return false;
    }

    private static bool Resolve(FilterTool filter, bool rows, string[] wanted,
        Dictionary<string, object> result, List<int> into) {

        if (wanted == null) return true;

        for (int i = 0; i < wanted.Length; i++) {
            string name = wanted[i];
            if (string.IsNullOrWhiteSpace(name)) continue;

            if (!filter.TryResolve(rows, name, out int index)) {
                string noun = rows ? "row" : "column";
                result["error"] = $"No single {noun} matches '{name.Trim()}'.";
                result[noun + "s"] = new List<object>(filter.Names(rows));
                return false;
            }
            if (!into.Contains(index)) into.Add(index);
        }
        return true;
    }

    private static void FilterMatching(Args args, FilterTool filter, DataSource data, Dictionary<string, object> result) {
        List<int> matching = args.matched.RowsOn(data, result);

        var match = new HashSet<int>(matching);
        var hidden = new HashSet<int>(data.HiddenInOrder(rows: true));
        switch (args.whereDoes ?? "only") {
            case "only":
                if (match.Count == 0) {
                    result["error"] = "No row on this sheet matches, and at least one row has to stay; nothing was hidden.";
                    return;
                }
                hidden.Clear();
                foreach (int row in data.FilterLinesInOrder(rows: true))
                    if (!match.Contains(row)) hidden.Add(row);
                break;
            case "hide": hidden.UnionWith(match); break;
            case "show": hidden.ExceptWith(match); break;
        }

        bool applied = filter.Apply(true, new List<int>(hidden), out string refusal);
        if (!applied && refusal != null) {
            result["error"] = refusal;
            return;
        }
        Report(data, true, result);
    }

    private static void Report(DataSource data, bool rows, Dictionary<string, object> result) {
        var showing = new List<object>();
        var off = new List<object>();

        foreach (int id in data.FilterLinesInOrder(rows))
            (data.IsHidden(rows, id) ? off : showing).Add(DataSource.FilterLabel(data, rows, id));

        string noun = rows ? "row" : "column";
        result["axis"] = noun;
        result["showingCount"] = showing.Count;
        result["hiddenCount"] = off.Count;
        // A long list costs the conversation more than it tells: the counts
        // stand in for it, and DescribeSheet reads the rows when they matter.
        bool listed = showing.Count + off.Count <= MostListed;
        if (listed) {
            result["showing"] = showing;
            result["hidden"] = off;
        }
        result["note"] = off.Count == 0
            ? $"Every {noun} is on the sheet."
            : $"Nothing moved on the sheet, but positions count only what is showing, so they have shifted; " +
              (listed ? $"the {noun}s on the sheet are the ones listed in 'showing', in that order."
                      : $"call DescribeSheet for the {noun}s now on the sheet before naming one by position.");
    }

    // The most lines a result lists by name.
    private const int MostListed = 60;

}
