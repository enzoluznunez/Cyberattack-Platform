using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Oculus.Interaction;
using Oculus.Interaction.HandGrab;

[RequireComponent(typeof(BoxCollider), typeof(Rigidbody))]
public class CreateSheet : MonoBehaviour
{

    private const float ZeroPlateFraction = 0.008f;

    private static readonly Color NoData = new Color(0.85f, 0.60f, 0.25f);

    private static readonly float[] Lightness =
        { 0f, 0.127f, 0.233f, 0.346f, 0.466f, 0.593f, 0.724f, 0.860f, 1f };

    public int sheetId = -1;
    public int rowMin, rowMax, colMin, colMax;

    private readonly List<CreateCube> _pool = new List<CreateCube>();
    private readonly List<CreateCube> _live = new List<CreateCube>();
    private readonly Dictionary<int, CreateCube> _byCell = new Dictionary<int, CreateCube>();
    private readonly Dictionary<int, List<CreateCube>> _byCol = new Dictionary<int, List<CreateCube>>();
    private readonly Dictionary<int, List<CreateCube>> _byRow = new Dictionary<int, List<CreateCube>>();

    private BoxCollider _bounds;
    private Rigidbody _body;
    private Grabbable _grabbable;
    private HandGrabInteractable _handGrab;
    private OneGrabTranslateTransformer _slide;

    private Material _material;
    private SheetLabels _labels;

    private struct BarTarget
    {
        public CreateCube cube;
        public Vector3 center;
        public Vector3 size;
    }

    private readonly List<BarTarget> _bars = new List<BarTarget>();

    // The lines standing away from their own slot: mid-slide in a sort's
    // reflow or held in a Sort drag. SheetRaycast looks at them wherever they are.
    private readonly HashSet<int> _offColumns = new HashSet<int>();
    private readonly HashSet<int> _offRows = new HashSet<int>();
    public IReadOnlyCollection<int> LinesOffSlot(bool columns) => columns ? _offColumns : _offRows;
    private Coroutine _grow;
    private float _cellSize;
    private float _rowPitch;
    private float _groupGap;
    private int _groupSize = 1;
    private float _baseY;

    private bool _wasGrabbed;
    private Vector3 _grabPos;
    private Quaternion _grabRot;
    private Vector3 _grabScale;

    public int RowCount => rowMax - rowMin + 1;
    public int ColCount => colMax - colMin + 1;
    public float CellSize => _cellSize;

    // How far apart lines stand along an axis: columns a bar and its gap apart,
    // rows far enough apart that their titles, which run along the rows, never
    // reach into one another.
    public float PitchOn(bool columns) => columns ? _cellSize : _rowPitch;
    public bool IsBuilt => _live.Count > 0;

    // A grouped axis draws its lines in tight blocks — one metric's years sitting
    // together — with an extra gap between blocks. Every index-to-position
    // conversion in the app runs through these, so spacing lives in one place.
    public static float Raw(int line, float cellSize, int groupSize, float groupGap) =>
        line * cellSize + (groupSize > 1 ? (line / groupSize) * groupGap : 0f);

    public static float Center(int min, int max, float cellSize, int groupSize, float groupGap) =>
        (Raw(min, cellSize, groupSize, groupGap) + Raw(max, cellSize, groupSize, groupGap)) * 0.5f;

    public int GroupSizeOn(bool columns) => columns ? _groupSize : 1;
    public float GroupGapOn(bool columns) => columns ? _groupGap : 0f;

    // A line stands at its slot: its place among every line, hidden ones
    // included (DataSource.Slots), so a filtered line leaves its place empty
    // and the rest stay where they were. Lines are still counted by their
    // visible position everywhere else; only the place they stand at is the
    // slot's. Copied at each build, so the sheet and its bars agree.
    private readonly List<int> _rowSlots = new List<int>();
    private readonly List<int> _colSlots = new List<int>();
    private int _rowSlotCount;
    private int _colSlotCount;

    // The slot of the line at a visible position; past either end, slots run
    // on one per line from the last one there is.
    public int SlotOf(bool columns, int line)
    {
        List<int> slots = columns ? _colSlots : _rowSlots;
        if (slots.Count == 0) return line;
        if (line < 0) return slots[0] + line;
        if (line >= slots.Count) return slots[slots.Count - 1] + (line - slots.Count + 1);
        return slots[line];
    }

