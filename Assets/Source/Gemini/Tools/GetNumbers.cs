using System.Collections.Generic;
using Google.GenAI.Types;

public sealed class GetNumbers : AgenticTool<GetNumbers.Args> {

    private const int MaxCells = 100;

    public class Args {
        [Doc("The row to read: its name, or its 1-based position. Give it with 'column' for one cell, or alone to read across the row."), Optional]
        public string row;
        [Doc("The column to read: its name, or its 1-based position. Give it with 'row' for one cell, or alone to read down the column."), Optional]
        public string column;
    }

    public override FunctionDeclaration Declaration => new FunctionDeclaration {
        Name = "GetNumbers",
        Description = "Read the figures behind the bars, in dollars (stock price in dollars per share). " +
                      "Give 'row' and 'column' for one cell, 'row' alone to read across it, 'column' alone to read " +
                      "down it, or neither for the whole sheet; over 100 cells is refused, so read a large sheet a " +
                      "line at a time. Lines take a name or a 1-based position among those showing, and come back " +
                      "in display order. Values are exact, so round when you say one aloud.",
        Parameters = ParametersFor(typeof(Args))
    };

    protected override void Run(Args args, Dictionary<string, object> result) {
        var data = Scene.Data;
        var mgr = Scene.Sheets;
        if (data == null || mgr == null) { result["error"] = "No sheet in scene."; return; }

        if (!TryResolveTargetBounds(result, out int rowMin, out int rowMax, out int colMin, out int colMax))
            return;

        bool hasRow = !string.IsNullOrWhiteSpace(args.row);
        bool hasColumn = !string.IsNullOrWhiteSpace(args.column);

        int visRow = -1;
        int colLo = colMin, colHi = colMax;
        if (hasRow && !TryResolveLine(args.row, false, rowMin, rowMax, result, out visRow)) return;
        if (hasColumn) {
            if (!TryResolveLine(args.column, true, colMin, colMax, result, out int block)) return;
            BlockSpan(true, block, colMin, colMax, out colLo, out colHi);
        }

        bool wholeRows = rowMin == 0 && rowMax == mgr.RowCount - 1;
        bool wholeColumns = colMin == 0 && colMax == mgr.ColCount - 1;

        if (hasRow && hasColumn) {
            if (colLo == colHi) Cell(data, visRow, colLo, result);
            else Block(data, visRow, visRow, colLo, colHi, result);
        }
        else if (hasRow) {
            Line(data, false, visRow, colMin, colMax, result);
            if (wholeColumns) NoteRefreshed(true);
        }
        else if (hasColumn) {
            if (colLo == colHi) Line(data, true, colLo, rowMin, rowMax, result);
            else Block(data, rowMin, rowMax, colLo, colHi, result);
            if (wholeRows) NoteRefreshed(false);
        }
        else {
            int cells = (rowMax - rowMin + 1) * (colMax - colMin + 1);
            if (cells > MaxCells) {
                result["error"] = $"That block has {cells} cells, more than the {MaxCells} this tool returns " +
                                  "at once; read it a row or column at a time, or use GetStatistics for totals " +
                                  "and averages.";
                return;
            }
            Block(data, rowMin, rowMax, colMin, colMax, result);
            if (wholeRows) NoteRefreshed(false);
            if (wholeColumns) NoteRefreshed(true);
        }
    }

    private static void Cell(DataSource data, int visRow, int visCol, Dictionary<string, object> result) {
        result["row"] = data.TitleAt(false, visRow);
        result["column"] = data.TitleAt(true, visCol);

        if (TryCellValue(data, visRow, visCol, out double value)) result["value"] = Round(value);
        else {
            result["value"] = null;
            result["note"] = "That cell has no value.";
        }
    }

    private static void Line(DataSource data, bool downColumn, int line, int min, int max,
        Dictionary<string, object> result) {
        result[downColumn ? "column" : "row"] = data.TitleAt(downColumn, line);

        var titles = new List<object>();
        var values = new List<object>();

        for (int v = min; v <= max; v++) {
            int visRow = downColumn ? v : line;
            int visCol = downColumn ? line : v;
            titles.Add(data.TitleAt(!downColumn, v));
            values.Add(TryCellValue(data, visRow, visCol, out double value) ? Round(value) : null);
        }

        result[downColumn ? "rows" : "columns"] = titles;
        result["values"] = values;
    }

    private static void Block(DataSource data, int rowMin, int rowMax, int colMin, int colMax,
        Dictionary<string, object> result) {
        var rowTitles = new List<object>();
        var grid = new List<object>();

        for (int vr = rowMin; vr <= rowMax; vr++) {
            rowTitles.Add(data.TitleAt(false, vr));

            var line = new List<object>();
            for (int vc = colMin; vc <= colMax; vc++)
                line.Add(TryCellValue(data, vr, vc, out double value) ? Round(value) : null);
            grid.Add(line);
        }

        var columnTitles = new List<object>();
        for (int vc = colMin; vc <= colMax; vc++) columnTitles.Add(data.TitleAt(true, vc));

        result["rows"] = rowTitles;
        result["columns"] = columnTitles;
        result["values"] = grid;
        result["note"] = "'values' is one list per row, in the same order as 'rows', each aligned to 'columns'.";
    }
}
