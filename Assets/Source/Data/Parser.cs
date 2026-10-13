using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
        string path = Path.Combine(Application.streamingAssetsPath, fileName);
        string url = path.Contains("://") ? path : "file://" + path;
        return LoadFromWebRequest(url);
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

    public void LoadFromCsvText(string csvText)
    {
        ClearGrid();
        _rawText = csvText;

        if (LooksLikeHtml(csvText))
        {
            Fail("That link returned a web page, not a dataset.");
            return;
        }

        string[] lines = csvText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

        var directives = new Directives();
        List<List<string>> grid = new List<List<string>>(lines.Length);
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (grid.Count == 0 && line.TrimStart().StartsWith("#", StringComparison.Ordinal))
            {
                ReadDirective(line, directives);
                continue;
            }
            grid.Add(ParseCSVLine(line));
        }
        int groupSize = directives.groupSize;

        if (grid.Count < 2)
        {
            Fail("That dataset has no data rows.");
            return;
        }

        List<string> header = grid[0];
        if (header.Count > 0) SetAxisTitles(header[0]);

        // '#heights N': the header's last N columns are the heights of the N
        // before them, so only those first N are columns of the sheet.
        int heights = directives.heights;
        if (heights > 0 && header.Count - 1 != heights * 2)
        {
            Debug.LogWarning($"[Parser:{name}] '#heights {heights}' does not halve the " +
                             $"{header.Count - 1} columns; drawing the cells as their own heights.");
            heights = 0;
        }
        int titled = heights > 0 ? heights : header.Count - 1;
        for (int c = 1; c <= titled; c++)
            _columnTitles.Add(header[c].Trim());

        if (groupSize > 1 && _columnTitles.Count % groupSize != 0)
        {
            Debug.LogWarning($"[Parser:{name}] '#group {groupSize}' does not divide the " +
                             $"{_columnTitles.Count} columns; loading them ungrouped.");
            groupSize = 1;
        }
        SetColumnGroupSize(groupSize);

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

        int skipped = sourceRows - rows.Count;
        if (skipped > 0)
            Debug.LogWarning($"[Parser:{name}] Skipped {skipped} row(s) shorter than the {header.Count}-column header.");

        int rowCount = rows.Count;
        int colCount = _columnTitles.Count;

        if (rowCount == 0 || colCount == 0)
        {
            Fail("That dataset has no rows or columns.");
            return;
        }

        for (int r = 0; r < rowCount; r++)
        {
            List<string> fields = rows[r];
            _rowTitles.Add(fields.Count > 0 ? fields[0].Trim() : "");
        }

        SetRowCategories(directives, sourceRows, sourceRow);

        int numericCells = FillGrid(rowCount, colCount, (r, c) =>
        {
            List<string> fields = rows[r];
            string field = (c + 1 < fields.Count) ? fields[c + 1] : null;
            return TryParseFloat(field, out float v) ? v : (float?)null;
        });

        if (heights > 0)
            FillHeights(rowCount, colCount, (r, c) =>
            {
                List<string> fields = rows[r];
                int at = c + 1 + heights;
                string field = at < fields.Count ? fields[at] : null;
                return TryParseFloat(field, out float v) ? v : (float?)null;
            });

        if (numericCells == 0)
        {
            Fail("That dataset has no numeric values.");
            return;
        }

        EnsureOrders();

        Debug.Log($"[Parser:{name}] Loaded grid {rowCount} rows x {colCount} cols " +
                  $"({ColumnGroupSize} col(s) per group, normalised range [{_globalMin}, {_globalMax}])");
        RaiseDataLoaded();
        ReportResult(true, null);
    }

    private void Fail(string reason)
    {
        Debug.LogWarning($"[Parser:{name}] {reason}");
        ClearGrid();
        EnsureOrders();
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
    private void ReadDirective(string line, Directives into)
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
            else Debug.LogWarning($"[Parser:{name}] Ignored unreadable directive '{line.Trim()}'.");
            return;
        }

        if (!TryBody(body, "group", out string value)) return;

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int size) && size > 1)
        {
            into.groupSize = size;
            return;
        }
        Debug.LogWarning($"[Parser:{name}] Ignored unreadable directive '{line.Trim()}'.");
    }

    private static bool TryBody(string body, string name, out string rest)
    {
        rest = null;
        if (!body.StartsWith(name, StringComparison.OrdinalIgnoreCase)) return false;

        rest = body.Substring(name.Length).Trim();
        return true;
    }

    // A row's metro, its colour and its city only mean anything together and
    // only if there is exactly one of each per row: a sheet whose directives
    // disagree with its rows is drawn uncoloured rather than coloured by a
    // guess. 'sourceRows' is how many data rows the directives were written
    // for, and 'sourceRow' says where each row that survived stood among them.
    private void SetRowCategories(Directives directives, int sourceRows, List<int> sourceRow)
    {
        _rowCategories.Clear();
        _rowColors.Clear();
        _rowCities.Clear();
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
            if (cities.Count > 0) _rowCities.Add(cities[at].Trim());
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
        _rowCities.Clear();
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