    // The first and last slot the sheet spans: every slot when it holds the
    // whole axis, so a hidden line at either end keeps the sheet its size and
    // its middle where it was. What a poke on an empty place is measured
    // against.
    public int FirstSlot(bool columns) =>
        (columns ? colMin : rowMin) <= 0 ? 0 : SlotOf(columns, columns ? colMin : rowMin);

    public int LastSlot(bool columns)
    {
        List<int> slots = columns ? _colSlots : _rowSlots;
        int max = columns ? colMax : rowMax;
        int count = columns ? _colSlotCount : _rowSlotCount;
        return slots.Count > 0 && max >= slots.Count - 1 && count > 0 ? count - 1 : SlotOf(columns, max);
    }

    public float CenterX => Center(FirstSlot(true), LastSlot(true), _cellSize, _groupSize, _groupGap);
    public float CenterZ => Center(FirstSlot(false), LastSlot(false), _rowPitch, 1, 0f);

    public float LineCoord(bool columns, int line) => SlotCoord(columns, SlotOf(columns, line));

    // Where a slot stands, filled or not.
    public float SlotCoord(bool columns, int slot) =>
        Raw(slot, PitchOn(columns), GroupSizeOn(columns), GroupGapOn(columns)) - (columns ? CenterX : CenterZ);

    // The visible position of the line at a slot, or -1 where the slot is
    // empty because its line is filtered off.
    public int LineAtSlot(bool columns, int slot)
    {
        List<int> slots = columns ? _colSlots : _rowSlots;
        int at = slots.BinarySearch(slot);
        return at >= 0 ? at : -1;
    }

    private float VisibleOfSlot(bool columns, float slot, int size)
    {
        List<int> slots = columns ? _colSlots : _rowSlots;
        int lines = slots.Count / Mathf.Max(size, 1);
        if (lines == 0) return slot;

        float SlotAt(int b) => slots[b * size] / (float)size;
        if (slot <= SlotAt(0)) return slot - SlotAt(0);
        if (slot >= SlotAt(lines - 1)) return lines - 1 + (slot - SlotAt(lines - 1));
        for (int b = 0; b + 1 < lines; b++)
        {
            float lo = SlotAt(b), hi = SlotAt(b + 1);
            if (slot <= hi) return b + (hi - lo > 1e-6f ? (slot - lo) / (hi - lo) : 0f);
        }
        return lines - 1;
    }

    // The slot a coordinate falls at, fractionally.
    public float SlotFraction(bool columns, float coord)
    {
        float cell = PitchOn(columns);
        if (cell <= 1e-6f) return 0f;

        float p = coord + (columns ? CenterX : CenterZ);
        int size = GroupSizeOn(columns);
        float gap = GroupGapOn(columns);
        if (size <= 1 || gap <= 0f) return p / cell;

        float pitch = size * cell + gap;
        float block = Mathf.Floor(p / pitch);
        float within = p - block * pitch;

        // Inside a block the lines sit one cell apart; across the gap the fraction
        // runs on smoothly to the next block's first line, so a drag never jumps.
        float edge = (size - 1) * cell;
        float step = within <= edge
            ? within / cell
            : (size - 1) + (within - edge) / (cell + gap);

        return block * size + step;
    }

    // A block is one addressable unit along an axis: a whole metric on a grouped
    // axis, a single line otherwise. Blocks are evenly pitched even when the lines
    // inside them are not, so anything that drags or reorders can work in block
    // space and stay the simple uniform problem it always was.
    public float BlockPitch(bool columns) => GroupSizeOn(columns) * PitchOn(columns) + GroupGapOn(columns);

    public int BlockMin(bool columns) => (columns ? colMin : rowMin) / GroupSizeOn(columns);
    public int BlockMax(bool columns) => (columns ? colMax : rowMax) / GroupSizeOn(columns);

    private float BlockInset(bool columns) => (GroupSizeOn(columns) - 1) * PitchOn(columns) * 0.5f;

