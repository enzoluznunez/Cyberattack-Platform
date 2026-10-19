using UnityEngine;

public class ProfileTool : Tool
{
    public float liftAboveMaximumHeight = 0f;

    [Tooltip("How far a pressed finger must travel, in cells, before the sweep direction chooses the axis.")]
    public float travelCells = 0.6f;

    private readonly AxisIntent _intent = new AxisIntent();
    private bool _pressed;
    private Vector3 _pressLocal;

    protected override ToolType Kind => ToolType.Profile;

    protected override bool UsesSheetEvents => true;

    protected override void OnActiveChanged(bool active)
    {
        _pressed = false;
        _intent.Reset();
        if (!active) ClearTint();
    }

    private static bool Usable(ReadSheets.Reading reading) =>
        reading.valid && reading.cube != null && reading.sheet != null;

    private void Tint(ReadSheets.Reading reading, float swell) => TintLine(reading, _intent.Columns, swell);

    private void FeedReach(ReadSheets.Reading reading)
    {
        if (reading.tip == Vector3.zero) return;
        if (AxisIntent.ReachScores(reading.sheet, reading.wrist, reading.tip, out float forColumns, out float forRows))
            _intent.Feed(forColumns, forRows);
    }

    private void FeedSweep(ReadSheets.Reading reading)
    {
        if (reading.tip == Vector3.zero) return;

        Vector3 delta = reading.sheet.transform.InverseTransformPoint(reading.tip) - _pressLocal;
        float travel = travelCells * reading.sheet.CellSize;
        if (Mathf.Max(Mathf.Abs(delta.x), Mathf.Abs(delta.z)) < travel) return;

        AxisIntent.SweepScores(delta, out float forColumns, out float forRows);
        _intent.Feed(forColumns, forRows);
        _intent.Latch();
    }

    protected override void OnSheetHover(ReadSheets.Reading reading)
    {
        if (!Active || sheetManager == null || !Usable(reading))
        {
            ClearTint();
            return;
        }

        if (_pressed) FeedSweep(reading);
        else FeedReach(reading);

        if (!_intent.Decided)
        {
            ClearTint();
            return;
        }

        Tint(reading, _pressed ? Style.PreviewSwell + Style.EngageSwell : Style.PreviewSwell);
    }

    protected override void OnSheetSelect(ReadSheets.Reading reading)
    {
        if (!Active || sheetManager == null || !Usable(reading)) return;

        FeedReach(reading);
        _pressed = true;
        _pressLocal = reading.sheet.transform.InverseTransformPoint(
            reading.tip == Vector3.zero ? reading.point : reading.tip);

        if (_intent.Decided) Tint(reading, Style.PreviewSwell + Style.EngageSwell);
    }

    protected override void OnSheetRelease(ReadSheets.Reading reading)
    {
        ClearTint();
        _pressed = false;
        _intent.Release();
    }

    protected override void OnSheetCleared()
    {
        ClearTint();
        _pressed = false;
        _intent.Reset();
    }

    protected override void OnSheetCommit(ReadSheets.Reading reading)
    {
        ClearTint();
        if (!Active || sheetManager == null || !Usable(reading) || !_intent.Decided) return;

        bool columns = _intent.Columns;
        if (!Project(columns, reading.cube.dataRow, reading.cube.dataCol, reading.visRow, reading.visCol)) return;
        UIPressFeedback.Play();
    }

    // Raises a line for the assistant. One already raised is left up, rather
    // than lowered as a press on it would.
    public bool ShowProfile(bool columns, int visRow, int visCol)
    {
        if (!Active || sheetManager == null) return false;

        CreateCube cube = sheetManager.CubeAt(visRow, visCol);
        if (cube == null) return false;

        if (sheetManager.IsProjected(Record(columns, cube.dataRow, cube.dataCol))) return true;
        return Project(columns, cube.dataRow, cube.dataCol, visRow, visCol);
    }

    private ProjectionRecord Record(bool columns, int dataRow, int dataCol) => new ProjectionRecord
    {
        isColumn = columns,
        dataRow = dataRow,
        dataCol = dataCol,
        lift = liftAboveMaximumHeight
    };

    // Raises the line, or lowers it when it is already raised.
    private bool Project(bool columns, int dataRow, int dataCol, int visRow, int visCol)
    {
        if (!sheetManager.PushProjection(Record(columns, dataRow, dataCol), EditKind.Profile, out bool lowered))
            return false;

        DataSource data = Scene.Data;
        int line = columns ? visCol : visRow;
        string label = columns && data != null && data.IsGrouped(true)
            ? DataSource.GroupLabelAt(data, true, data.GroupOf(true, line))
            : DataSource.LabelAt(data, columns, line);
        Report(lowered ? $"lowered {label}" : $"projected {label}");
        return true;
    }
}
