using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Google.GenAI.Types;
using UnityEngine.Profiling;

public abstract class AgenticTool : Function {

    private static int refreshedAxes;

    private readonly string sample;

    protected AgenticTool() {
        sample = "GeminiTool." + GetType().Name;
    }

    protected abstract void Run(Dictionary<string, object> args, Dictionary<string, object> result);

    private static readonly string[] RefusalKeys =
        { "error", "preconditionUnmet", "needsChoice" };

    protected static bool IsRefusal(Dictionary<string, object> result) {
        for (int i = 0; i < RefusalKeys.Length; i++)
            if (result.ContainsKey(RefusalKeys[i])) return true;
        return false;
    }

    protected virtual bool EditsAreOutcome => false;

    protected static void NoteRefreshed(bool columns) => refreshedAxes |= columns ? 2 : 1;

    protected override Task<Dictionary<string, object>> Execute(Dictionary<string, object> args) =>
        Perform(args, result => Run(args ?? new Dictionary<string, object>(), result));

    // Runs 'act' on the main thread as the assistant's call, and reads back
    // what it did.
    protected async Task<Dictionary<string, object>> Perform(Dictionary<string, object> args,
        Action<Dictionary<string, object>> act) {
        var result = new Dictionary<string, object>();
        var total = System.Diagnostics.Stopwatch.StartNew();
        long runMs = 0;
        int editsBefore = 0, editsAfter = 0;

        await MainThread.Run(() => {
            var run = System.Diagnostics.Stopwatch.StartNew();
            Profiler.BeginSample(sample);
            StateChannel.InAgentCall = true;
            try {
                AgentTurn.NoteToolCall();
                Scene.Sort?.CompleteOrderSequence();
                editsBefore = ManageDatasets.ActiveEdits != null ? ManageDatasets.ActiveEdits.Count : 0;
                refreshedAxes = 0;
                act(result);
            }
            catch (Exception e) {
                result.Clear();
                result["error"] = e.Message;
            }
            finally {
                if (!IsRefusal(result)) {
                    if ((refreshedAxes & 1) != 0) StalePositions.Clear(false);
                    if ((refreshedAxes & 2) != 0) StalePositions.Clear(true);
                }
                editsAfter = ManageDatasets.ActiveEdits != null ? ManageDatasets.ActiveEdits.Count : 0;
                StateChannel.InAgentCall = false;
                string did = StateChannel.TakeAgentBatch();
                if (!string.IsNullOrEmpty(did)) result["did"] = did;
                Profiler.EndSample();
                run.Stop();
                runMs = run.ElapsedMilliseconds;
            }
        });
        total.Stop();

        bool changed = editsAfter != editsBefore;
        if (EditsAreOutcome) result["changed"] = changed;

        if (!IsRefusal(result) && !result.ContainsKey("ok")) {
            result["ok"] = !EditsAreOutcome || changed;
            if (EditsAreOutcome && !changed && !result.ContainsKey("note"))
                result["note"] = "Nothing changed; the sheet is already the way this call would leave it.";
        }

        UnityEngine.Debug.Log($"[Gemini][tool] {Name} args={Brief(args)} -> {Brief(result)} " +
            $"({total.ElapsedMilliseconds} ms, run {runMs} ms, edits {editsBefore}->{editsAfter})");

        return result;
    }

    private static string Brief(Dictionary<string, object> map) {
        if (map == null || map.Count == 0) return "{}";
        try {
            string head = BriefJson(map, 300, out long totalBytes);
            return totalBytes > 300 ? head + "..." : head;
        }
        catch (Exception e) { return "<unserializable: " + e.Message + ">"; }
    }

    protected static Schema ParametersFor(System.Type args) => ToolArguments.Schema(args);

    // A tool's schema with the names a 'where' takes listed, from the contract.
    protected static Schema WithNames(Schema schema) {
        if (schema.Properties.TryGetValue("where", out Schema where)) BreachFilters.Named(where);
        return schema;
    }