    public float BlockCoord(bool columns, int block) =>
        LineCoord(columns, block * GroupSizeOn(columns)) + BlockInset(columns);

    public float BlockFraction(bool columns, float coord)
    {
        float pitch = BlockPitch(columns);
        if (pitch <= 1e-6f) return 0f;
        float slot = (coord + (columns ? CenterX : CenterZ) - BlockInset(columns)) / pitch;
        int size = GroupSizeOn(columns);
        return VisibleOfSlot(columns, slot, size);
    }

    public void LayoutBlock(bool columns, int block, float coord)
    {
        int size = GroupSizeOn(columns);
        float first = coord - BlockInset(columns);
        for (int s = 0; s < size; s++)
            LayoutLine(columns, block * size + s, first + s * PitchOn(columns));
    }

    public float BlockOffset(bool columns, int block)
    {
        int size = GroupSizeOn(columns);
        return LineOffset(columns, block * size) + BlockInset(columns);
    }

    public bool Contains(int visRow, int visCol) =>
        visRow >= rowMin && visRow <= rowMax && visCol >= colMin && visCol <= colMax;

    public CreateCube CubeAt(int visRow, int visCol) =>
        _byCell.TryGetValue(Key(visRow, visCol), out CreateCube c) ? c : null;

    public void Build(DataSource data, Material material,
        int rMin, int rMax, int cMin, int cMax,
        float cellSize, float rowPitch, float groupGap, float height, float baseY, float cubeSide,
        SheetLabelStyle labels)
    {
        if (data == null) return;
        using var marker = BuildMarker.Auto();

        if (_grow != null) { StopCoroutine(_grow); _grow = null; }
        _pendingGrow = -1f;
        _growing = null;
        _bars.Clear();

        rowMin = rMin; rowMax = rMax; colMin = cMin; colMax = cMax;
        _offColumns.Clear();
        _offRows.Clear();
        _rowSlots.Clear();
        _rowSlots.AddRange(data.Slots(false));
        _colSlots.Clear();
        _colSlots.AddRange(data.Slots(true));
        _rowSlotCount = data.SlotCount(false);
        _colSlotCount = data.SlotCount(true);
        _material = material;
        _cellSize = cellSize;
        _rowPitch = Mathf.Max(rowPitch, cellSize);
        _groupSize = data.ColumnGroupSize;
        _groupGap = _groupSize > 1 ? groupGap : 0f;
        _baseY = baseY;

        IReadOnlyList<int> rowOrder = data.RowOrder;
        IReadOnlyList<int> colOrder = data.ColumnOrder;

        float half = cubeSide * 0.5f;

        int used = 0;
        _byCell.Clear();
        foreach (List<CreateCube> line in _byCol.Values) line.Clear();
        foreach (List<CreateCube> line in _byRow.Values) line.Clear();

        for (int vr = rMin; vr <= rMax; vr++)
        {
            if (vr < 0 || vr >= rowOrder.Count) continue;
            int dRow = rowOrder[vr];
            float z = LineCoord(false, vr);

            for (int vc = cMin; vc <= cMax; vc++)
            {
                if (vc < 0 || vc >= colOrder.Count) continue;
                int dCol = colOrder[vc];

                bool has = data.HasValue(dRow, dCol);
                float topY = has ? data.GetHeightFraction(dRow, dCol) * height : baseY;
                float lo = Mathf.Min(topY, baseY);
                float hi = Mathf.Max(topY, baseY);

                if (hi - lo <= 1e-6f)
                {
                    float plate = height * ZeroPlateFraction * 0.5f;
                    lo = baseY - plate;
                    hi = baseY + plate;
                }

                CreateCube cube = Acquire(used++);
                cube.SetCell(vr, vc, dRow, dCol);
                Vector3 center = new Vector3(LineCoord(true, vc), (lo + hi) * 0.5f, z);
                Vector3 size = new Vector3(half * 2f, hi - lo, half * 2f);
                cube.SetBox(center, size);
                // A figure the company never reported is a gap in the row,
                // where a plate would read as the smallest figure there is.
                bool shown = has || !data.HasHeights;
                _bars.Add(new BarTarget { cube = cube, center = center, size = size });

                cube.SetColor(ColorFor(data, has, dRow, dCol));
                // A pooled cube may have been lit as another row's bar.
                cube.ClearHighlight();
                cube.SetVisible(shown);

                _byCell[Key(vr, vc)] = cube;
                LineBucket(_byCol, vc).Add(cube);
                LineBucket(_byRow, vr).Add(cube);
            }
        }

        _live.Clear();
        for (int i = 0; i < used; i++) _live.Add(_pool[i]);
        for (int i = 0; i < used; i++) _pool[i].SetMoving(false);
        _tint = null;
        for (int i = used; i < _pool.Count; i++) _pool[i].SetVisible(false);

        FitBounds();

        if (_labels == null) _labels = new SheetLabels(transform);
        _labels.Defer = !_shown;
        if (!_shown) _labels.SetShown(false);
        _labels.Rebuild(this, data, rMin, rMax, cMin, cMax, cellSize, cubeSide, baseY, labels);
    }

