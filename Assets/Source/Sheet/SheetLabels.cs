using System.Collections.Generic;
using TMPro;
using UnityEngine;

public struct SheetLabelStyle
{
    public bool show;
    public Color color;
    public float gap;
}

public class SheetLabels
{
    private static readonly Vector2 LabelBox = new Vector2(20f, 4f);

    private const float FadeSeconds = 0.12f;

    private class Side
    {
        public bool high;
        public bool flipping;
        public bool want;
        public float fade = 1f;
    }

    private readonly Transform _owner;
    private readonly List<TextMeshPro> _pool = new List<TextMeshPro>();
    private readonly Dictionary<int, TextMeshPro> _colLabel = new Dictionary<int, TextMeshPro>();
    private readonly Dictionary<int, TextMeshPro> _rowLabel = new Dictionary<int, TextMeshPro>();

    // On a grouped axis the column labels become small series ticks and the
    // metric name is drawn once, further out, riding the middle of its block.
    private readonly Dictionary<int, TextMeshPro> _colGroup = new Dictionary<int, TextMeshPro>();
    private int _colGroupSize = 1;
    private float _colGroupOut;

    private const float TickScale = 0.72f;

    // The cell pitch the fixed text scale was chosen against. A label is sized
    // relative to the column it stands under rather than in metres, so the sheet
    // can be a tabletop model or a wall and the titles stay the same size
    // against the bars instead of running into one another at the small end.
    private const float ReferenceCellSize = 0.1f;

    // How much larger than that fixed scale the titles are drawn, at the
    // sheet's default size.
    private const float TitleSize = 1.2f;

    private float _textScale = 1f;
    private readonly Side _cols = new Side();
    private readonly Side _rows = new Side();
    private Transform _root;
    private int _used;
    private bool _placed;
    private bool _shown = true;

    private float _lowZ;
    private float _highZ;
    private float _lowX;
    private float _highX;
    private float _margin;

    public SheetLabels(Transform owner) => _owner = owner;

    // While the sheet is hidden its titles are placed at once but written a
    // few a frame (Pump): writing one remakes its text mesh, and a whole
    // sheet's worth at once is a stall. Flush writes the rest, as the sheet
    // is shown.
    public bool Defer { get; set; }

    private readonly List<(TextMeshPro label, string text)> _unwritten = new List<(TextMeshPro, string)>();
    private int _written;

    private static readonly Unity.Profiling.ProfilerMarker RebuildMarker =
        new Unity.Profiling.ProfilerMarker("SheetLabels.Rebuild");
    private static readonly Unity.Profiling.ProfilerMarker PumpMarker =
        new Unity.Profiling.ProfilerMarker("SheetLabels.Pump");

    public void Pump(double budgetMs)
    {
        if (_written >= _unwritten.Count) return;
        using var marker = PumpMarker.Auto();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (_written < _unwritten.Count && clock.Elapsed.TotalMilliseconds < budgetMs)
        {
            (TextMeshPro label, string text) = _unwritten[_written++];
            if (label == null) continue;
            label.text = text;
            label.ForceMeshUpdate();
        }
    }

