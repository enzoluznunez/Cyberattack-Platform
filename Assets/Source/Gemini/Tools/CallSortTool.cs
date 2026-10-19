using System.Collections.Generic;
using System.Threading.Tasks;
using Google.GenAI.Types;
using UnityEngine;

public sealed class CallSortTool : AgenticTool<CallSortTool.Args> {

    public class Args {
        [Doc("Set the whole order at once: the rows or columns in the sequence you want them, each a name or its " +
             "1-based current position. Name them all for a full reorder, or name only the ones to bring to the " +
             "front and the rest keep their current order behind them."), Optional]
        public string[] order;
[Doc("Whether this works on columns or rows. Leave it out when the name you gave already says which."), Values("columns", "rows"), Optional]
        public string axis;
        [Doc("Move one row or column instead: which one to move, by name or 1-based current position."), Optional]
        public string from;
        [Doc("Where that one should end up, 1-based (1 = first)."), Limits(1, 1000), Optional]
        public int? to;
        [Doc("Order by the numbers instead of by name: the tool reads the values itself and ranks the lines. " +
             "Use this for requests like 'sort by market cap' rather than reading the values first."), Optional]
        public By by;
        [Doc("Bring every row whose breach matches these conditions to the front, keeping their order, looked " +
             "up in the breach database: anything a breach is and bounds on any amount, in dollars. Rows only; " +
             "give no other way of ordering with it."), Optional]
        public BreachFilters where;

        // The breaches 'where' named, looked up before the call runs.
        internal BreachMatch matched;
    }

    public class By {
        [Doc("Which number to rank each line by, worked out across the other axis. Give 'measure' or 'line'."),
         Values("sum", "average", "max", "min"), Optional]
        public string measure;
        [Doc("Rank by one line's own cells instead: a row or column on the other axis, by name or 1-based " +
             "position. Give 'measure' or 'line'."), Optional]
        public string line;
        [Doc("'biggest' puts the largest first, 'smallest' puts the smallest first. Defaults to biggest."),
         Values("biggest", "smallest"), Optional]
        public string first;
    }

    protected override bool EditsAreOutcome => true;

    public override FunctionDeclaration Declaration => new FunctionDeclaration {
        Name = "CallSortTool",
        Description = "Reorder the rows or columns, as one undo entry. Pass 'axis' when the names do not say " +
                      "which. Use 'order' whenever more than one line moves; 'from' and 'to' nudge a single line. " +
                      "Pass 'by' to rank by the numbers, which the tool reads itself: 'line' ranks by one row or " +
                      "column ('sort the breaches by market cap'), 'measure' by each line's own sum, average, max " +
                      "or min, which mixes the columns' units, so prefer 'line'. Positions count the lines showing. Ranking by numbers ranks filtered lines too, so " +
                      "their empty places move to where they rank; 'order' and 'from' leave empty places where " +
                      "they are.",
        Parameters = WithNames(ParametersFor(typeof(Args)))
    };

    protected override Task<string> Prepare(Args args) => LookUp(args.where, m => args.matched = m);

