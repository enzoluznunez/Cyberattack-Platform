using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class SortTool : Tool
{
    public float reflowSmoothing = 18f;

    private readonly List<SortLineProxy> _proxies = new List<SortLineProxy>();

    // Handles put away, made while the app loads (Prepare): taking the tool
    // out or finishing a move stands them on the lines again, making none.
    private readonly List<SortLineProxy> _spare = new List<SortLineProxy>();
    private Transform _spareRoot;

    private SortLineProxy _held;
    private CreateSheet _dragSheet;
    private bool _dragColumns;
    private int _dragBlock = -1;
    private int _dragTarget;
    private int _blockMin;
    private int _blockMax;

    protected override ToolType Kind => ToolType.Sort;

    protected override void OnToolStart()
    {
        if (sheetManager != null) sheetManager.OnSheetsChanged += RebuildProxies;
    }

    protected override void OnToolDestroy()
    {
        if (sheetManager != null) sheetManager.OnSheetsChanged -= RebuildProxies;
        CompleteOrderSequence();
        CancelDrag();
        ClearProxies();
        foreach (SortLineProxy proxy in _spare)
            if (proxy != null) Destroy(proxy.gameObject);
        _spare.Clear();
    }

    // Makes handles, put away, until there are 'count', within 'budgetMs'
    // this frame. True once there are that many.
    public bool Prepare(int count, double budgetMs)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (_spare.Count + _proxies.Count < count)
        {
            if (clock.Elapsed.TotalMilliseconds >= budgetMs) return false;
            _spare.Add(SortLineProxy.Make(SpareRoot()));
        }
        return true;
    }

    private Transform SpareRoot()
    {
        if (_spareRoot == null) _spareRoot = new GameObject("SortLines").transform;
        return _spareRoot;
    }

    private SortLineProxy TakeProxy() => ManageSheets.PopLive(_spare) ?? SortLineProxy.Make(SpareRoot());

    private void OnDisable() => CompleteOrderSequence();

    protected override void OnResetTool()
    {
        CompleteOrderSequence();
        CancelDrag();
        // Already in the data's order, as after undoing every sort, there is
        // nothing to rebuild.
        DataSource data = Scene.Data;
        if (data != null && !data.InDataOrder)
        {
            if (sheetManager != null) sheetManager.SuppressNextReflow();
            data.ResetOrder();
        }
        RebuildProxies();
    }

    protected override void OnActiveChanged(bool active)
    {
        CancelDrag();
        RebuildProxies();
    }

    protected override void ClearToolState()
    {
        base.ClearToolState();
        CancelDrag();
        RebuildProxies();
    }

    private void RebuildProxies()
    {
        // While a set order walks, the handles stay as they stand, unless the
        // tool is put away: then they go, or they would stay live under the
        // next tool, sliding when pinched with nothing to commit them.
        if (_sequence != null && Active) return;
        if (_held != null) CancelDrag();
        ClearProxies();

        if (!Active || sheetManager == null || !sheetManager.IsBuilt) return;

        CreateSheet sheet = sheetManager.Sheet;
        if (sheet == null || !sheet.IsBuilt) return;

        for (int axis = 0; axis < 2; axis++)
        {
            bool columns = axis == 1;
            int min = sheet.BlockMin(columns);
            int max = sheet.BlockMax(columns);
            for (int block = min; block <= max; block++)
            {
                SortLineProxy proxy = TakeProxy();
                if (proxy.Place(sheet, columns, block, sheetManager.Height)) _proxies.Add(proxy);
                else _spare.Add(proxy);
            }
        }
    }

    private void ClearProxies()
    {
        for (int i = 0; i < _proxies.Count; i++)
        {
            if (_proxies[i] == null) continue;
            _proxies[i].SetLive(false);
            _spare.Add(_proxies[i]);
        }
        _proxies.Clear();
    }

    private SortLineProxy FirstGrabbedProxy()
    {
        for (int i = 0; i < _proxies.Count; i++)
            if (_proxies[i] != null && _proxies[i].IsGrabbed) return _proxies[i];
        return null;
    }

    private void Update()
    {
        if (!Active || sheetManager == null) return;

        if (_held != null && !_held.IsGrabbed) { CommitDrag(); return; }

        if (_held == null)
        {
            SortLineProxy grabbed = FirstGrabbedProxy();
            if (grabbed == null) return;
            BeginDrag(grabbed);
            if (_held == null) return;
        }

        bool columns = _dragColumns;
        float dragged = _held.Coord;

        _dragTarget = Mathf.Clamp(
            Mathf.RoundToInt(_dragSheet.BlockFraction(columns, dragged)), _blockMin, _blockMax);

        Reflow(dragged);
    }

    private void BeginDrag(SortLineProxy proxy)
    {
        CreateSheet sheet = proxy.sheet;
        if (sheet == null) return;

        _dragColumns = proxy.columns;
        bool columns = _dragColumns;

        // Lines still sliding from the last change, or bars still growing
        // back after a filter, are put where they end first: both move the
        // same bars this drag moves, and would pull them back each frame.
        sheetManager.CompleteLineMotion();
        sheet.CompleteGrow();
        sheetManager.LinesDragged = true;

        _dragSheet = sheet;
        _blockMin = sheet.BlockMin(columns);
        _blockMax = sheet.BlockMax(columns);
        _dragBlock = proxy.block;
        _dragTarget = _dragBlock;
        _held = proxy;
    }

    private void Reflow(float dragged)
    {
        float t = Style.Damp(reflowSmoothing, Time.deltaTime);
        bool columns = _dragColumns;

        for (int block = _blockMin; block <= _blockMax; block++)
        {
            if (block == _dragBlock)
            {
                _dragSheet.LayoutBlock(columns, block, dragged);
                continue;
            }

            float goal = _dragSheet.BlockCoord(columns, DisplaySlot(block, _dragBlock, _dragTarget));
            float now = _dragSheet.BlockOffset(columns, block);
            _dragSheet.LayoutBlock(columns, block, Mathf.Lerp(now, goal, t));
        }
    }

    private void CommitDrag()
    {
        int from = _dragBlock;
        int target = _dragTarget;
        CreateSheet sheet = _dragSheet;

        ClearDrag();

        // A drag lets go with the lines sliding into their places: Sort is
        // one of the user's changes that plays out rather than landing.
        if (sheetManager != null) sheetManager.UserChangeAnimates = true;
        bool moved = MoveLine(_dragColumns, from, target);
        if (sheetManager != null) sheetManager.UserChangeAnimates = false;
        // A move rebuilds the sheet, and the handles with it (OnSheetsChanged).
        if (!moved)
        {
            RestLayout(sheet);
            RebuildProxies();
        }
    }

    [Tooltip("Beyond this many steps a set-order request stops animating one line at a time and moves them together.")]
    public int maxSequencedSteps = 24;

    public int LastReorderedLines { get; private set; }

    public bool SequenceRunning => _sequence != null;

    private Coroutine _sequence;
    private System.Action _sequenceFinish;

    public void CompleteOrderSequence()
    {
        if (_sequence != null) { StopCoroutine(_sequence); _sequence = null; }
        var finish = _sequenceFinish;
        _sequenceFinish = null;
        finish?.Invoke();
    }

    public bool HaltOrderSequence()
    {
        if (_sequence == null) return false;

        StopCoroutine(_sequence);
        _sequence = null;
        _sequenceFinish = null;
        if (sheetManager != null) sheetManager.ForceAgentMotion = false;
        RebuildProxies();
        return true;
    }

    // 'fullOrder', when given, is the whole arrangement the sort ends on,
    // hidden lines included: a sort that ranks every line, so a filtered line
    // comes back where it belongs. The lines on the sheet walk to their places
    // as ever; the hidden ones, which nobody sees, are put in place at the end.
    public bool SetOrder(bool columns, IReadOnlyList<int> targetOrder, IReadOnlyList<int> fullOrder = null)
    {
        if (!Active) return false;

        DataSource src = Scene.Data;
        if (src == null || targetOrder == null || targetOrder.Count == 0) return false;

        CompleteOrderSequence();

        IReadOnlyList<int> live = columns ? src.ColumnOrder : src.RowOrder;
        var preOrder = new List<int>(live);

        // Plan and walk in block space so a grouped axis steps a whole metric at a
        // time; the recorded order stays line-level, so undo is unchanged.
        int size = src.GroupSize(columns);
        List<int> preBlocks = Blocks(preOrder, size);
        List<int> targetBlocks = Blocks(targetOrder, size);

        var steps = PlanSteps(preBlocks, targetBlocks);
        List<int> preFull = src.FullOrder(columns);
        bool fullChanged = fullOrder != null && !SameOrder(preFull, fullOrder);
        if (steps.Count == 0 && !fullChanged) return false;

        int changed = 0;
        for (int i = 0; i < targetBlocks.Count && i < preBlocks.Count; i++)
            if (preBlocks[i] != targetBlocks[i]) changed++;
        LastReorderedLines = changed;

        ManageDatasets.ActiveEdits.PushReorder(columns, preOrder, changed, fullChanged ? preFull : null);
        string nouns = DataSource.Plural(DataSource.GroupNoun(src, columns));
        Report(changed > 0
            ? $"set the order of {changed} {nouns}"
            : $"left the {nouns} on the sheet where they were and reordered only the filtered-off ones, " +
              "which come back in their new places");

        if (steps.Count == 0)
        {
            src.SetFullOrder(columns, fullOrder);
            return true;
        }

        var final = new List<int>(targetOrder);
        IReadOnlyList<int> whole = fullChanged ? new List<int>(fullOrder) : null;
        bool stepwise = sheetManager != null && sheetManager.AgentMotionAnimates
                        && steps.Count <= maxSequencedSteps;

        if (!stepwise)
        {
            Apply(src, columns, final, whole);
            return true;
        }

        sheetManager.ForceAgentMotion = true;
        _sequenceFinish = () => FinishSequence(src, columns, final, whole);
        _sequence = StartCoroutine(WalkOrder(src, columns, steps, final, whole));
        return true;
    }

    private static List<int> Blocks(IReadOnlyList<int> order, int size)
    {
        var blocks = new List<int>((order.Count + size - 1) / size);
        for (int i = 0; i < order.Count; i += size) blocks.Add(order[i]);
        return blocks;
    }

    private static void Apply(DataSource src, bool columns, IReadOnlyList<int> order, IReadOnlyList<int> whole)
    {
        if (whole != null && src.SetFullOrder(columns, whole)) return;
        if (columns) src.SetColumnOrder(order);
        else src.SetRowOrder(order);
    }

    private static bool SameOrder(IReadOnlyList<int> a, IReadOnlyList<int> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private struct OrderStep
    {
        public int key;
        public int pos;
    }

    private static List<OrderStep> PlanSteps(IReadOnlyList<int> preOrder, IReadOnlyList<int> targetOrder)
    {
        var working = new List<int>(preOrder);
        var steps = new List<OrderStep>();
        for (int i = 0; i < targetOrder.Count && i < working.Count; i++)
        {
            int want = targetOrder[i];
            int at = working.IndexOf(want);
            if (at < 0 || at == i) continue;
            working.RemoveAt(at);
            working.Insert(i, want);
            steps.Add(new OrderStep { key = want, pos = i });
        }
        return steps;
    }

    private IEnumerator WalkOrder(DataSource src, bool columns, List<OrderStep> steps, List<int> final,
        IReadOnlyList<int> whole)
    {
        int size = src.GroupSize(columns);
        for (int i = 0; i < steps.Count; i++)
        {
            OrderStep step = steps[i];
            IReadOnlyList<int> live = columns ? src.ColumnOrder : src.RowOrder;
            int at = -1;
            for (int b = 0; b * size < live.Count; b++) if (live[b * size] == step.key) { at = b; break; }
            if (at < 0 || at == step.pos) continue;

            if (columns) src.MoveColumn(at * size, step.pos * size);
            else src.MoveRow(at, step.pos);

            if (sheetManager != null) yield return sheetManager.WaitForReflow();
            else yield return null;
        }

        _sequence = null;
        _sequenceFinish = null;
        FinishSequence(src, columns, final, whole);
    }

    private void FinishSequence(DataSource src, bool columns, List<int> final, IReadOnlyList<int> whole)
    {
        if (sheetManager != null) sheetManager.ForceAgentMotion = false;
        Apply(src, columns, final, whole);
    }

    // 'from' and 'to' are block positions: whole metrics on a grouped axis, single
    // lines otherwise.
    public bool MoveLine(bool columns, int from, int to)
    {
        if (!Active) return false;

        DataSource src = Scene.Data;
        if (src == null || from == to) return false;

        int size = src.GroupSize(columns);
        IReadOnlyList<int> order = columns ? src.ColumnOrder : src.RowOrder;
        int blocks = order.Count / size;
        if (from < 0 || from >= blocks || to < 0 || to >= blocks) return false;

        var preOrder = new List<int>(order);

        string what = DataSource.GroupLabelAt(src, columns, from);
        string noun = DataSource.Plural(DataSource.GroupNoun(src, columns));

        var postOrder = new List<int>(preOrder);
        DataSource.MoveGroupWithin(postOrder, from, to, size);

        string arrangement = "";
        if (blocks <= 40)
        {
            List<string> names = DataSource.BlockTitlesFor(src, columns, postOrder);
            arrangement = $"; the {noun} now run: {string.Join(", ", names)}";
        }

        Report($"moved {what} from position {from + 1} to {to + 1}{arrangement}");

        StalePositions.MarkDirty(columns);

        ManageDatasets.ActiveEdits.PushSort(columns, preOrder, from, to);

        if (columns) src.MoveColumn(from * size, to * size);
        else src.MoveRow(from, to);
        return true;
    }

    private void CancelDrag()
    {
        CreateSheet sheet = _dragSheet;
        ClearDrag();
        RestLayout(sheet);
    }

    private void ClearDrag()
    {
        _held = null;
        if (sheetManager != null) sheetManager.LinesDragged = false;
        _dragSheet = null;
        _dragBlock = -1;
    }

    private void RestLayout(CreateSheet sheet)
    {
        if (sheet != null) sheet.RestLines();
    }

    private static int DisplaySlot(int slot, int from, int target)
    {
        if (slot == from) return target;
        int p = slot > from ? slot - 1 : slot;
        if (p >= target) p += 1;
        return p;
    }
}
