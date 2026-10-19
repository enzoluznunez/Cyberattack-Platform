using System.Collections.Generic;
using UnityEngine;

// Chooses what stands on the sheet, on either axis: which breaches and which
// figures. A poke takes a line off the sheet and a poke where it stood brings
// it back. Nothing moves when a line goes: every other line keeps its place,
// and the line's place, its bars and its title with it, is simply empty until
// it is poked again. Every breach on a sheet reports every figure, so an empty
// place can only be a line filtered away, never one with nothing to show.
//
// Whether a poke means a row or a column is told the way the Sort tool tells
// it, by where along the bars' height the finger is: in the lower band it is
// the row, in the upper band the column, and past either end of a line, at
// any height, that line. Each line's volume is measured from its place on the
// sheet, not from its bars, so an empty place answers as a full one does.
public class FilterTool : Tool
{
    protected override ToolType Kind => ToolType.Filter;

    // The Sort tool's bands, as shares of the bars' full height
    // (SortLineProxy): rows below, columns above, a gap between.
    private const float HalfBand = SortLineProxy.BandHeight * 0.5f;
    private const float RowBandLow = SortLineProxy.RowBand - HalfBand, RowBandHigh = SortLineProxy.RowBand + HalfBand;
    private const float ColumnBandLow = SortLineProxy.ColumnBand - HalfBand, ColumnBandHigh = SortLineProxy.ColumnBand + HalfBand;

    // A poke is a fingertip coming to rest on a line inside the sheet's
    // volume: the line it has stayed on for this long is taken or brought
    // back, then nothing more until the finger has left the volume. Passing
    // through a band on the way to another, as a finger reaching a row from
    // above passes the column band, is too quick to count.
    private const float PokeDwell = 0.12f;

    private struct Tip
    {
        public bool fired;
        public bool rows;
        public int slot;
        public float since;
    }

    // The fingertips inside the sheet's volume, by index into Fingertips.All.
    private readonly Dictionary<int, Tip> _inside = new Dictionary<int, Tip>();

    private static DataSource Data => Scene.Data;

    // Undo All resets every tool: the filter it clears is the whole of this
    // tool's state, so the sheet comes back whole.
    protected override void OnResetTool()
    {
        DataSource data = Data;
        if (data == null) return;

        bool columns = data.SetHiddenOn(false, null, out _);
        bool rows = data.SetHiddenOn(true, null, out _);

        string rowNouns = DataSource.Plural(DataSource.FilterNoun(data, true));
        string columnNouns = DataSource.Plural(DataSource.FilterNoun(data, false));
        if (columns && rows) Report($"put every one of the {rowNouns} and {columnNouns} back on the sheet");
        else if (columns) Report($"showed every one of the {columnNouns} again");
        else if (rows) Report($"showed every one of the {rowNouns} again");
    }

    // Leaving the tool leaves no line lit behind it.
    protected override void ClearToolState()
    {
        ClearTint();
        _inside.Clear();
    }

    private void LateUpdate()
    {
        if (!Active) return;

        DataSource data = Data;
        CreateSheet sheet = sheetManager != null ? sheetManager.Sheet : null;
        if (data == null || !data.IsLoaded || sheet == null || !sheet.Shown)
        {
            ClearToolState();
            return;
        }

        IReadOnlyList<Fingertips.Source> tips = Fingertips.All;
        bool lit = false;
        float now = Time.unscaledTime;
        for (int i = 0; i < tips.Count; i++)
        {
            bool inVolume = false, rows = false;
            int slot = -1;
            bool on = tips[i].Live && TryLineAt(sheet, tips[i].poke.Origin, out inVolume, out rows, out slot);
            if (!inVolume) { _inside.Remove(i); continue; }

            if (!_inside.TryGetValue(i, out Tip tip)) tip.slot = -1;
            if (!on) { tip.slot = -1; _inside[i] = tip; continue; }
            if (tip.slot != slot || tip.rows != rows) { tip.rows = rows; tip.slot = slot; tip.since = now; }

            if (!tip.fired && now - tip.since >= PokeDwell)
            {
                tip.fired = true;
                _inside[i] = tip;
                Poke(data, sheet, rows, slot);
                continue;
            }
            _inside[i] = tip;
            if (tip.fired) continue;

            // The line about to go is lit, the way the Profile tool lights the
            // line it is about to raise; an empty place has nothing to light.
            if (lit) continue;
            int line = sheet.LineAtSlot(!rows, slot);
            if (line < 0) continue;
            TintLine(sheet, !rows, line);
            lit = true;
        }
        if (!lit) ClearTint();
    }