    // There is one sheet, so there is nothing to pick: the bounds are its bounds.
    private static CreateSheet ResolvedSheet() {
        ManageSheets mgr = Scene.Sheets;
        return mgr != null ? mgr.Sheet : null;
    }

    protected static bool TryResolveTargetBounds(Dictionary<string, object> result,
        out int rowMin, out int rowMax, out int colMin, out int colMax) {
        rowMin = rowMax = colMin = colMax = 0;

        CreateSheet sheet = ResolvedSheet();
        if (sheet == null) { result["error"] = "No sheet in scene."; return false; }

        rowMin = sheet.rowMin; rowMax = sheet.rowMax; colMin = sheet.colMin; colMax = sheet.colMax;
        return true;
    }

    // On a grouped axis the agent addresses metrics, not raw columns: names match
    // the metric, numbers count metrics, and the value returned is a block index.
    // Rows and ungrouped columns have one line per block, so this is unchanged for
    // them and every caller below reads the same either way.
    protected static bool TryResolveLine(object arg, bool columns, int min, int max,
        Dictionary<string, object> result, out int block) {
        block = -1;
        string token = AsString(arg)?.Trim();
        var data = Scene.Data;
        string what = DataSource.GroupNoun(data, columns);
        if (string.IsNullOrEmpty(token)) { result["error"] = $"Provide a {what} number or name."; return false; }

        int size = GroupSize(columns);
        int blockMin = min / size;
        int blockMax = max / size;
        int span = blockMax - blockMin + 1;

        if (int.TryParse(token, out int number)) {
            if (StalePositions.IsDirty(columns)) {
                StalePositions.Clear(columns);
                var current = new List<string>();
                if (data != null) {
                    int liveBlocks = data.GroupCount(columns);
                    for (int b = blockMin; b <= blockMax && b < liveBlocks; b++)
                        current.Add(DataSource.GroupLabelAt(data, columns, b));
                }
                result["error"] = $"The {what} positions have shifted: the user reordered the {what}s since you " +
                    $"last read them. They now run: {string.Join(", ", current)}. Name the {what} you mean, or " +
                    "give its position in this order.";
                return false;
            }
            if (number < 1 || number > span) {
                result["error"] = $"There is no {what} {number} there; it has {span}.";
                return false;
            }
            block = blockMin + number - 1;
            return true;
        }

        if (data == null) { result["error"] = "No dataset is open."; return false; }

        // An exact name first, then, for three letters or more, the start of one.
        int found = -1, hits = 0;
        void Match(Func<string, bool> matches) {
            for (int b = blockMin; b <= blockMax; b++) {
                if (!matches(data.GroupTitleAt(columns, b) ?? "")) continue;
                found = b;
                hits++;
            }
        }
        Match(title => string.Equals(title, token, StringComparison.OrdinalIgnoreCase));
        if (hits == 0 && token.Length >= 3)
            Match(title => title.StartsWith(token, StringComparison.OrdinalIgnoreCase));

        if (hits == 1) { block = found; return true; }
        if (hits > 1) {
            result["error"] = $"More than one {what} is called '{token}'; ask the user which, or give its number.";
            return false;
        }
        string other = DataSource.GroupNoun(data, !columns);
        result["error"] = TitleExistsOnAxis(token, !columns)
            ? $"No {what} called '{token}', but there is a {other} called '{token}'; pass it as '{other}' instead."
            : $"No {what} called '{token}'.";
        var names = new List<object>();
        for (int b = blockMin; b <= blockMax; b++) names.Add(data.GroupTitleAt(columns, b) ?? "");
        result[columns ? "columns" : "rows"] = names;
        return false;
    }

    // How many lines one block holds: a metric's on a grouped axis, else one.
    private static int GroupSize(bool columns) {
        var data = Scene.Data;
        return data != null ? data.GroupSize(columns) : 1;
    }

    // The line span one block covers, clipped to the piece.
    protected static void BlockSpan(bool columns, int block, int min, int max, out int lo, out int hi) {
        int size = GroupSize(columns);
        lo = Math.Max(block * size, min);
        hi = Math.Min(block * size + size - 1, max);
    }