    // The size a sheet opens at, which ManageSheets builds as small as its
    // titles stay legible: made any smaller, the titles go, and they come back
    // once it is this size again.
    public const float DefaultScale = 1f;

    public bool LabelsLegible => Mathf.Abs(RestScale.x) >= DefaultScale - 1e-3f;

    private Vector3 RestScale => _swell.Rest(transform);

    private void LateUpdate()
    {
        if (_labels == null) return;
        // Hidden, the titles are written a few each frame, so a sheet built
        // while the one before it falls is ready to show when it is down.
        if (!_shown) { _labels.Pump(LabelBudgetMs); return; }
        _labels.SetShown(LabelsLegible && _fall == null);
        _labels.FaceViewer(Time.unscaledDeltaTime);
    }

    private const double LabelBudgetMs = 1.0;

    private static readonly Unity.Profiling.ProfilerMarker BuildMarker =
        new Unity.Profiling.ProfilerMarker("CreateSheet.Build");
    private static readonly Unity.Profiling.ProfilerMarker ShowMarker =
        new Unity.Profiling.ProfilerMarker("CreateSheet.SetShown");

    // Whether the sheet is up. A sheet put away or waiting to rise stays
    // active and only stops drawing and taking hands and pokes: switching
    // a whole sheet off and on again would remake every title's text.
    private bool _shown = true;
    private bool _pickable = true;
    private bool _grabWanted = true;

    public bool Shown => _shown;

    public void SetShown(bool on)
    {
        if (_shown == on) return;
        using var marker = ShowMarker.Auto();
        _shown = on;
        EnsureComponents();

        for (int i = 0; i < _pool.Count; i++) _pool[i].SetShown(on);
        if (_bounds != null) _bounds.enabled = on && _pickable;
        ApplyGrabbable();

        if (_labels != null)
        {
            _labels.Defer = !on;
            if (on) _labels.Flush();
            else _labels.SetShown(false);
        }

        if (on) StartPendingGrow();
    }

    // Makes cubes and titles, hidden, up to these counts, within 'budgetMs'
    // this frame, so no dataset this sheet shows later makes any. True once
    // there are that many.
    public bool Reserve(Material material, int cubes, int labels, double budgetMs)
    {
        if (_material == null) _material = material;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (_pool.Count < cubes)
        {
            if (clock.Elapsed.TotalMilliseconds >= budgetMs) return false;
            Acquire(_pool.Count).SetVisible(false);
        }
        if (_labels == null) _labels = new SheetLabels(transform);
        return _labels.Reserve(labels, budgetMs - clock.Elapsed.TotalMilliseconds);
    }

    private float _pendingGrow = -1f;

    // The bars a grow under way is raising, or null for all of them.
    private HashSet<CreateCube> _growing;

    // The bars on the sheet as drawn: a line filtered off leaves as copies of
    // these.
    public IReadOnlyList<CreateCube> LiveBars => _live;

    public void PlayGrow(float duration)
    {
        if (duration <= 0f || _bars.Count == 0) return;

        if (_grow != null) { StopCoroutine(_grow); _grow = null; }
        _growing = null;
        SetBarsMoving(true);
        ApplyGrow(0f);
        if (!isActiveAndEnabled || !_shown)
        {
            _pendingGrow = duration;
            return;
        }
        _grow = StartCoroutine(GrowRoutine(duration));
    }

