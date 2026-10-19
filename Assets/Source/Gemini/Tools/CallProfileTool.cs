using System.Collections.Generic;
using System.Threading.Tasks;
using Google.GenAI.Types;

public sealed class CallProfileTool : AgenticTool<CallProfileTool.Args> {

    public class Args {
        [Doc("Which row or column to pull: its name, or its 1-based position on the sheet."), Optional]
        public string index;
[Doc("Whether this works on columns or rows. Leave it out when the name you gave already says which."), Values("columns", "rows"), Optional]
        public string axis;
        [Doc("Raise several strips in one go: each a name or 1-based position. Use this instead of calling repeatedly."), Optional]
        public string[] indexes;
        [Doc("Pick the line by its numbers instead of by name: the tool reads them itself. " +
             "Use this for requests like 'raise the breach with the highest market cap' rather than reading the values first."), Optional]
        public Of of;
        [Doc("Raise every row whose breach matches these conditions, looked up in the breach database: " +
             "anything a breach is (attack type, industry, year, place, what was taken) and bounds on any " +
             "amount, in dollars. For 'profile all companies with revenue over 10 million' send " +
             "{revenue_min: 10000000}. Rows only; give no other way of picking lines with it."), Optional]
        public BreachFilters where;

        // The breaches 'where' named, looked up before the call runs.
        internal BreachMatch matched;
    }

    public class Of {
        [Doc("Rank each line by one line of the other axis, by name or 1-based position, such as 'Market " +
             "Cap' for the breaches: what 'the highest market cap' means. Give 'line' or 'measure'."), Optional]
        public string line;
        [Doc("Or judge each line by a number worked out across every line of the other axis, which mixes " +
             "the columns' units. Give 'line' or 'measure'."),
         Values("sum", "average", "max", "min"), Optional]
        public string measure;
        [Doc("'highest' takes the biggest, 'lowest' takes the smallest. Defaults to highest."),
         Values("highest", "lowest"), Optional]
        public string pick;
        [Doc("How many lines to raise, best first. Defaults to 1."), Limits(1, 100), Optional]
        public int? count;
    }

    protected override bool EditsAreOutcome => true;

    public override FunctionDeclaration Declaration => new FunctionDeclaration {
        Name = "CallProfileTool",
        Description = "Raise a whole row or column as a strip floating above the sheet, at the sheet's own " +
                      "heights. Pass 'axis' when the name does not say which. A strip stays up after the tool is " +
                      "put away, follows its line through a sort, and is an edit on the undo timeline; any number " +
                      "can stand, and raising one already up leaves it up. Pass 'of' to pick lines by their " +
                      "numbers ('measure', 'pick', and 'count' for the top few): the tool reads them itself, so " +
                      "the top three is one call with no separate read. The columns are in different units, " +
                      "so judging lines across them rarely means much; name the line when you can.",
        Parameters = WithNames(ParametersFor(typeof(Args)))
    };

    // The most rows one 'where' raises: each is a strip of its own, and more
    // than this buries the sheet under them.
    private const int MostStrips = 50;

    protected override Task<string> Prepare(Args args) => LookUp(args.where, m => args.matched = m);

    protected override void Run(Args args, Dictionary<string, object> result) {
        var profile = Scene.Profile;
        if (!EnsureToolSelected(ToolType.Profile, profile, result)) return;

        if (args.where != null) {
            RaiseMatching(args, profile, result);
            return;
        }

        if (!ResolveAxis("profile", args.axis, args.index, result, out bool columns)) return;

        if (!TryResolveTargetBounds(result, out int rowMin, out int rowMax, out int colMin, out int colMax))
            return;

        int lineMin = columns ? colMin : rowMin;
        int lineMax = columns ? colMax : rowMax;
        var wanted = new List<int>();
        if (args.of != null) {
            int crossMin = columns ? rowMin : colMin;
            int crossMax = columns ? rowMax : colMax;
            if (!PickLines(args.of, columns, lineMin, lineMax, crossMin, crossMax, result, wanted)) return;
        }
        else if (args.indexes != null && args.indexes.Length > 1) {
            if (!TryResolveLines(args.indexes, columns, lineMin, lineMax, result, out List<int> many)) return;
            wanted.AddRange(many);
        }
        else {
            string one = args.indexes != null && args.indexes.Length == 1 ? args.indexes[0] : args.index;
            if (!TryResolveLine(one, columns, lineMin, lineMax, result, out int single)) return;
            wanted.Add(single);
        }

        // 'wanted' holds blocks; one strip raises a whole paired metric, so any
        // line inside the block stands for it.
        var data = Scene.Data;
        int blockMin = data != null ? data.GroupOf(columns, lineMin) : lineMin;
        var lines = new List<int>(wanted.Count);
        foreach (int block in wanted) {
            BlockSpan(columns, block, lineMin, lineMax, out int lo, out _);
            lines.Add(lo);
        }

        Raise(profile, columns, lines, rowMin, rowMax, colMin, colMax, blockMin, result);
    }