    protected static int BlockCountIn(bool columns, int min, int max) {
        int size = GroupSize(columns);
        return (max / size) - (min / size) + 1;
    }

    protected static bool TryResolveLines(IReadOnlyList<string> tokens, bool columns, int min, int max,
        Dictionary<string, object> result, out List<int> visIndexes) {
        visIndexes = null;
        string what = DataSource.GroupNoun(Scene.Data, columns);

        if (tokens == null || tokens.Count == 0) {
            result["error"] = $"Provide at least one {what} name or number.";
            return false;
        }

        int span = BlockCountIn(columns, min, max);
        if (tokens.Count > span) {
            result["error"] = $"That is {tokens.Count} entries but there are only {span} {what}s.";
            return false;
        }

        var resolved = new List<int>(tokens.Count);
        var seen = new HashSet<int>();
        for (int i = 0; i < tokens.Count; i++) {
            if (!TryResolveLine(tokens[i], columns, min, max, result, out int vis)) return false;
            if (!seen.Add(vis)) {
                result["error"] = $"'{tokens[i]}' is listed more than once; each {what} may appear only once.";
                return false;
            }
            resolved.Add(vis);
        }

        visIndexes = resolved;
        return true;
    }

    protected static bool TryInferAxis(string token, out bool columns) {
        columns = false;
        token = token?.Trim();
        if (string.IsNullOrEmpty(token) || int.TryParse(token, out _)) return false;

        bool onColumns = TitleExistsOnAxis(token, true);
        bool onRows = TitleExistsOnAxis(token, false);
        if (onColumns == onRows) return false;

        columns = onColumns;
        return true;
    }

    protected static bool RunGrouped(int count, Func<int, bool> act) {
        var edits = ManageDatasets.ActiveEdits;
        if (count > 1) edits.OpenGroup();
        try {
            for (int i = 0; i < count; i++)
                if (!act(i)) return false;
        }
        finally { edits.CloseGroup(); }
        return true;
    }

    protected static void NeedChoice(Dictionary<string, object> result, string what,
        IReadOnlyList<string> options, string message) {
        result["needsChoice"] = what;
        if (options != null && options.Count > 0) result["options"] = new List<object>(options);
        result["message"] = message;
    }

