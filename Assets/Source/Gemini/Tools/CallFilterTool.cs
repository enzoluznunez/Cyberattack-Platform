using System.Collections.Generic;
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
    }

    protected override bool EditsAreOutcome => true;

    public override FunctionDeclaration Declaration => new FunctionDeclaration {
        Name = "CallFilterTool",
        Description = "Choose what stands on the sheet: which columns, or which rows. A hidden column leaves " +
                      "with every bar it holds \u2014 on a sheet that groups its columns, a whole figure with all " +
                      "its years \u2014 and keeps its place in the arrangement, so bringing it back does not " +
                      "disturb a sort. The same call filters the rows when 'axis' is 'row': a hidden row is off " +
                      "the sheet, and it keeps its place in the arrangement too. " +
                      "Give 'hide' and 'show' to change particular ones, 'only' to leave just the ones named, " +
                      "or 'showAll' to clear the filter on that axis. " +
                      "Everything in one call is one edit on the undo timeline. " +
                      "What is off the sheet is not gone: it comes back with this tool, and no other " +
                      "tool can read it while it is hidden. At least one column and one row always stay on the sheet.",
        Parameters = ParametersFor(typeof(Args))
    };

    protected override void Run(Args args, Dictionary<string, object> result) {
        if (!EnsureToolSelected(ToolType.Filter, result)) return;

        var filter = Scene.Filter;
        if (filter == null) { result["error"] = "Filter tool not found in scene."; return; }

        var data = Scene.Data;
        if (data == null || !data.IsLoaded) { result["error"] = "No dataset is open."; return; }

        bool rows;
        if (!ReadAxis(args.axis, out rows, result)) return;

        bool showAll = args.showAll ?? false;
        bool only = args.only != null && args.only.Length > 0;
        bool named = (args.hide != null && args.hide.Length > 0) || (args.show != null && args.show.Length > 0);

        if (!showAll && !only && !named) {
            string nouns = DataSource.Plural(rows ? DataSource.RowNoun(data) : DataSource.GroupNoun(data, true));
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
            : rows ? data.HiddenRowsInOrder() : data.HiddenGroupsInOrder());

        if (only) {
            List<int> keep = new List<int>();
            if (!Resolve(filter, rows, args.only, result, keep)) return;

            hidden.Clear();
            List<int> all = rows ? data.DataRowsInOrder() : data.DataGroupsInOrder();
            for (int i = 0; i < all.Count; i++)
                if (!keep.Contains(all[i])) hidden.Add(all[i]);
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

        string wanted = axis.Trim().ToLowerInvariant();
        switch (wanted) {
            case "company": case "companies": case "row": case "rows":
                rows = true;
                return true;
            case "metric": case "metrics": case "column": case "columns":
                return true;
            default:
                result["error"] = $"'{axis.Trim()}' is not an axis; say 'column' or 'row'.";
                return false;
        }
    }

    private static bool Resolve(FilterTool filter, bool rows, string[] wanted,
        Dictionary<string, object> result, List<int> into) {

        if (wanted == null) return true;

        for (int i = 0; i < wanted.Length; i++) {
            string name = wanted[i];
            if (string.IsNullOrWhiteSpace(name)) continue;

            if (rows) {
                if (!filter.TryResolve(true, name, out int row)) {
                    result["error"] = $"No single row matches '{name.Trim()}'.";
                    result["rows"] = new List<object>(filter.Names(true));
                    return false;
                }
                if (!into.Contains(row)) into.Add(row);
                continue;
            }

            if (!filter.TryResolve(false, name, out int group)) {
                result["error"] = $"No single column matches '{name.Trim()}'.";
                result["columns"] = new List<object>(filter.Names(false));
                return false;
            }
            if (!into.Contains(group)) into.Add(group);
        }
        return true;
    }

    private static void Report(DataSource data, bool rows, Dictionary<string, object> result) {
        var showing = new List<object>();
        var off = new List<object>();

        if (rows)
            foreach (int row in data.DataRowsInOrder())
                (data.IsRowHidden(row) ? off : showing).Add(DataSource.RowLabelOfData(data, row));
        else
            foreach (int group in data.DataGroupsInOrder())
                (data.IsDataGroupHidden(group) ? off : showing).Add(DataSource.GroupLabelOfData(data, group));

        string noun = rows ? "row" : "column";
        result["axis"] = noun;
        result["showing"] = showing;
        result["hidden"] = off;
        result["note"] = off.Count == 0
            ? $"Every {noun} is on the sheet."
            : $"Positions have shifted; the {noun}s on the sheet are the ones listed in 'showing', in that order.";
    }

}
