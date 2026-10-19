using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Unity.Profiling;
using UnityEngine;

public class Parser : DataSource
{
    public Action<bool, string> onLoadResult;

    private void ReportResult(bool ok, string reason) => onLoadResult?.Invoke(ok, reason);

    private enum SourceKind { StreamingAsset, Url, Inline }

    public void Load(string source)
    {
        switch (Classify(source))
        {
            case SourceKind.Url:    StartCoroutine(LoadFromWebRequest(source)); break;
            case SourceKind.Inline: LoadFromCsvText(StripDataUri(source)); break;
            default:                StartCoroutine(LoadFromStreamingAssets(source)); break;
        }
    }

    private static SourceKind Classify(string source)
    {
        if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return SourceKind.Url;
        if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            source.IndexOf('\n') >= 0)
            return SourceKind.Inline;
        return SourceKind.StreamingAsset;
    }

    private static string StripDataUri(string source)
    {
        if (!source.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return source;
        int comma = source.IndexOf(',');
        return comma >= 0 ? source.Substring(comma + 1) : source;
    }

    private System.Collections.IEnumerator LoadFromStreamingAssets(string fileName)
    {
        return LoadFromWebRequest(CyberApi.StreamingAssetUrl(fileName));
    }

    private System.Collections.IEnumerator LoadFromWebRequest(string url)
    {
        using (UnityEngine.Networking.UnityWebRequest www = UnityEngine.Networking.UnityWebRequest.Get(url))
        {
            // A sheet from the API carries its key; a file or any other URL does not.
            CyberApi.Authorize(www);
            yield return www.SendWebRequest();

            if (www.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[Parser:{name}] Failed to load CSV from {url}: {www.error}");
                Fail("That dataset could not be reached.");
                yield break;
            }

            LoadFromCsvText(www.downloadHandler.text);
        }
    }

    // Which read is current: a read still under way when another starts, or
    // when the sheet is let go, is dropped when it lands.
    private int _readTicket;

    private static readonly ProfilerMarker ApplyMarker = new ProfilerMarker("Parser.Apply");

    // The text is split and its figures read on a worker, so a large sheet
    // never holds up a frame; what is read is taken in here on the main
    // thread, on a later frame.
    public void LoadFromCsvText(string csvText)
    {
        ClearGrid();
        _rawText = csvText;
        int ticket = ++_readTicket;

        if (LooksLikeHtml(csvText))
        {
            Fail("That link returned a web page, not a dataset.");
            return;
        }

        string label = name;
        Task.Run(() => Table.Read(csvText, label)).ContinueWith(read => MainThread.Run(() =>
        {
            if (this == null || ticket != _readTicket) return;
            if (read.Status != TaskStatus.RanToCompletion)
            {
                Debug.LogException(read.Exception?.GetBaseException() ?? new Exception("Read cancelled."));
                Fail("That dataset could not be read.");
                return;
            }
            Apply(read.Result);
        }));
    }

    private void Apply(Table table)
    {
        using (ApplyMarker.Auto())
        {
            if (table.failure != null)
            {
                Fail(table.failure);
                return;
            }

            List<string> header = table.header;
            if (header.Count > 0) SetAxisTitles(header[0]);
            for (int c = 1; c <= table.columns; c++)
                _columnTitles.Add(header[c].Trim());
            SetColumnGroupSize(table.groupSize);

            int rowCount = table.rows.Count;
            int colCount = table.columns;
            for (int r = 0; r < rowCount; r++)
            {
                List<string> fields = table.rows[r];
                _rowTitles.Add(fields.Count > 0 ? fields[0].Trim() : "");
            }

            SetRowCategories(table.directives, table.sourceRows, table.sourceRow);

            FillGrid(rowCount, colCount, (r, c) => table.values[r, c]);
            if (table.heights != null)
                FillHeights(rowCount, colCount, (r, c) => table.heights[r, c]);

            Debug.Log($"[Parser:{name}] Loaded grid {rowCount} rows x {colCount} cols " +
                      $"({ColumnGroupSize} col(s) per group, normalised range [{_globalMin}, {_globalMax}])");
            RaiseDataLoaded();
            ReportResult(true, null);
        }
    }

    // What a sheet's text says, read without touching Unity, so it can be read
    // on any thread. A sheet that will not do says why in 'failure'.
    private sealed class Table
    {
        public string failure;
        public Directives directives;
        public List<string> header;
        public int columns;
        public int groupSize;
        public List<List<string>> rows;
        public List<int> sourceRow;
        public int sourceRows;
        public float?[,] values;
        public float?[,] heights;

        public static Table Read(string csvText, string label)
        {
            var table = new Table();
            string[] lines = csvText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            var directives = new Directives();
            table.directives = directives;
            List<List<string>> grid = new List<List<string>>(lines.Length);
            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (grid.Count == 0 && line.TrimStart().StartsWith("#", StringComparison.Ordinal))
                {
                    ReadDirective(line, directives, label);
                    continue;
                }
                grid.Add(ParseCSVLine(line));
            }

            if (grid.Count < 2) return table.Failed("That dataset has no data rows.");

            List<string> header = grid[0];
            table.header = header;

            // '#heights N': the header's last N columns are the heights of the N
            // before them, so only those first N are columns of the sheet.
            int heights = directives.heights;
            if (heights > 0 && header.Count - 1 != heights * 2)
            {
                Debug.LogWarning($"[Parser:{label}] '#heights {heights}' does not halve the " +
                                 $"{header.Count - 1} columns; drawing the cells as their own heights.");
                heights = 0;
            }
            int colCount = heights > 0 ? heights : header.Count - 1;
            table.columns = colCount;

            int groupSize = directives.groupSize;
            if (groupSize > 1 && colCount % groupSize != 0)
            {
                Debug.LogWarning($"[Parser:{label}] '#group {groupSize}' does not divide the " +
                                 $"{colCount} columns; loading them ungrouped.");
                groupSize = 1;
            }
            table.groupSize = groupSize;

            // The '#metro', '#color' and '#city' directives carry one value per data row as
            // the server wrote them, so dropping a short row here has to drop its
            // place in them too. Keeping each survivor's original position is what
            // lets the rest of the sheet keep its colour.
            int sourceRows = grid.Count - 1;
            var rows = new List<List<string>>(sourceRows);
            var sourceRow = new List<int>(sourceRows);
            for (int g = 1; g < grid.Count; g++)
            {
                if (grid[g].Count < header.Count) continue;
                sourceRow.Add(g - 1);
                rows.Add(grid[g]);
            }
            table.rows = rows;
            table.sourceRow = sourceRow;
            table.sourceRows = sourceRows;

            int skipped = sourceRows - rows.Count;
            if (skipped > 0)
                Debug.LogWarning($"[Parser:{label}] Skipped {skipped} row(s) shorter than the {header.Count}-column header.");

            int rowCount = rows.Count;
            if (rowCount == 0 || colCount == 0) return table.Failed("That dataset has no rows or columns.");

            table.values = new float?[rowCount, colCount];
            int numericCells = 0;
            for (int r = 0; r < rowCount; r++)
                for (int c = 0; c < colCount; c++)
                {
                    float? v = FieldAt(rows[r], c + 1);
                    table.values[r, c] = v;
                    if (v.HasValue) numericCells++;
                }

            if (heights > 0)
            {
                table.heights = new float?[rowCount, colCount];
                for (int r = 0; r < rowCount; r++)
                    for (int c = 0; c < colCount; c++)
                        table.heights[r, c] = FieldAt(rows[r], c + 1 + heights);
            }

            if (numericCells == 0) return table.Failed("That dataset has no numeric values.");
            return table;
        }

        private Table Failed(string reason)
        {
            failure = reason;
            return this;
        }

        private static float? FieldAt(List<string> fields, int at) =>
            at < fields.Count && TryParseFloat(fields[at], out float v) ? v : (float?)null;
    }

    private void Fail(string reason)
    {
        _readTicket++;
        Debug.LogWarning($"[Parser:{name}] {reason}");
        ClearGrid();
        RaiseLoadFailed();
        ReportResult(false, reason);
    }

    private static bool LooksLikeHtml(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        int i = 0;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        return i < text.Length && text[i] == '<';
    }

    private sealed class Directives
    {
        public int groupSize = 1;
        public int heights;
        public readonly List<string> categories = new List<string>();
        public readonly List<string> colors = new List<string>();
        public readonly List<string> cities = new List<string>();
    }

    // Directive lines sit above the header and configure the grid. '#group N'
    // says how many columns belong to one metric; '#heights N' that the last N
    // columns are the heights of the N before them; '#metro', '#color' and
    // '#city' carry one value per data row, in row order. Anything else is
    // ignored, so a file may carry comments without becoming a row.
    private static void ReadDirective(string line, Directives into, string label)
    {
        string body = line.TrimStart().TrimStart('#').Trim();

        foreach ((string name, List<string> list) in new[]
                 { ("metro", into.categories), ("color", into.colors), ("city", into.cities) })
        {
            if (!TryBody(body, name, out string listed)) continue;
            list.Clear();
            list.AddRange(ParseCSVLine(listed));
            return;
        }

        if (TryBody(body, "heights", out string count))
        {
            if (int.TryParse(count, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0)
                into.heights = n;
            else Debug.LogWarning($"[Parser:{label}] Ignored unreadable directive '{line.Trim()}'.");
            return;
        }

        if (!TryBody(body, "group", out string value)) return;

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int size) && size > 1)
        {
            into.groupSize = size;
            return;
        }
        Debug.LogWarning($"[Parser:{label}] Ignored unreadable directive '{line.Trim()}'.");
    }

    private static bool TryBody(string body, string name, out string rest)
    {
        rest = null;
        if (!body.StartsWith(name, StringComparison.OrdinalIgnoreCase)) return false;
        // The whole word, so a comment such as '# colors follow...' stays a comment.
        if (body.Length > name.Length && !char.IsWhiteSpace(body[name.Length])) return false;

        rest = body.Substring(name.Length).Trim();
        return true;
    }

    // A row's metro, its colour and its city only mean anything together and
    // only if there is exactly one of each per row (the city is only checked,
    // not kept): a sheet whose directives
    // disagree with its rows is drawn uncoloured rather than coloured by a
    // guess. 'sourceRows' is how many data rows the directives were written
    // for, and 'sourceRow' says where each row that survived stood among them.
    private void SetRowCategories(Directives directives, int sourceRows, List<int> sourceRow)
    {
        _rowCategories.Clear();
        _rowColors.Clear();
        List<string> categories = directives.categories, colors = directives.colors, cities = directives.cities;
        if (categories.Count == 0 && colors.Count == 0) return;

        if (categories.Count != sourceRows || colors.Count != sourceRows ||
            (cities.Count > 0 && cities.Count != sourceRows))
        {
            Debug.LogWarning($"[Parser:{name}] Ignored the row directives: {categories.Count} metros, " +
                             $"{colors.Count} colors and {cities.Count} cities for {sourceRows} rows.");
            return;
        }

        for (int r = 0; r < sourceRow.Count; r++)
        {
            int at = sourceRow[r];
            _rowCategories.Add(categories[at].Trim());
            _rowColors.Add(ColorUtility.TryParseHtmlString(colors[at].Trim(), out Color c) ? c : Color.white);
        }
    }

    private void SetAxisTitles(string corner)
    {
        if (string.IsNullOrWhiteSpace(corner)) return;

        int sep = corner.IndexOf('\\');
        if (sep < 0) sep = corner.IndexOf('/');

        if (sep >= 0)
        {
            _rowAxisTitle = CleanAxisTitle(corner.Substring(0, sep));
            _columnAxisTitle = CleanAxisTitle(corner.Substring(sep + 1));
        }
        else
        {
            _rowAxisTitle = CleanAxisTitle(corner);
        }
    }

    private static string CleanAxisTitle(string value)
    {
        value = value.Trim();
        return value.Length == 0 ? null : value;
    }

    private void ClearGrid()
    {
        _columnTitles.Clear();
        _rowTitles.Clear();
        _rowCategories.Clear();
        _rowColors.Clear();
        _heights = null;
        _columnAxisTitle = null;
        _rowAxisTitle = null;
        _values = new float[0, 0];
        _valid = new bool[0, 0];
        _columnOrder.Clear();
        _rowOrder.Clear();
        _rawText = null;
        _globalMin = 0f;
        _globalMax = 1f;
        SetColumnGroupSize(1);
    }

    public static List<string> ParseCSVLine(string line)
    {
        var fields = new List<string>();
        bool inQuotes = false;
        var sb = new System.Text.StringBuilder(64);

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',')
                {
                    fields.Add(sb.ToString());
                    sb.Clear();
                }
                else sb.Append(c);
            }
        }
        fields.Add(sb.ToString());
        return fields;
    }

    private static bool TryParseFloat(string value, out float result)
    {
        result = 0f;
        return !string.IsNullOrEmpty(value) &&
               float.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out result);
    }
}