    // The line a point is on, by its slot on the sheet, and whether it is a
    // row; false off any line. 'inVolume' says whether the point is within
    // the sheet's volume at all, gaps between the bands included.
    private bool TryLineAt(CreateSheet sheet, Vector3 world, out bool inVolume, out bool rows, out int slot)
    {
        inVolume = false;
        rows = false;
        slot = -1;
        Vector3 p = sheet.transform.InverseTransformPoint(world);
        float height = sheetManager.Height;
        if (p.y < 0f || p.y > height) return false;

        // Past either end of the rows (left or right) is a row; past either
        // end of the columns (near or far) is a column; within the sheet, the
        // band says which.
        bool pastRowEnds = Outside(sheet, true, p.x, out bool inRowReach);
        bool pastColumnEnds = Outside(sheet, false, p.z, out bool inColumnReach);
        if (!inRowReach || !inColumnReach) return false;
        inVolume = true;
        if (pastRowEnds && pastColumnEnds) return false;

        float share = p.y / Mathf.Max(height, 1e-6f);
        if (pastRowEnds) rows = true;
        else if (pastColumnEnds) rows = false;
        else if (share >= RowBandLow && share <= RowBandHigh) rows = true;
        else if (share >= ColumnBandLow && share <= ColumnBandHigh) rows = false;
        else return false;

        // A row is found along the sheet's depth, a column along its width.
        bool columns = !rows;
        float coord = columns ? p.x : p.z;
        slot = Mathf.RoundToInt(sheet.SlotFraction(columns, coord));
        return slot >= sheet.FirstSlot(columns) && slot <= sheet.LastSlot(columns);
    }

    // Whether a coordinate along one axis lies past the sheet's ends there,
    // and whether it is still within reach of them: a line's handle past its
    // end is one line's pitch long, as the Sort tool's grips are.
    private static bool Outside(CreateSheet sheet, bool columns, float coord, out bool inReach)
    {
        float half = sheet.PitchOn(columns) * 0.5f;
        float low = sheet.SlotCoord(columns, sheet.FirstSlot(columns)) - half;
        float high = sheet.SlotCoord(columns, sheet.LastSlot(columns)) + half;
        float reach = sheet.BlockPitch(!columns);
        bool outside = coord < low || coord > high;
        inReach = coord >= low - reach && coord <= high + reach;
        return outside;
    }

    // Takes the line at a slot off the sheet, or brings it back.
    private void Poke(DataSource data, CreateSheet sheet, bool rows, int slot)
    {
        ClearTint();
        List<int> order = data.FullOrder(!rows);
        if (slot < 0 || slot >= order.Count) return;
        int id = rows ? order[slot] : data.DataGroupOf(order[slot]);
        // The poke landed, taken or refused, so it clicks as a button does.
        UIPressFeedback.Play();
        if (!Toggle(rows, id, out string refusal) && refusal != null)
            Notices.Show(this, "Filter", refusal);
    }

    public bool Toggle(bool rows, int id, out string refusal)
    {
        refusal = null;

        DataSource data = Data;
        if (data == null || !data.IsLoaded) { refusal = "No dataset is open."; return false; }

        List<int> hidden = data.HiddenInOrder(rows);
        if (data.IsHidden(rows, id)) hidden.Remove(id);
        else hidden.Add(id);

        return Apply(rows, hidden, out refusal);
    }