    private void StartPendingGrow()
    {
        if (_pendingGrow <= 0f || !isActiveAndEnabled || !_shown) return;
        float duration = _pendingGrow;
        _pendingGrow = -1f;
        _grow = StartCoroutine(GrowRoutine(duration));
    }

    // A sheet leaving for another dataset: its bars sink into the base, its
    // titles gone at once, and 'done' runs once they are down.
    private Coroutine _fall;
    private Action _fallDone;

    public bool Falling => _fall != null;

    public void PlayFall(float duration, Action done)
    {
        CompleteGrow();
        CompleteFall();
        if (_labels != null) _labels.SetShown(false);
        SetBarsMoving(true);
        if (duration <= 0f || !isActiveAndEnabled || !_shown) { done?.Invoke(); return; }
        _fallDone = done;
        _fall = StartCoroutine(FallRoutine(duration));
    }

    // Puts a fall under way straight down and runs what was waiting on it.
    public void CompleteFall()
    {
        if (_fall == null) return;
        StopCoroutine(_fall);
        FinishFall();
    }

    private IEnumerator FallRoutine(float duration)
    {
        yield return Style.Tween(duration, k => ApplyGrow(1f - k));
        FinishFall();
    }

    private void FinishFall()
    {
        ApplyGrow(0f);
        _fall = null;
        Action done = _fallDone;
        _fallDone = null;
        done?.Invoke();
    }

    public void CompleteGrow()
    {
        bool pending = _pendingGrow > 0f;
        _pendingGrow = -1f;
        if (_grow == null && !pending) return;

        if (_grow != null)
        {
            StopCoroutine(_grow);
            _grow = null;
        }
        FinishGrow();
    }

    // Stands the growing bars full height; once the whole sheet stands, its
    // bars take pokes again.
    private void FinishGrow()
    {
        ApplyGrow(1f);
        bool whole = _growing == null;
        _growing = null;
        if (whole) SetBarsMoving(false);
    }

    // The bars take no pokes while the whole sheet rises or falls, and take
    // them again once it stands. A flag on each bar, with no collider behind
    // it, so all of them come back in one frame for nothing.
    private void SetBarsMoving(bool moving)
    {
        for (int i = 0; i < _live.Count; i++) _live[i].SetMoving(moving);
    }


    // Raises only the bars given up from the base, as a new sheet's bars rise,
    // after 'delay' seconds flat: a line coming back onto the sheet grows in
    // while the lines already there make room for it.
    public void PlayGrowOf(ICollection<CreateCube> bars, float duration, float delay)
    {
        if (duration <= 0f || bars == null || bars.Count == 0) return;

        CompleteGrow();
        _growing = new HashSet<CreateCube>(bars);
        ApplyGrow(0f);
        if (!isActiveAndEnabled || !_shown) { FinishGrow(); return; }
        _grow = StartCoroutine(GrowRoutine(duration, delay));
    }

    private IEnumerator GrowRoutine(float duration, float delay = 0f)
    {
        if (delay > 0f) yield return new WaitForSeconds(delay);
        yield return Style.Tween(duration, ApplyGrow);

        _grow = null;
        FinishGrow();
    }

    private void ApplyGrow(float k)
    {
        for (int i = 0; i < _bars.Count; i++)
        {
            BarTarget b = _bars[i];
            if (b.cube == null || (_growing != null && !_growing.Contains(b.cube))) continue;
            GrowBar(b.cube, b.center, b.size, _baseY, k);
        }
    }

    // A bar 'k' of the way grown out of the base at 'baseY', top and bottom
    // alike, toward the box it stands in when grown.
    public static void GrowBar(CreateCube cube, Vector3 center, Vector3 size, float baseY, float k)
    {
        float half = size.y * 0.5f;
        float lo = Mathf.Lerp(baseY, center.y - half, k);
        float hi = Mathf.Lerp(baseY, center.y + half, k);
        cube.SetBox(new Vector3(center.x, (lo + hi) * 0.5f, center.z), new Vector3(size.x, hi - lo, size.z));
    }

