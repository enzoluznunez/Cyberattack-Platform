using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

// Breaches named by what they are rather than by their rows ('every ransomware
// breach with revenue over 10 million'), looked up in the database, and the
// open sheet's rows that are those breaches. A row is titled as the database
// labels a breach (ticker and disclosure day), so the label finds the row.
// What the sheet tools' 'where' acts on.
public sealed class BreachMatch {

    // How many breaches match in the whole database, on this sheet or not.
    public int Total { get; private set; }

    private readonly HashSet<string> _labels = new HashSet<string>();

    // Off the main thread: the lookup is a request to the database.
    public static async Task<(BreachMatch match, string error)> Look(BreachFilters where) {
        string bad = where.Check();
        if (bad != null) return (null, bad);

        return await CyberApi.Read("BreachMatch", "/match?" + where.QueryAlone(), root => {
            var match = new BreachMatch { Total = root.GetProperty("total").GetInt32() };
            foreach (JsonElement label in root.GetProperty("labels").EnumerateArray())
                match._labels.Add(label.GetString());
            return match;
        }).ConfigureAwait(false);
    }

    // The data rows of 'data' that are matching breaches, in the order they
    // stand, hidden ones included.
    public List<int> DataRows(DataSource data) {
        var rows = new List<int>();
        if (data == null) return rows;
        foreach (int row in data.FilterLinesInOrder(rows: true))
            if (_labels.Contains(DataSource.RowLabelOfData(data, row))) rows.Add(row);
        return rows;
    }

    // DataRows, with Report's account of them put on the result.
    public List<int> RowsOn(DataSource data, Dictionary<string, object> result) {
        List<int> rows = DataRows(data);
        Report(data, rows, result);
        return rows;
    }

    // Says how many matched, how many of those are this sheet's rows, and how
    // many of those a filter has off the sheet, so the model can tell the user
    // where the rest are.
    public void Report(DataSource data, List<int> rows, Dictionary<string, object> result) {
        int hidden = 0;
        foreach (int row in rows) if (data.IsHidden(true, row)) hidden++;
        result["matchedInDatabase"] = Total;
        result["matchedOnSheet"] = rows.Count;
        if (hidden > 0) result["matchedButFilteredOff"] = hidden;
        if (rows.Count < Total)
            result["matchNote"] = $"{Total - rows.Count} of the {Total} matching breaches are not on this sheet, " +
                                  "which holds only its own place's breaches; the United States sheet holds every one.";
    }
}