    protected override void Run(Args args, Dictionary<string, object> result) {
        var sort = Scene.Sort;
        if (!EnsureToolSelected(ToolType.Sort, sort, result)) return;

        if (args.where != null && (args.order != null || args.by != null || args.from != null || args.to.HasValue)) {
            result["error"] = "'where' orders the rows itself; send it without 'order', 'by', 'from' or 'to'.";
            return;
        }

        string hint = args.order != null && args.order.Length > 0 ? args.order[0] : args.from;
        bool isColumn = false;
        if (args.where == null && !ResolveAxis("sort", args.axis, hint, result, out isColumn)) return;
        string axis = isColumn ? "columns" : "rows";

        var data = Scene.Data;
        if (data == null) { result["error"] = "No data source found in scene."; return; }

        int lineCount = (isColumn ? data.ColumnOrder : data.RowOrder).Count;
        int count = data.GroupCount(isColumn);
        if (count == 0) { result["error"] = $"The sheet has no {axis} to move."; return; }

        result["direction"] = axis;
        string dataset = Scene.DatasetLabel;
        if (dataset != null) result["dataset"] = dataset;

        bool wantsOrder = args.order != null && args.order.Length > 0;
        bool wantsMove = !string.IsNullOrEmpty(args.from) || args.to.HasValue;
        bool wantsBy = args.by != null;
        bool wantsWhere = args.where != null;

        int ways = (wantsOrder ? 1 : 0) + (wantsMove ? 1 : 0) + (wantsBy ? 1 : 0) + (wantsWhere ? 1 : 0);
        if (ways > 1) {
            result["error"] = "Give one of 'order', 'by', or 'from' with 'to'; not more than one.";
            return;
        }
        if (ways == 0) {
            result["error"] = $"Give 'order' with the {axis} in the sequence you want, 'by' to rank them by their " +
                "numbers, or 'from' and 'to' to move one.";
            return;
        }

        int round = Gemini.ToolRoundId;
        int axisSlot = isColumn ? 1 : 0;
        if (lastSortRound[axisSlot] == round) {
            result["error"] = $"Only one reorder can run at a time. Each one shifts the positions of the "
                + $"{axis} around it, so separate calls do not combine into the arrangement you pictured. "
                + $"Send one call with 'order' listing the {axis} in the sequence you want.";
            EchoOrder(data, isColumn, isColumn ? data.ColumnOrder : data.RowOrder, result);
            return;
        }
        int stamped = lastSortRound[axisSlot];
        lastSortRound[axisSlot] = round;

        if (wantsWhere) RunWhere(args, sort, data, result);
        else if (wantsOrder) RunOrder(args, sort, data, isColumn, axis, lineCount, result);
        else if (wantsBy) RunBy(args, sort, data, isColumn, axis, result);
        else RunMove(args, sort, data, isColumn, axis, count, result);

        // A call that failed reordered nothing, so it leaves the round's one
        // reorder for the corrected call.
        if (result.ContainsKey("error")) lastSortRound[axisSlot] = stamped;
    }

    private static readonly int[] lastSortRound = { -1, -1 };

    private static void RunOrder(Args args, SortTool sort, DataSource data, bool isColumn,
        string axis, int lineCount, Dictionary<string, object> result) {

        if (!TryResolveLines(args.order, isColumn, 0, lineCount - 1, result, out List<int> wanted)) return;

        // 'wanted' holds blocks. Emitting each block's lines together keeps the
        // order group-aligned, which is the only shape the dataset will accept.
        IReadOnlyList<int> live = isColumn ? data.ColumnOrder : data.RowOrder;
        int size = data.GroupSize(isColumn);
        var target = new List<int>(lineCount);
        var taken = new HashSet<int>();

        for (int i = 0; i < wanted.Count; i++)
            for (int sIdx = 0; sIdx < size; sIdx++) {
                int key = live[wanted[i] * size + sIdx];
                target.Add(key);
                taken.Add(key);
            }

        for (int v = 0; v < live.Count; v += size) {
            if (taken.Contains(live[v])) continue;
            for (int sIdx = 0; sIdx < size; sIdx++) target.Add(live[v + sIdx]);
        }

        Commit(sort, data, isColumn, axis, live, target, result);
    }

    // The matching rows on the sheet first, in the order they stand, then the rest.
    private static void RunWhere(Args args, SortTool sort, DataSource data, Dictionary<string, object> result) {
        List<int> matching = args.matched.RowsOn(data, result);

        IReadOnlyList<int> live = data.RowOrder;
        var match = new HashSet<int>(matching);
        var target = new List<int>(live.Count);
        foreach (int row in live) if (match.Contains(row)) target.Add(row);
        int first = target.Count;
        if (first == 0) {
            result["note"] = "No row showing on this sheet matches, so nothing moved.";
            return;
        }
        foreach (int row in live) if (!match.Contains(row)) target.Add(row);

        result["broughtToFront"] = first;
        Commit(sort, data, false, "rows", live, target, result);
    }