    // The whole hidden set at once: one edit on the timeline however many lines
    // it covers, so undo puts the sheet back the way one action found it.
    public bool Apply(bool rows, IReadOnlyList<int> hidden, out string refusal)
    {
        refusal = null;

        DataSource data = Data;
        if (data == null || !data.IsLoaded) { refusal = "No dataset is open."; return false; }

        List<int> before = data.HiddenInOrder(rows);
        if (!data.SetHiddenOn(rows, hidden, out refusal)) return false;

        List<int> after = data.HiddenInOrder(rows);
        ManageDatasets.ActiveEdits.PushFilter(before, after, rows);

        Report(Describe(data, before, after, rows));
        return true;
    }

    private static string Describe(DataSource data, List<int> before, List<int> after, bool rows)
    {
        var gone = new List<string>();
        var back = new List<string>();

        var wasHidden = new HashSet<int>(before);
        var isHidden = new HashSet<int>(after);
        for (int i = 0; i < after.Count; i++)
            if (!wasHidden.Contains(after[i])) gone.Add(DataSource.FilterLabel(data, rows, after[i]));
        for (int i = 0; i < before.Count; i++)
            if (!isHidden.Contains(before[i])) back.Add(DataSource.FilterLabel(data, rows, before[i]));

        string noun = DataSource.FilterNoun(data, rows);
        var parts = new List<string>(2);
        if (gone.Count > 0) parts.Add($"took {Summarize(gone, noun)} off the sheet");
        if (back.Count > 0) parts.Add($"put {Summarize(back, noun)} back on the sheet");

        string what = parts.Count > 0
            ? string.Join(" and ", parts)
            : $"left the same {DataSource.Plural(noun)} on the sheet";

        int showing = rows ? data.RowOrder.Count : data.GroupCount(true);
        int total = rows ? data.RowCount : data.DataGroupCount;
        return $"{what}, leaving {showing} of {total} showing";
    }

    private static string Summarize(List<string> names, string noun)
    {
        if (names.Count == 1) return names[0];
        if (names.Count <= 3) return string.Join(", ", names);
        return $"{names.Count} {DataSource.Plural(noun)}";
    }

    // A company or metric by the name the user says, or by its 1-based place
    // among those on the sheet. Hidden ones answer to their names: they are what
    // the tool exists to bring back. Numbers count only what is showing, the way
    // DescribeSheet and every other tool count them, so a hidden one has none.
    public bool TryResolve(bool rows, string query, out int id)
    {
        id = -1;

        DataSource data = Data;
        if (data == null || !data.IsLoaded || string.IsNullOrWhiteSpace(query)) return false;

        string wanted = query.Trim();
        List<int> all = data.FilterLinesInOrder(rows);

        for (int i = 0; i < all.Count; i++)
            if (string.Equals(DataSource.FilterLabel(data, rows, all[i]), wanted, System.StringComparison.OrdinalIgnoreCase))
            {
                id = all[i];
                return true;
            }

        // A bare number is a place on the sheet, never part of a name: '3'
        // means the third one showing, not whichever title has a 3 in it.
        if (int.TryParse(wanted, out int position))
        {
            if (position < 1) return false;
            for (int i = 0; i < all.Count; i++)
            {
                if (data.IsHidden(rows, all[i])) continue;
                if (--position == 0) { id = all[i]; return true; }
            }
            return false;
        }

        int only = -1, hits = 0;
        for (int i = 0; i < all.Count; i++)
        {
            if (DataSource.FilterLabel(data, rows, all[i]).IndexOf(wanted, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
            only = all[i];
            hits++;
        }
        if (hits != 1) return false;
        id = only;
        return true;
    }

    public List<string> Names(bool rows)
    {
        DataSource data = Data;
        var names = new List<string>();
        if (data == null || !data.IsLoaded) return names;

        foreach (int id in data.FilterLinesInOrder(rows)) names.Add(DataSource.FilterLabel(data, rows, id));
        return names;
    }
}
