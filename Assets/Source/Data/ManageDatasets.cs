using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

public class ManageDatasets : MonoBehaviour
{
    public static ManageDatasets Instance { get; private set; }
    public static DataSource ActiveSource => Instance != null ? Instance.Active : null;

    public ManageSheets sheetManager;
    public ManageTools toolManager;

    public event Action<int> OnActiveDatasetChanged;

    public class Dataset
    {
        public string label;
        public string payload;

        // Listed from the database's catalogue of sheets rather than scanned in:
        // it stands in the rail from startup and is read the first time someone
        // opens it, so a session begins with every sheet named and none of them
        // read. The description says what it would show once read.
        public bool catalogued;
        public string description;

        public DataSource source;
        public bool loaded;

        // A parse is in flight exactly while a reader exists that has not
        // answered yet; the result handler and Unload both clear the reader.
        public bool loading => source != null && !loaded;

        public readonly EditList Edits = new EditList();
    }

    // Which dataset each reader in flight is for.
    private readonly Dictionary<Parser, Dataset> _reads = new Dictionary<Parser, Dataset>();

    private readonly List<Dataset> _datasets = new List<Dataset>();
    private int _active = -1;

    // The dataset most recently asked for that was still being read. Reads
    // finish in any order, so only this one is switched to when its read lands;
    // an earlier request that finishes later (the startup sheet, say) is kept
    // but left in the background.
    private Dataset _requested;
    private int _datasetsCreated;
    private static readonly EditList Unowned = new EditList();

    public IReadOnlyList<Dataset> Datasets => _datasets;
    public int ActiveIndex => _active;
    public int DatasetCount => _datasets.Count;
    public DataSource Active => (_active >= 0 && _active < _datasets.Count) ? _datasets[_active].source : null;
    public Dataset ActiveDataset => (_active >= 0 && _active < _datasets.Count) ? _datasets[_active] : null;

    public static EditList ActiveEdits
    {
        get
        {
            Dataset d = Instance != null ? Instance.ActiveDataset : null;
            return d != null ? d.Edits : Unowned;
        }
    }