    // How far the further of a bar's top and bottom is from the base.
    public static float BarTravel(Vector3 center, Vector3 size, float baseY)
    {
        float half = size.y * 0.5f;
        return Mathf.Max(Mathf.Abs(center.y + half - baseY), Mathf.Abs(center.y - half - baseY));
    }

    // The furthest the top or bottom of any bar travels growing from the
    // base, or sinking into it, in the sheet's own units: of 'only' when
    // given, else of every bar. What a grow, a fall or a filter's bars are
    // timed by, the fastest-moving point being that one.
    public float GrowTravel(ICollection<CreateCube> only = null)
    {
        float most = 0f;
        for (int i = 0; i < _bars.Count; i++)
        {
            BarTarget b = _bars[i];
            if (b.cube == null || (only != null && !only.Contains(b.cube))) continue;
            most = Mathf.Max(most, BarTravel(b.center, b.size, _baseY));
        }
        return most;
    }

    public void CollectLineCoords(Dictionary<int, float> columns, Dictionary<int, float> rows)
    {
        for (int i = 0; i < _live.Count; i++)
        {
            CreateCube cube = _live[i];
            if (cube == null) continue;
            Vector3 p = cube.transform.localPosition;
            columns[cube.dataCol] = p.x;
            rows[cube.dataRow] = p.z;
        }
    }

    // A bar is drawn in the colour of the metro its row belongs to, which the
    // sheet states per row, so reordering or filtering never repaints one.
    // A metro with no colour of its own is grey on the map; its bars shade
    // from black to white, so their value reads as every other metro's does.
    private static Color ColorFor(DataSource data, bool has, int dRow, int dCol)
    {
        if (!has) return NoData;
        Color top = data.RowColorAt(dRow);
        if (Uncoloured(top)) top = Color.white;
        return Shade(top, data.GetColorFraction(dRow, dCol));
    }

    // A metro left out of the palette, which the map and the sheet both draw in greys.
    public static bool Uncoloured(Color c) => c.maxColorComponent < 0.01f;

    private static Color Shade(Color top, float t) =>
        Color.Lerp(Color.black, top, PerceptualFraction(t));

    public static float PerceptualFraction(float t)
    {
        t = Mathf.Clamp01(t);
        float scaled = t * (Lightness.Length - 1);
        int lower = Mathf.FloorToInt(scaled);
        int upper = Mathf.Min(lower + 1, Lightness.Length - 1);
        return Mathf.Lerp(Lightness[lower], Lightness[upper], scaled - lower);
    }

    private static List<CreateCube> LineBucket(Dictionary<int, List<CreateCube>> map, int line)
    {
        if (!map.TryGetValue(line, out List<CreateCube> bucket))
        {
            bucket = new List<CreateCube>();
            map[line] = bucket;
        }
        return bucket;
    }

    private List<CreateCube> LineCubes(bool columns, int line) =>
        (columns ? _byCol : _byRow).TryGetValue(line, out List<CreateCube> bucket) ? bucket : null;

    public void LayoutLine(bool columns, int line, float coord)
    {
        List<CreateCube> cubes = LineCubes(columns, line);
        if (cubes != null)
        {
            for (int i = 0; i < cubes.Count; i++)
            {
                CreateCube cube = cubes[i];
                if (cube == null) continue;

                Vector3 p = cube.transform.localPosition;
                if (columns) p.x = coord;
                else p.z = coord;
                cube.transform.localPosition = p;
            }
        }

        if (_labels != null) _labels.MoveLine(columns, line, coord);

        HashSet<int> off = columns ? _offColumns : _offRows;
        if (Mathf.Abs(coord - LineCoord(columns, line)) > 1e-4f) off.Add(line);
        else off.Remove(line);
    }

    public float LineOffset(bool columns, int line)
    {
        List<CreateCube> cubes = LineCubes(columns, line);
        if (cubes != null)
        {
            for (int i = 0; i < cubes.Count; i++)
            {
                CreateCube cube = cubes[i];
                if (cube == null) continue;

                Vector3 p = cube.transform.localPosition;
                return columns ? p.x : p.z;
            }
        }
        return LineCoord(columns, line);
    }