    // Makes titles, hidden, up to 'count', within 'budgetMs' this frame.
    // True once there are that many.
    public bool Reserve(int count, double budgetMs)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (_pool.Count < count)
        {
            if (clock.Elapsed.TotalMilliseconds >= budgetMs) return false;
            TextMeshPro label = Acquire(_pool.Count);
            if (label == null) return true;
            label.renderer.enabled = false;
        }
        return true;
    }

    public void Flush()
    {
        while (_written < _unwritten.Count)
        {
            (TextMeshPro label, string text) = _unwritten[_written++];
            if (label != null) label.text = text;
        }
        _unwritten.Clear();
        _written = 0;
    }

    // Hidden, the titles stop turning to the viewer; shown again, they snap
    // to whichever edges face the viewer by then. They stop drawing rather
    // than switching off, so showing them again costs nothing.
    public void SetShown(bool on)
    {
        if (_shown == on) return;
        _shown = on;
        if (on) _placed = false;
        for (int i = 0; i < _used && i < _pool.Count; i++)
            if (_pool[i] != null) _pool[i].renderer.enabled = on;
    }

    public void FaceViewer(float dt)
    {
        if (_used == 0 || _owner == null || !_shown) return;

        Transform cam = CameraRig.MainTransform;
        if (cam == null) return;

        Vector3 local = _owner.InverseTransformPoint(cam.position);

        if (!_placed)
        {
            _placed = true;
            Snap(_cols, local.z > 0f, _colLabel, _lowZ, _highZ, true);
            Snap(_rows, local.x > 0f, _rowLabel, _lowX, _highX, false);
            return;
        }

        Step(_cols, Decide(local.z, _cols.high), dt, _colLabel, _lowZ, _highZ, true);
        Step(_rows, Decide(local.x, _rows.high), dt, _rowLabel, _lowX, _highX, false);
    }

    private void Snap(Side side, bool high, Dictionary<int, TextMeshPro> map,
        float lowEdge, float highEdge, bool columns)
    {
        side.high = high;
        side.flipping = false;
        side.fade = 1f;

        ApplyEdges(map, high ? highEdge : lowEdge, columns, high);
        ApplyFades(map, 1f, columns);
    }

    private bool Decide(float coord, bool high) =>
        high ? coord > -_margin : coord > _margin;

    private void Step(Side side, bool desired, float dt,
        Dictionary<int, TextMeshPro> map, float lowEdge, float highEdge, bool columns)
    {
        if (!side.flipping && desired != side.high)
        {
            side.flipping = true;
            side.want = desired;
        }

        if (side.flipping)
        {
            side.fade -= dt / FadeSeconds;
            if (side.fade <= 0f)
            {
                side.fade = 0f;
                side.high = side.want;
                side.flipping = false;
                ApplyEdges(map, side.high ? highEdge : lowEdge, columns, side.high);
            }
        }
        else if (side.fade < 1f)
        {
            side.fade = Mathf.Min(1f, side.fade + dt / FadeSeconds);
        }
        else return;

        ApplyFades(map, side.fade, columns);
    }

    private const float UpwardTiltDegrees = 45f;

    private static Quaternion Facing(bool columns, bool high) =>
        (columns
            ? Quaternion.Euler(0f, high ? 180f : 0f, 0f)
            : Quaternion.Euler(0f, high ? -90f : 90f, 0f))
        * Quaternion.Euler(UpwardTiltDegrees, 0f, 0f);

    private void ApplyEdges(Dictionary<int, TextMeshPro> map, float edge, bool columns, bool high)
    {
        ApplyEdge(map, edge, columns, high);
        if (columns && _colGroup.Count > 0)
            ApplyEdge(_colGroup, edge + (high ? _colGroupOut : -_colGroupOut), columns, high);
    }

    private void ApplyFades(Dictionary<int, TextMeshPro> map, float alpha, bool columns)
    {
        ApplyFade(map, alpha);
        if (columns) ApplyFade(_colGroup, alpha);
    }

    private static void ApplyEdge(Dictionary<int, TextMeshPro> map, float edge, bool columns, bool high)
    {
        Quaternion facing = Facing(columns, high);

        foreach (KeyValuePair<int, TextMeshPro> pair in map)
        {
            if (pair.Value == null) continue;

            Vector3 p = pair.Value.transform.localPosition;
            if (columns) p.z = edge;
            else p.x = edge;
            pair.Value.transform.localPosition = p;
            pair.Value.transform.localRotation = facing;
        }
    }

    private static void ApplyFade(Dictionary<int, TextMeshPro> map, float alpha)
    {
        foreach (KeyValuePair<int, TextMeshPro> pair in map)
            if (pair.Value != null) pair.Value.alpha = alpha;
    }

    public void MoveLine(bool columns, int line, float coord)
    {
        Dictionary<int, TextMeshPro> map = columns ? _colLabel : _rowLabel;
        if (map.TryGetValue(line, out TextMeshPro label) && label != null)
        {
            Vector3 p = label.transform.localPosition;
            if (columns) p.x = coord;
            else p.z = coord;
            label.transform.localPosition = p;
        }

        if (columns && _colGroupSize > 1) CentreGroup(line / _colGroupSize);
    }

    // The metric name sits at the mean of its ticks, so it follows however they move.
    private void CentreGroup(int group)
    {
        if (!_colGroup.TryGetValue(group, out TextMeshPro label) || label == null) return;

        float sum = 0f;
        int found = 0;
        for (int s = 0; s < _colGroupSize; s++)
            if (_colLabel.TryGetValue(group * _colGroupSize + s, out TextMeshPro tick) && tick != null)
            {
                sum += tick.transform.localPosition.x;
                found++;
            }
        if (found == 0) return;

        Vector3 p = label.transform.localPosition;
        p.x = sum / found;
        label.transform.localPosition = p;
    }

    // "2019" reads better as "'19" once the metric name carries the meaning.
    private static string Tick(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        string t = title.Trim();
        if (t.Length != 4) return t;
        for (int i = 0; i < 4; i++)
            if (!char.IsDigit(t[i])) return t;
        return "'" + t.Substring(2);
    }

    // Adds every glyph a dataset's titles use to the body font now, when it
    // is read, rather than mid-switch when its sheet is first built. A metric's
    // name is a run of its columns' titles, so those are covered too.
    public static void WarmGlyphs(DataSource data)
    {
        TMP_FontAsset font = Style.BodyFont;
        if (font == null || data == null) return;
        // A static font holds what it was baked with and takes nothing more.
        if (font.atlasPopulationMode != AtlasPopulationMode.Dynamic) return;

        var seen = new HashSet<char>();
        var missing = new System.Text.StringBuilder();
        void Collect(IReadOnlyList<string> titles)
        {
            for (int i = 0; i < titles.Count; i++)
            {
                string t = titles[i];
                if (t == null) continue;
                foreach (char c in t)
                    if (seen.Add(c) && !font.characterLookupTable.ContainsKey(c)) missing.Append(c);
            }
        }
        Collect(data.RowTitles);
        Collect(data.ColumnTitles);
        if (missing.Length > 0) font.TryAddCharacters(missing.ToString(), out _);
    }

    // How long a title runs, in the sheet's metres, at the size titles are
    // drawn on a sheet of this cell size: its glyphs' advances in the body
    // font, as TextMeshPro sets them. The sheet spaces its rows by the longest.
    public static float TitleLength(string text, float cellSize)
    {
        if (string.IsNullOrEmpty(text)) return 0f;

        TMP_FontAsset font = Style.BodyFont;
        float units;
        // Read only: the glyphs were added when the dataset was read
        // (WarmGlyphs), and asking a static font to add any logs a warning,
        // with its stack trace in a development build, for every title.
        if (font != null && font.faceInfo.pointSize > 0f)
        {
            float advance = 0f;
            foreach (char c in text)
                advance += font.characterLookupTable.TryGetValue(c, out TMP_Character glyph)
                    ? glyph.glyph.metrics.horizontalAdvance * glyph.glyph.scale
                    : font.faceInfo.pointSize * 0.6f;
            // A TextMeshPro in the world draws a point a tenth of a unit.
            units = advance * Style.Body / font.faceInfo.pointSize * font.faceInfo.scale * 0.1f;
        }
        else units = text.Length * Style.Body * 0.06f;

        return units * Style.WorldTextScale * TextScaleFor(cellSize);
    }

    // How large titles are drawn on cells 'cellSize' across.
    private static float TextScaleFor(float cellSize) =>
        TitleSize * (cellSize > 0f ? cellSize / ReferenceCellSize : 1f);

    public void Rebuild(CreateSheet owner, DataSource data, int rowMin, int rowMax, int colMin, int colMax,
        float cellSize, float cubeSide, float baseY, SheetLabelStyle style)
    {
        using var marker = RebuildMarker.Auto();
        _used = 0;
        _unwritten.Clear();
        _written = 0;
        _colLabel.Clear();
        _rowLabel.Clear();
        _colGroup.Clear();
        _colGroupSize = data != null ? data.ColumnGroupSize : 1;
        _colGroupOut = cellSize * 0.85f;
        _textScale = TextScaleFor(cellSize);

        _cols.fade = 1f;
        _cols.flipping = false;
        _rows.fade = 1f;
        _rows.flipping = false;

        if (data != null && style.show && cellSize > 0f)
        {
            float half = cubeSide * 0.5f;
            float y = baseY;

            // Each title stands at its line's place on the sheet, which a
            // hidden line keeps empty; the edges the titles run along are
            // the sheet's own, every slot included, so hiding an end line
            // moves none of them.
            float ColX(int vc) => owner.LineCoord(true, vc);
            float RowZ(int vr) => owner.LineCoord(false, vr);

            _margin = cellSize;
            _lowZ = owner.SlotCoord(false, owner.FirstSlot(false)) - half - style.gap;
            _highZ = owner.SlotCoord(false, owner.LastSlot(false)) + half + style.gap;
            _lowX = owner.SlotCoord(true, owner.FirstSlot(true)) - half - style.gap;
            _highX = owner.SlotCoord(true, owner.LastSlot(true)) + half + style.gap;

            IReadOnlyList<int> colOrder = data.ColumnOrder;
            IReadOnlyList<string> colTitles = data.ColumnTitles;
            float edgeZ = _cols.high ? _highZ : _lowZ;
            bool grouped = _colGroupSize > 1;
            for (int vc = colMin; vc <= colMax; vc++)
            {
                if (vc < 0 || vc >= colOrder.Count) continue;
                int dCol = colOrder[vc];
                if (dCol < 0 || dCol >= colTitles.Count) continue;

                string text = grouped ? Tick(data.SeriesTitleAt(true, vc)) : colTitles[dCol];
                TextMeshPro label = Place(text,
                    new Vector3(ColX(vc), y, edgeZ), style.color,
                    Facing(true, _cols.high), grouped ? TickScale : 1f);
                if (label != null) _colLabel[vc] = label;
            }

            if (grouped)
            {
                float groupZ = edgeZ + (_cols.high ? _colGroupOut : -_colGroupOut);
                for (int g = 0; g <= colMax / _colGroupSize; g++)
                {
                    int lo = g * _colGroupSize;
                    if (lo < colMin || lo > colMax) continue;
                    TextMeshPro label = Place(data.GroupTitleAt(true, g),
                        new Vector3(ColX(lo), y, groupZ), style.color,
                        Facing(true, _cols.high));
                    if (label == null) continue;
                    _colGroup[g] = label;
                    CentreGroup(g);
                }
            }

            IReadOnlyList<int> rowOrder = data.RowOrder;
            IReadOnlyList<string> rowTitles = data.RowTitles;
            float edgeX = _rows.high ? _highX : _lowX;
            for (int vr = rowMin; vr <= rowMax; vr++)
            {
                if (vr < 0 || vr >= rowOrder.Count) continue;
                int dRow = rowOrder[vr];
                if (dRow < 0 || dRow >= rowTitles.Count) continue;
                TextMeshPro label = Place(rowTitles[dRow],
                    new Vector3(edgeX, y, RowZ(vr)), style.color,
                    Facing(false, _rows.high));
                if (label != null) _rowLabel[vr] = label;
            }
        }

        for (int i = _used; i < _pool.Count; i++)
            if (_pool[i] != null) _pool[i].renderer.enabled = false;
    }

    private TextMeshPro Place(string text, Vector3 localPosition, Color color, Quaternion facing,
        float scale = 1f)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        TextMeshPro label = Acquire(_used++);
        if (label == null) return null;

        if (Defer) _unwritten.Add((label, text));
        else label.text = text;
        label.color = color;

        Transform t = label.transform;
        t.localPosition = localPosition;
        t.localRotation = facing;
        t.localScale = Vector3.one * (Style.WorldTextScale * scale * _textScale);

        label.renderer.enabled = _shown;
        return label;
    }

    private TextMeshPro Acquire(int index)
    {
        EnsureRoot();
        if (_root == null) return null;

        while (_pool.Count <= index)
        {
            GameObject go = new GameObject($"Label_{_pool.Count}");
            go.transform.SetParent(_root, false);

            TextMeshPro label = go.AddComponent<TextMeshPro>();
            Style.ApplyBody(label);
            label.alignment = TextAlignmentOptions.Center;
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Overflow;
            label.rectTransform.sizeDelta = LabelBox;
            _pool.Add(label);
        }

        return _pool[index];
    }

    private void EnsureRoot()
    {
        if (_root != null || _owner == null) return;
        GameObject go = new GameObject("Labels");
        _root = go.transform;
        _root.SetParent(_owner, false);
    }
}