    private static void Commit(SortTool sort, DataSource data, bool isColumn, string axis,
        IReadOnlyList<int> live, List<int> target, Dictionary<string, object> result,
        IReadOnlyList<int> full = null) {

        if (!sort.SetOrder(isColumn, target, full)) {
            result["note"] = $"The {axis} are already in that order.";
            EchoOrder(data, isColumn, live, result);
            return;
        }

        result["reordered"] = sort.LastReorderedLines;
        EchoOrder(data, isColumn, target, result);
        result["undoable"] = true;
        if (sort.LastReorderedLines == 0)
            result["note"] = $"The {axis} on the sheet were already in this order, so nothing on it moved; " +
                             "only filtered-off ones were reordered, and they come back in their new places.";
        else if (sort.SequenceRunning)
            result["note"] = "The sheet is still animating to this order; it is committed before your next tool call runs.";
    }

    private static void RunBy(Args args, SortTool sort, DataSource data, bool isColumn,
        string axis, Dictionary<string, object> result) {

        bool hasMeasure = !string.IsNullOrWhiteSpace(args.by.measure);
        bool hasLine = !string.IsNullOrWhiteSpace(args.by.line);
        if (hasMeasure == hasLine) {
            result["error"] = "'by' needs exactly one of 'measure' or 'line'.";
            return;
        }

        // Ranking only means something within one metric. Ordering the metrics
        // themselves by their numbers would compare different units, and ranking
        // rows across every metric would add dollars to share counts.
        if (data.IsGrouped(isColumn)) {
            result["error"] = "The metrics are in different units, so they cannot be ranked against each other. " +
                "Give 'order' with the metrics in the sequence you want instead.";
            return;
        }
        if (data.IsGrouped(!isColumn) && !hasLine) {
            result["error"] = "Each row spans several metrics in different units, so there is no single number to " +
                "rank them by. Name the metric in 'by.line', such as {line: 'Net Income'}.";
            return;
        }

        string measure = null;
        if (hasMeasure && !TryParseMeasure(args.by.measure, "by", result, out measure)) return;
        bool biggestFirst = !string.Equals(args.by.first, "smallest", System.StringComparison.OrdinalIgnoreCase);

        int across = isColumn ? (data.RowOrder != null ? data.RowOrder.Count : 0)
                              : (data.ColumnOrder != null ? data.ColumnOrder.Count : 0);
        if (across == 0) { result["error"] = $"The sheet has no values to rank the {axis} by."; return; }

        int crossLo = -1, crossHi = -1;
        if (hasLine) {
            if (!TryResolveLine(args.by.line, !isColumn, 0, across - 1, result, out int crossBlock)) return;
            BlockSpan(!isColumn, crossBlock, 0, across - 1, out crossLo, out crossHi);
        }

        // A named metric may cover more than one cell per row, so rank by the
        // measure over its cells; a single cell reads the same either way.
        string crossMeasure = measure ?? "sum";

        IReadOnlyList<int> live = isColumn ? data.ColumnOrder : data.RowOrder;
        var visible = new HashSet<int>(live);

        // Every line is ranked, the filtered ones too, so a line filtered off
        // the sheet comes back where the sort puts it rather than where it was.
        // A line is scored from its cells on the lines of the other axis that
        // are on the sheet, whether or not it is on the sheet itself.
        int lo = hasLine ? crossLo : 0, hi = hasLine ? crossHi : across - 1;
        string scoreBy = hasLine ? (crossLo == crossHi ? "sum" : crossMeasure) : measure;
        var ranked = new List<KeyValuePair<double, int>>();
        var unranked = new List<int>();
        foreach (int line in data.FullOrder(isColumn)) {
            if (TryDataScore(data, isColumn, line, lo, hi, scoreBy, out double score))
                ranked.Add(new KeyValuePair<double, int>(score, line));
            else unranked.Add(line);
        }

        if (!ranked.Exists(r => visible.Contains(r.Value))) {
            result["error"] = $"None of the {axis} had numbers to rank.";
            return;
        }

        // Stable, so equal scores keep the order they had.
        var was = new Dictionary<int, int>(ranked.Count);
        for (int i = 0; i < ranked.Count; i++) was[ranked[i].Value] = i;
        ranked.Sort((a, b) => {
            int by = biggestFirst ? b.Key.CompareTo(a.Key) : a.Key.CompareTo(b.Key);
            return by != 0 ? by : was[a.Value].CompareTo(was[b.Value]);
        });

        var full = new List<int>(ranked.Count + unranked.Count);
        var target = new List<int>(live.Count);
        var scores = new List<object>();
        foreach (var r in ranked) {
            full.Add(r.Value);
            if (!visible.Contains(r.Value)) continue;
            target.Add(r.Value);
            scores.Add(System.Math.Round(r.Key, 2));
        }
        full.AddRange(unranked);
        foreach (int line in unranked) if (visible.Contains(line)) target.Add(line);
        int unrankedShown = target.Count - scores.Count;

        result["rankedBy"] = hasLine
            ? DataSource.GroupLabelAt(data, !isColumn, data.GroupOf(!isColumn, crossLo))
              + (crossLo == crossHi ? "" : $" ({crossMeasure} of its columns)")
            : measure;
        result["first"] = biggestFirst ? "biggest" : "smallest";
        if (scores.Count <= MaxEchoedLines) result["scores"] = scores;
        if (unrankedShown > 0) result["unranked"] = unrankedShown;

        Commit(sort, data, isColumn, axis, live, target, result, full);
    }