    public void RestLines()
    {
        for (int vc = colMin; vc <= colMax; vc++) LayoutLine(true, vc, LineCoord(true, vc));
        for (int vr = rowMin; vr <= rowMax; vr++) LayoutLine(false, vr, LineCoord(false, vr));

        for (int i = 0; i < _live.Count; i++)
            if (_live[i] != null) _live[i].transform.localRotation = Quaternion.identity;
    }

    // What the bars are lit with now, or null when that is not known (after a
    // build). A tool re-sends its tint every frame the finger moves, so an
    // unchanged one is not walked across every bar again. Axis 1 is columns,
    // 2 rows, and 0 nothing lit.
    private (int axis, int min, int max, float swell)? _tint;
    private static readonly (int, int, int, float) Untinted = (0, 0, 0, 0f);

    public void SetHoverTint(bool columns, int min, int max, float swell)
    {
        var tint = (columns ? 1 : 2, min, max, swell);
        if (_tint == tint) return;
        _tint = tint;

        for (int i = 0; i < _live.Count; i++)
        {
            CreateCube cube = _live[i];
            int v = columns ? cube.visCol : cube.visRow;
            bool hit = v >= min && v <= max;
            if (hit) cube.SetHighlight(swell);
            else cube.ClearHighlight();
        }
    }

    public void ClearTint()
    {
        if (_tint == Untinted) return;
        _tint = Untinted;

        for (int i = 0; i < _live.Count; i++) _live[i].ClearHighlight();
    }

    private GrabSwell _swell;

    public void SetGrabLook(bool on) => _swell.Set(transform, on);

    public void ForgetGrabLook() => _swell.Forget(transform);

    // Puts a sheet kept from an earlier dataset back as a fresh one would stand,
    // so its cubes and labels can be built again in place rather than made anew.
    // The Sort tool's handles on it were put away when the tool was.
    public void ResetForReuse(Vector3 localPosition, Quaternion localRotation, Vector3 localScale)
    {
        CompleteGrow();
        // What waited on a fall still under way runs, or it would wait forever.
        CompleteFall();
        _bars.Clear();
        _wasGrabbed = false;
        ClearTint();
        transform.SetLocalPositionAndRotation(localPosition, localRotation);
        transform.localScale = localScale;
        ForgetGrabLook();
    }

    public void SetPickable(bool on)
    {
        EnsureComponents();
        _pickable = on;
        if (_bounds != null) _bounds.enabled = on && _shown;
        for (int i = 0; i < _pool.Count; i++) _pool[i].SetPickable(on);
    }

    private void FitBounds()
    {
        EnsureComponents();
        if (_live.Count == 0) { _bounds.size = Vector3.one * 1e-3f; return; }

        Bounds b = new Bounds(_live[0].transform.localPosition, Vector3.zero);
        for (int i = 0; i < _live.Count; i++)
        {
            CreateCube c = _live[i];
            b.Encapsulate(new Bounds(c.transform.localPosition, c.transform.localScale));
        }
        _bounds.center = b.center;
        _bounds.size = b.size;
    }

    private void EnsureComponents()
    {
        if (_bounds == null) _bounds = GetComponent<BoxCollider>();
        if (_body == null)
        {
            _body = GetComponent<Rigidbody>();
            _body.isKinematic = true;
            _body.useGravity = false;
        }
        // The sheet's own, not a Sort handle's: those stand under the sheet too.
        if (_grabbable == null) _grabbable = GetComponent<Grabbable>();
        if (_grabbable != null && _grabbable.Transform == null)
            _grabbable.InjectOptionalTargetTransform(_grabbable.transform);
        if (_handGrab == null) _handGrab = GetComponent<HandGrabInteractable>();
        if (_handGrab != null && _handGrab.gameObject == gameObject) MoveGrabToGrip();
        if (_slide == null) _slide = GetComponent<OneGrabTranslateTransformer>();
    }

    public bool IsGrabbed
    {
        get
        {
            EnsureComponents();
            return _grabbable != null && _grabbable.SelectingPointsCount > 0;
        }
    }