    private void Awake()
    {
        if (Instance == null) Instance = this;
        ResolveRefs();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void ResolveRefs()
    {
        if (sheetManager == null) sheetManager = FindAnyObjectByType<ManageSheets>();
        if (toolManager == null) toolManager = FindAnyObjectByType<ManageTools>();
    }

    public void AddDataset(string payload, string label = null)
    {
        if (string.IsNullOrEmpty(payload)) return;

        int open = IndexOf(payload);
        if (open >= 0)
        {
            bool alreadyActive = open == _active;
            string openLabel = _datasets[open].label;
            SwitchDataset(open);
            Notices.Show(this, "Already Open",
                alreadyActive ? $"{openLabel} is already open." : $"Switched to {openLabel}.");
            return;
        }

        Dataset dataset = new Dataset
        {
            payload = payload,
            label = label ?? Stylize(DeriveLabel(payload, _datasetsCreated))
        };
        _datasets.Add(dataset);
        SwitchDataset(_datasets.Count - 1);
    }

    // The dataset with this payload, or -1 when none has it.
    public int IndexOf(string payload) => _datasets.FindIndex(d => d.payload == payload);

    // Datasets the app knows of but has not read. They are listed straight away
    // and cost nothing until they are opened.
    public void AddCatalogEntries(IEnumerable<(string file, string label, string description)> entries)
    {
        if (entries == null) return;

        foreach ((string file, string label, string description) in entries)
        {
            if (string.IsNullOrEmpty(file) || IndexOf(file) >= 0) continue;

            _datasets.Add(new Dataset
            {
                payload = file,
                label = string.IsNullOrEmpty(label) ? Stylize(DeriveLabel(file, _datasets.Count)) : label,
                catalogued = true,
                description = description
            });
        }
    }

    private readonly Dictionary<Dataset, TaskCompletionSource<bool>> _awaitingLoad =
        new Dictionary<Dataset, TaskCompletionSource<bool>>();

    // Reads a listed dataset if it has not been read, and answers when it is
    // ready either way. The assistant awaits this so that it never reports
    // switching to a sheet the app is still reading, then switches itself;
    // reading alone asks for nothing to be shown.
    public Task<bool> EnsureLoaded(int index)
    {
        if (index < 0 || index >= _datasets.Count) return Task.FromResult(false);

        Dataset dataset = _datasets[index];
        if (dataset.loaded) return Task.FromResult(true);

        if (!_awaitingLoad.TryGetValue(dataset, out TaskCompletionSource<bool> waiting))
        {
            waiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _awaitingLoad[dataset] = waiting;
        }

        BeginLoad(dataset);
        return waiting.Task;
    }

    // Whether anyone was waiting on it.
    private bool SettleLoad(Dataset dataset, bool ok)
    {
        if (!_awaitingLoad.TryGetValue(dataset, out TaskCompletionSource<bool> waiting)) return false;
        _awaitingLoad.Remove(dataset);
        waiting.SetResult(ok);
        return true;
    }

    // Reads a listed dataset ahead of anyone asking for it, so switching to it
    // later shows it at once. Nobody is told it was read, or that it could not
    // be: a failed read is tried again when it is asked for.
    public void Preload(int index)
    {
        if (index >= 0 && index < _datasets.Count) BeginLoad(_datasets[index]);
    }

    private void BeginLoad(Dataset dataset)
    {
        if (dataset == null || dataset.loading || dataset.loaded) return;

        GameObject host = new GameObject($"Dataset_{_datasetsCreated++}");
        host.transform.SetParent(transform, false);

        Parser reader = host.AddComponent<Parser>();
        dataset.source = reader;
        _reads[reader] = dataset;

        reader.onLoadResult = (ok, reason) => OnDatasetLoadResult(reader, ok, reason);
        reader.Load(dataset.payload);
    }

    private void OnDatasetLoadResult(Parser reader, bool ok, string reason)
    {
        if (!_reads.TryGetValue(reader, out Dataset dataset)) return;
        _reads.Remove(reader);

        int index = _datasets.IndexOf(dataset);
        if (index < 0) return;

        bool awaited = SettleLoad(dataset, ok);

        bool requested = dataset == _requested;
        if (requested) _requested = null;
        bool preloaded = !requested && !awaited;

        if (!ok)
        {
            if (preloaded && dataset.catalogued) { Unload(dataset); return; }

            // A listed sheet stays listed when it will not read: it is still
            // one of the regions, and trying again is a slide away.
            if (dataset.catalogued)
            {
                Unload(dataset);
                Notices.Show(this, "Sheet Unavailable",
                    reason ?? $"{dataset.label} could not be read.");
                return;
            }

            RemoveDataset(index);
            Notices.Show(this, "Not Read",
                reason ?? "That dataset could not be read.");
            return;
        }

        dataset.loaded = true;

        if (!preloaded) StateChannel.Record("Dataset", $"loaded a new dataset, {dataset.label}");
        if (requested) Activate(index);
    }

    public void RemoveDataset(int index)
    {
        if (index < 0 || index >= _datasets.Count) return;

        Dataset dataset = _datasets[index];
        bool wasActive = index == _active;
        if (dataset == _requested) _requested = null;

        if (wasActive && sheetManager != null) sheetManager.CommitPendingGrabs();

        _datasets.RemoveAt(index);
        if (_active > index) _active--;
        else if (wasActive) _active = -1;

        if (dataset.source is Parser reader) _reads.Remove(reader);
        if (dataset.source != null) Destroy(dataset.source.gameObject);
        SettleLoad(dataset, false);

        if (wasActive)
        {
            if (_datasets.Count > 0) SwitchDataset(_datasets.Count - 1);
            else
            {
                if (sheetManager != null) sheetManager.SetDataSource(null);
            }
        }

        if (dataset.loaded)
            StateChannel.Record("Dataset", $"closed the dataset {dataset.label}");
        if (wasActive && _datasets.Count == 0) OnActiveDatasetChanged?.Invoke(-1);
    }


    public void SwitchDataset(int index)
    {
        if (index < 0 || index >= _datasets.Count) return;

        // Any switch supersedes a read still pending from an earlier request,
        // including asking for the dataset already open.
        _requested = null;
        if (index == _active && _datasets[index].loaded) return;

        // A listed sheet is read here, the first time it is asked for, or again
        // when its last read failed. The parse finishes on a later frame and
        // lands back in OnDatasetLoadResult, which switches to it then, unless
        // something else was asked for since.
        if (!_datasets[index].loaded)
        {
            Request(_datasets[index]);
            return;
        }

        Activate(index);
    }

    // Shows a read dataset on the table.
    private void Activate(int index)
    {
        SettleHands();

        _active = index;
        Dataset next = _datasets[index];

        Rebind(next);
        if (sheetManager != null) sheetManager.PlaySwitchGrow();
        OnActiveDatasetChanged?.Invoke(index);

        StateChannel.RecordState("dataset",
            $"the {(string.IsNullOrEmpty(next.label) ? "dataset" : next.label)} dataset is open" +
            "; row and column numbers and edits all belong to it");

        ReportAxes(next.source);
    }

    private static void ReportAxes(DataSource source)
    {
        if (source == null) return;

        string columns = AxisSketch(source, true);
        string rows = AxisSketch(source, false);
        if (columns == null || rows == null) return;

        StateChannel.SetState("axes", $"the columns are {columns}; the rows are {rows}");
    }

    private static string AxisSketch(DataSource source, bool columns)
    {
        IReadOnlyList<int> order = columns ? source.ColumnOrder : source.RowOrder;
        if (order == null || order.Count == 0) return null;

        var shown = new List<string>(2);
        for (int i = 0; i < order.Count && shown.Count < 2; i++)
        {
            string title = source.TitleAt(columns, i);
            if (!string.IsNullOrEmpty(title)) shown.Add(title);
        }
        if (shown.Count == 0) return null;

        int rest = order.Count - shown.Count;
        string list = string.Join(", ", shown);
        return rest > 0 ? $"{list} and {rest} more" : list;
    }

    // Reads a dataset that is to be shown once it lands.
    private void Request(Dataset dataset)
    {
        _requested = dataset;
        BeginLoad(dataset);
    }

    // Finishes any move in hand and puts the tool down, before what the table
    // shows changes under them.
    private void SettleHands()
    {
        if (sheetManager != null) sheetManager.CommitPendingGrabs();
        if (toolManager != null) toolManager.DeselectTool();
    }

    // Gives back everything the parse held, and leaves the entry listed.
    private void Unload(Dataset dataset)
    {
        dataset.loaded = false;
        if (dataset.source != null) Destroy(dataset.source.gameObject);
        dataset.source = null;
    }

    private void Rebind(Dataset dataset)
    {
        TryStep("sheetManager", () => { if (sheetManager != null) sheetManager.SetDataSource(dataset.source); });
        TryStep("replay", () => { if (sheetManager != null) sheetManager.ReplayEdits(dataset.Edits); });
    }

    private void TryStep(string label, Action step)
    {
        try { step(); }
        catch (Exception e) { Debug.LogError($"[ManageDatasets] Rebind step '{label}' failed: {e}"); }
    }

    private static string Stylize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;

        string[] words = raw.Split(new[] { '_', '-', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return raw;

        for (int i = 0; i < words.Length; i++)
        {
            if (HasInnerUppercase(words[i])) continue;
            string w = words[i].ToLowerInvariant();
            bool edge = i == 0 || i == words.Length - 1;
            words[i] = (!edge && SmallWords.Contains(w))
                ? w
                : char.ToUpperInvariant(w[0]) + w.Substring(1);
        }
        return string.Join(" ", words);
    }

    private static readonly HashSet<string> SmallWords = new HashSet<string>
    {
        "a", "an", "and", "as", "at", "but", "by", "for", "if", "in", "nor", "of",
        "on", "or", "per", "so", "the", "to", "up", "via", "vs", "yet"
    };

    private static bool HasInnerUppercase(string word)
    {
        for (int i = 1; i < word.Length; i++)
            if (char.IsUpper(word[i])) return true;
        return false;
    }

    private static string DeriveLabel(string payload, int ordinal)
    {
        string fallback = $"Dataset {ordinal + 1}";
        if (string.IsNullOrEmpty(payload)) return fallback;

        if (payload.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return DeriveDataUriName(payload) ?? fallback;

        if (payload.IndexOf('\n') >= 0)
            return fallback;

        string trimmed = payload.Trim();
        int query = trimmed.IndexOf('?');
        if (query >= 0) trimmed = trimmed.Substring(0, query);
        trimmed = trimmed.TrimEnd('/');

        int slash = trimmed.LastIndexOf('/');
        string name = slash >= 0 ? trimmed.Substring(slash + 1) : trimmed;

        int dot = name.LastIndexOf('.');
        if (dot > 0) name = name.Substring(0, dot);

        return string.IsNullOrEmpty(name) ? fallback : name;
    }

    private static string DeriveDataUriName(string payload)
    {
        int comma = payload.IndexOf(',');
        string header = comma >= 0 ? payload.Substring(0, comma) : payload;

        int nameIdx = header.IndexOf("name=", StringComparison.OrdinalIgnoreCase);
        if (nameIdx < 0) return null;

        string name = header.Substring(nameIdx + 5);
        int semi = name.IndexOf(';');
        if (semi >= 0) name = name.Substring(0, semi);
        name = name.Trim();

        int dot = name.LastIndexOf('.');
        if (dot > 0) name = name.Substring(0, dot);

        return string.IsNullOrEmpty(name) ? null : name;
    }
}