    // Raises each of 'lines', visible positions on the axis, as one undo entry.
    private static void Raise(ProfileTool profile, bool columns, List<int> lines,
        int rowMin, int rowMax, int colMin, int colMax, int blockMin, Dictionary<string, object> result) {
        var data = Scene.Data;
        var raised = new List<object>();
        bool ok = RunGrouped(lines.Count, i => {
            int line = lines[i];
            int vr = columns ? (rowMin + rowMax) / 2 : line;
            int vc = columns ? line : (colMin + colMax) / 2;
            if (!profile.ShowProfile(columns, vr, vc)) {
                result["error"] = raised.Count == 0
                    ? "That row or column could not be projected; it has no values."
                    : $"Raised {raised.Count}, then one could not be projected.";
                if (raised.Count > 0) result["projected"] = raised;
                return false;
            }
            object at = data != null ? data.GroupOf(columns, line) - blockMin + 1 : line - (columns ? colMin : rowMin) + 1;
            if (!raised.Contains(at)) raised.Add(at);
            return true;
        });
        if (!ok) return;

        result["projected"] = columns ? "column" : "row";
        result["indexes"] = raised;
    }

    private static void RaiseMatching(Args args, ProfileTool profile, Dictionary<string, object> result) {
        if (args.index != null || args.indexes != null || args.of != null) {
            result["error"] = "'where' picks the rows itself; send it without 'index', 'indexes' or 'of'.";
            return;
        }
        if (!TryResolveTargetBounds(result, out int rowMin, out int rowMax, out int colMin, out int colMax)) return;

        var data = Scene.Data;
        List<int> matching = args.matched.RowsOn(data, result);

        var lines = new List<int>();
        foreach (int row in matching) {
            int vis = data.VisIndexOf(false, row);
            if (vis >= rowMin && vis <= rowMax) lines.Add(vis);
        }

        if (lines.Count == 0) {
            result["projected"] = "row";
            result["indexes"] = new List<object>();
            result["note"] = matching.Count > 0
                ? "Every matching row is filtered off the sheet; bring them back with the Filter tool first."
                : "No row on this sheet matches.";
            return;
        }
        if (lines.Count > MostStrips) {
            Refuse(result, "fewer matching rows",
                $"{lines.Count} rows match, more than the {MostStrips} that can stand raised at once. Narrow the " +
                "conditions, or use the Filter tool's 'where' to show only the matching rows instead.");
            return;
        }

        Raise(profile, false, lines, rowMin, rowMax, colMin, colMax, data.GroupOf(false, rowMin), result);
    }

    private static bool PickLines(Of of, bool columns, int lineMin, int lineMax, int crossMin, int crossMax,
        Dictionary<string, object> result, List<int> into) {

        var data = Scene.Data;
        if (data == null) { result["error"] = "No data source found in scene."; return false; }

        // Ranking needs one measure in one unit. On a paired sheet the columns hold
        // different metrics, so neither axis can be ranked this way: judging metrics
        // against each other is meaningless, and judging a row means adding dollars
        // to share counts.
        bool byLine = !string.IsNullOrWhiteSpace(of.line);
        if (byLine == !string.IsNullOrWhiteSpace(of.measure)) {
            result["error"] = "'of' needs exactly one of 'line' or 'measure'.";
            return false;
        }

        if (data.IsGrouped(true) && !byLine) {
            result["error"] = "The metrics on this sheet are in different units, so there is no single number to " +
                "judge lines by. Name the metric to judge by in 'of.line', or the line you want in 'index'.";
            return false;
        }
        if (columns && data.IsGrouped(true)) {
            result["error"] = "The metrics on this sheet are in different units, so they cannot be ranked against " +
                "each other. Name the metric you want in 'index'.";
            return false;
        }

        // By one line of the other axis, its cells alone are read: a single
        // cell's sum is its value, and a paired metric's is its years together.
        string measure = "sum";
        if (byLine) {
            if (!TryResolveLine(of.line, !columns, crossMin, crossMax, result, out int crossBlock)) return false;
            BlockSpan(!columns, crossBlock, crossMin, crossMax, out crossMin, out crossMax);
            result["judgedBy"] = DataSource.GroupLabelAt(data, !columns, crossBlock);
        }
        else if (!TryParseMeasure(of.measure, "of", result, out measure)) return false;
        bool highest = !string.Equals(of.pick, "lowest", System.StringComparison.OrdinalIgnoreCase);
        int count = of.count ?? 1;

        if (crossMax < crossMin) { result["error"] = "The sheet has no values to judge by."; return false; }

        var scored = new List<KeyValuePair<double, int>>();
        for (int line = lineMin; line <= lineMax; line++) {
            if (!TryLineMeasure(data, columns, line, crossMin, crossMax, measure, out double score)) continue;
            scored.Add(new KeyValuePair<double, int>(score, line));
        }

        if (scored.Count == 0) { result["error"] = $"None of the {(columns ? "columns" : "rows")} had numbers to judge."; return false; }

        scored.Sort((a, b) => highest ? b.Key.CompareTo(a.Key) : a.Key.CompareTo(b.Key));
        int take = System.Math.Min(count, scored.Count);

        var picked = new List<object>(take);
        var scores = new List<object>(take);
        for (int i = 0; i < take; i++) {
            // 'into' holds blocks, as TryResolveLine's do.
            into.Add(data.GroupOf(columns, scored[i].Value));
            picked.Add(data.TitleAt(columns, scored[i].Value));
            scores.Add(System.Math.Round(scored[i].Key, 2));
        }

        result["pickedBy"] = measure;
        result["pick"] = highest ? "highest" : "lowest";
        result["picked"] = picked;
        result["scores"] = scores;
        return true;
    }

}