    public bool PollGrabRelease(out Vector3 prePos, out Quaternion preRot, out Vector3 preScale)
    {
        bool grabbed = IsGrabbed;
        if (grabbed && !_wasGrabbed)
        {
            _wasGrabbed = true;
            _grabPos = transform.localPosition;
            _grabRot = transform.localRotation;
            _grabScale = transform.localScale;
        }
        else if (!grabbed) return ForceGrabRelease(out prePos, out preRot, out preScale);

        prePos = Vector3.zero;
        preRot = Quaternion.identity;
        preScale = Vector3.one;
        return false;
    }

    public bool ForceGrabRelease(out Vector3 prePos, out Quaternion preRot, out Vector3 preScale)
    {
        prePos = Vector3.zero;
        preRot = Quaternion.identity;
        preScale = Vector3.one;

        if (!_wasGrabbed) return false;
        _wasGrabbed = false;

        prePos = _grabPos;
        preRot = _grabRot;
        preScale = _grabScale;
        return true;
    }

    public void SetGrabbable(bool on)
    {
        _grabWanted = on;
        ApplyGrabbable();
    }

    private void ApplyGrabbable()
    {
        EnsureComponents();
        bool on = _grabWanted && _shown;
        if (_grabbable != null) _grabbable.enabled = on;
        if (_handGrab != null) _handGrab.enabled = on;
    }

    // Stands the hand's grab on a child of its own, "Grip", that holds one box
    // the size of the bars and nothing else. A grab takes as its own every
    // collider under its body, and only the first time it is switched on; on
    // the sheet's own body that was every Sort handle stood under the sheet
    // by then, thousands on a large sheet, each measured against the hand
    // every frame and moved by the physics while the sheet was held. The
    // grab is still the sheet's: the sheet's Grabbable takes the hold and
    // moves the sheet, the grip with it.
    private void MoveGrabToGrip()
    {
        HandGrabInteractable old = _handGrab;

        var go = new GameObject("Grip");
        go.layer = gameObject.layer;
        go.transform.SetParent(transform, false);
        Rigidbody body = go.AddComponent<Rigidbody>();
        body.isKinematic = true;
        body.useGravity = false;
        BoxCollider box = go.AddComponent<BoxCollider>();
        if (_bounds != null)
        {
            box.center = _bounds.center;
            box.size = _bounds.size;
            box.enabled = _bounds.enabled;
            _bounds.enabled = false;
        }

        HandGrabInteractable grip = go.AddComponent<HandGrabInteractable>();
        grip.InjectAllHandGrabInteractable(old.SupportedGrabTypes, body, old.PinchGrabRules, old.PalmGrabRules);
        grip.InjectOptionalScoreModifier(old.ScoreModifier);
        grip.InjectOptionalPointableElement(_grabbable);
        grip.enabled = _grabWanted && _shown;

        old.enabled = false;
        Destroy(old);

        _handGrab = grip;
        _bounds = box;
    }

    public void SetOneGrab()
    {
        EnsureComponents();
        if (_grabbable == null) return;

        _grabbable.MaxGrabPoints = 1;
        if (_slide == null) return;
        _grabbable.InjectOptionalOneGrabTransformer(_slide);
        // The grabbable readies only the transformers it holds when it starts;
        // one put back after Rotate or Scale swapped it out must be readied
        // here, or the first grab finds it bound to nothing.
        _slide.Initialize(_grabbable);
    }

    public void SetTwoGrab(ITransformer transformer)
    {
        EnsureComponents();
        if (_grabbable == null || transformer == null) return;

        _grabbable.MaxGrabPoints = 2;
        _grabbable.InjectOptionalOneGrabTransformer(null);
        _grabbable.InjectOptionalTwoGrabTransformer(transformer);
        transformer.Initialize(_grabbable);
    }

    private CreateCube Acquire(int index)
    {
        while (_pool.Count <= index)
        {
            GameObject go = new GameObject($"Cube_{_pool.Count}");
            go.transform.SetParent(transform, false);
            CreateCube cube = go.AddComponent<CreateCube>();
            cube.Init(_material);
            cube.SetSheet(this);
            cube.SetShown(_shown);
            cube.SetPickable(_pickable);
            _pool.Add(cube);
        }
        return _pool[index];
    }

    private void OnEnable() => StartPendingGrow();

    private static int Key(int visRow, int visCol) => (visRow << 16) | (visCol & 0xFFFF);
}