    // A line's score by its place in the data, from its cells on the other
    // axis's lines lo..hi as they stand on the sheet: the score a line on the
    // sheet would get, given to a filtered one too.
    private static bool TryDataScore(DataSource data, bool isColumn, int line, int lo, int hi,
        string measure, out double score) {
        IReadOnlyList<int> cross = isColumn ? data.RowOrder : data.ColumnOrder;
        var acc = new MeasureAcc();
        for (int j = System.Math.Max(lo, 0); j <= hi && j < cross.Count; j++) {
            int r = isColumn ? cross[j] : line, c = isColumn ? line : cross[j];
            if (data.HasValue(r, c)) acc.Add(data.GetValue(r, c));
        }
        return acc.TryScore(measure, out score);
    }


    private static void RunMove(Args args, SortTool sort, DataSource data, bool isColumn,
        string axis, int count, Dictionary<string, object> result) {

        if (!args.to.HasValue) { result["error"] = "Provide 'to' with 'from'."; return; }
        string what = DataSource.GroupNoun(data, isColumn);
        int size = data.GroupSize(isColumn);
        var pre = new List<int>(isColumn ? data.ColumnOrder : data.RowOrder);

        if (!TryResolveLine(args.from, isColumn, 0, pre.Count - 1, result, out int fromPos)) return;
        int toPos = Mathf.Clamp(args.to.Value - 1, 0, count - 1);
        string clampNote = toPos == args.to.Value - 1 ? null
            : $"The {axis} run 1 to {count}, so position {args.to.Value} became {toPos + 1}.";

        result["from"] = fromPos + 1;
        result["to"] = toPos + 1;

        if (toPos == fromPos) {
            result["note"] = clampNote != null
                ? clampNote + $" That {what} is already there."
                : $"That {what} is already at position {toPos + 1}.";
            EchoOrder(data, isColumn, pre, result);
            return;
        }

        if (!sort.MoveLine(isColumn, fromPos, toPos)) {
            result["error"] = $"That {what} could not be moved.";
            return;
        }

        if (clampNote != null) result["note"] = clampNote;
        DataSource.MoveGroupWithin(pre, fromPos, toPos, size);
        EchoOrder(data, isColumn, pre, result);
        result["undoable"] = true;
    }

    private const int MaxEchoedLines = 40;

    private static void EchoOrder(DataSource data, bool isColumn, IReadOnlyList<int> order,
        Dictionary<string, object> result) {
        if (order == null || order.Count > MaxEchoedLines) return;
        result["order"] = DataSource.BlockTitlesFor(data, isColumn, order);
    }
}