    protected static bool TitleExistsOnAxis(string token, bool columns) {
        var data = Scene.Data;
        if (data == null) return false;

        if (data.IsGrouped(columns)) {
            int blocks = data.GroupCount(columns);
            for (int b = 0; b < blocks; b++)
                if (string.Equals(data.GroupTitleAt(columns, b), token, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        IReadOnlyList<string> titles = columns ? data.ColumnTitles : data.RowTitles;
        for (int i = 0; i < titles.Count; i++)
            if (string.Equals(titles[i], token, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    protected static bool TryWorldDirection(string direction, out UnityEngine.Vector3 world) {
        world = UnityEngine.Vector3.zero;
        if (direction == "up") { world = UnityEngine.Vector3.up; return true; }
        if (direction == "down") { world = UnityEngine.Vector3.down; return true; }

        UnityEngine.Transform cam = CameraRig.MainTransform;
        if (cam == null) return false;

        UnityEngine.Vector3 forward = CameraRig.FlatForward;
        UnityEngine.Vector3 right = UnityEngine.Vector3.Cross(UnityEngine.Vector3.up, forward).normalized;

        switch (direction) {
            case "forward": world = forward; return true;
            case "back": world = -forward; return true;
            case "left": world = -right; return true;
            case "right": world = right; return true;
            case "forward-left": world = (forward - right).normalized; return true;
            case "forward-right": world = (forward + right).normalized; return true;
            case "back-left": world = (-forward - right).normalized; return true;
            case "back-right": world = (-forward + right).normalized; return true;
        }
        return false;
    }

    // +1 for a turn to the right (clockwise seen from above), -1 to the left.
    protected static bool TryTurnSign(string direction, out float sign) {
        sign = direction == "right" ? 1f : direction == "left" ? -1f : 0f;
        return sign != 0f;
    }

    protected static bool TryGet(Dictionary<string, object> args, string key, out object value) {
        value = null;
        return args != null && args.TryGetValue(key, out value) && value != null;
    }

    protected static string AsString(object v) {
        if (v is JsonElement je)
            return je.ValueKind == JsonValueKind.String ? je.GetString() : je.ToString();
        return v?.ToString();
    }

    protected static object Round(double v) {
        if (double.IsNaN(v) || double.IsInfinity(v)) return null;
        if (v > (double)decimal.MaxValue || v < (double)decimal.MinValue) return null;
        return (decimal)Math.Round(v, 4);
    }


    protected readonly struct PiecePose {
        public readonly UnityEngine.Vector3 pos;
        public readonly UnityEngine.Quaternion rot;
        public readonly UnityEngine.Vector3 scale;
        public PiecePose(UnityEngine.Transform t) { pos = t.localPosition; rot = t.localRotation; scale = t.localScale; }
    }

    protected static bool TryResolveSheet(Dictionary<string, object> result,
        string verb, out ManageSheets mgr, out CreateSheet sheet) {
        mgr = Scene.Sheets;
        sheet = ResolvedSheet();
        if (sheet == null) { result["error"] = $"The sheet is not ready to {verb}; is a sheet shown?"; return false; }
        return true;
    }

    protected static PiecePose ApplyPieceTransform(ManageSheets mgr, CreateSheet sheet, Action<UnityEngine.Transform> mutate) {
        mgr.CompletePieceMotion(sheet);
        UnityEngine.Transform t = sheet.transform;
        var pre = new PiecePose(t);
        mutate(t);
        mgr.NotifyMoveCommitted(sheet, pre.pos, pre.rot, pre.scale);
        mgr.AnimatePieceFrom(sheet, pre.pos, pre.rot, pre.scale);
        return pre;
    }

    protected static bool ResolveAxis(string toolName, string axisArg, string inferFrom,
        Dictionary<string, object> result, out bool columns) {
        columns = false;

        // Bound to 'columns' or 'rows' already (ToolArguments).
        if (axisArg != null) {
            columns = axisArg == "columns";
            result["axisFrom"] = "given";
            return true;
        }

        if (TryInferAxis(inferFrom, out columns)) {
            result["axisFrom"] = "inferred";
            return true;
        }

        NeedChoice(result, "axis", new List<string> { "columns", "rows" },
            $"The {toolName} tool works on columns or rows and the name you gave does not say which. Ask the user which, then call this again with 'axis'.");
        return false;
    }

    // 'row' or 'rows', 'column' or 'columns', in any case.
    protected static bool TryParseAxis(string axis, out bool columns) {
        switch (axis?.Trim().ToLowerInvariant()) {
            case "column": case "columns": columns = true; return true;
            case "row": case "rows": columns = false; return true;
            default: columns = false; return false;
        }
    }

    protected static void Refuse(Dictionary<string, object> result, string unmet, string message) {
        result["preconditionUnmet"] = unmet;
        result["message"] = message;
    }

    protected static bool EnsureToolPanelOpen(Dictionary<string, object> result) {
        var panel = Scene.ToolPanel;
        if (panel == null) { result["error"] = "The tool panel was not found in the scene."; return false; }
        if (!panel.IsVisible) {
            if (AgentTurn.UserTookOver) {
                Refuse(result, "tool panel closed by the user",
                    "The user closed the tool panel while you were working, so it is not yours to reopen. " +
                    "Tell them what you had done and ask before carrying on.");
                return false;
            }
            panel.ShowPanel();
        }
        return true;
    }

    protected static bool EnsureToolSelected(ToolType tool, Dictionary<string, object> result) {
        if (!EnsureToolPanelOpen(result)) return false;

        var tools = Scene.Tools;
        if (tools == null) { result["error"] = "The tool manager was not found in the scene."; return false; }
        if (tools.SelectedTool != tool) {
            if (AgentTurn.UserTookOver) {
                Refuse(result, "tool changed by the user",
                    $"The user changed the tool while you were working, so the {tool} tool is no longer selected. " +
                    "Tell them what you had done and ask before carrying on.");
                return false;
            }
            tools.SelectTool(tool);
        }

        if (tools.SelectedTool != tool) {
            result["error"] = $"The {tool} tool could not be selected.";
            return false;
        }
        return true;
    }

    // EnsureToolSelected, for a tool the call then works through itself.
    protected static bool EnsureToolSelected<T>(ToolType kind, T tool, Dictionary<string, object> result) where T : Tool {
        if (!EnsureToolSelected(kind, result)) return false;
        if (tool == null) { result["error"] = $"{kind} tool not found in scene."; return false; }
        return true;
    }

    protected static bool TryCellValue(DataSource data, int visRow, int visCol, out double value) {
        value = 0d;
        return data != null && data.TryValueAt(visRow, visCol, out value);
    }

    protected static bool TryParseMeasure(string raw, string owner,
        Dictionary<string, object> result, out string measure) {
        measure = raw?.Trim().ToLowerInvariant();
        if (measure == "sum" || measure == "average" || measure == "max" || measure == "min") return true;
        result["error"] = $"'{owner}' needs a 'measure': sum, average, max or min.";
        return false;
    }

    protected struct MeasureAcc {
        private double sum, best, worst;
        private int n;

        public void Add(double v) {
            if (n == 0) { best = v; worst = v; }
            else { if (v > best) best = v; if (v < worst) worst = v; }
            sum += v;
            n++;
        }

        public bool TryScore(string measure, out double score) {
            score = 0d;
            if (n == 0) return false;
            switch (measure) {
                case "sum": score = sum; return true;
                case "average": score = sum / n; return true;
                case "max": score = best; return true;
                case "min": score = worst; return true;
                default: return false;
            }
        }
    }

    protected static bool TryLineMeasure(DataSource data, bool isColumn, int line,
        int crossMin, int crossMax, string measure, out double score) {

        var acc = new MeasureAcc();
        for (int j = crossMin; j <= crossMax; j++)
            if (TryCellValue(data, isColumn ? j : line, isColumn ? line : j, out double v)) acc.Add(v);
        return acc.TryScore(measure, out score);
    }

    // A map tool's change is only seen with the map panel up, so it opens it.
    protected static void OpenMap(MapCarousel maps, Dictionary<string, object> result) {
        if (maps.IsOpen) return;
        maps.SetOpen(true);
        result["opened"] = "the map panel was closed, so it was opened";
    }
}

public abstract class AgenticTool<TArgs> : AgenticTool where TArgs : class, new() {

    protected abstract void Run(TArgs args, Dictionary<string, object> result);

    // What the arguments name that has to be fetched before the call can run,
    // fetched off the main thread: a 'where' looked up in the database. Null
    // to go on, or why the call cannot.
    protected virtual Task<string> Prepare(TArgs args) => Task.FromResult<string>(null);

    // Binds the arguments and prepares them here, off the main thread, and
    // only then hops to it to run.
    protected sealed override async Task<Dictionary<string, object>> Execute(Dictionary<string, object> args) {
        var bound = ToolArguments.Bind(typeof(TArgs), args, out string error) as TArgs;
        if (bound != null) error = await Prepare(bound).ConfigureAwait(false);
        return await Perform(args, result => {
            if (error != null) result["error"] = error;
            else Run(bound, result);
        }).ConfigureAwait(false);
    }

    // Never reached: Execute binds the arguments and runs the typed Run.
    protected sealed override void Run(Dictionary<string, object> args, Dictionary<string, object> result) =>
        throw new NotSupportedException();

    // Looks a 'where' up, for a tool that takes one; 'store' keeps the match
    // on the arguments for Run.
    protected static async Task<string> LookUp(BreachFilters where, Action<BreachMatch> store) {
        if (where == null) return null;
        (BreachMatch match, string error) = await BreachMatch.Look(where).ConfigureAwait(false);
        if (match != null) store(match);
        return error;
    }
}
