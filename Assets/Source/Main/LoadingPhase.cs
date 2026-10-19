using TMPro;
using UnityEngine;

// Holds the map and sheet back at launch until everything a session can open
// is ready, so nothing is fetched, parsed or made once the user is using it:
// every sheet listed is read; both sheets that take turns, the Profile tool's
// strips, a filter's shrinking copies and the Sort tool's handles have room
// for the largest of them; and every map is drawn. A line ahead of the user says how
// far it has got. Should anything never answer, it gives up waiting after
// a while and the session opens with what there is.
public class LoadingPhase : MonoBehaviour
{
    private const float GiveUpSeconds = 45f;
    private const double RoomBudgetMs = 3.0;
    private const float AheadMetres = 1.2f;
    private const float FollowRate = 4f;
    // A frame longer than this is the app paused, the headset taken off or
    // the system menu up, and counts toward giving up only this much.
    private const float LongestFrame = 0.25f;

    public static bool Done { get; private set; }

    private static LoadingPhase _instance;

    private TextMeshPro _label;
    // Seconds spent loading while the app ran.
    private float _elapsed;
    private bool _measured;
    private ManageSheets.Room _room;
    private int _lines;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        _instance = null;
        Done = false;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Create() => Spawn.Once(ref _instance);

    private void Start()
    {
        _label = new GameObject("LoadingLabel").AddComponent<TextMeshPro>();
        _label.transform.SetParent(transform, false);
        Style.ApplyTitle(_label);
        _label.alignment = TextAlignmentOptions.Center;
        _label.textWrappingMode = TextWrappingModes.NoWrap;
        _label.color = Style.White;
        _label.rectTransform.sizeDelta = new Vector2(40f, 8f);
        _label.transform.localScale = Vector3.one * (Style.WorldTextScale * 2f);
        _label.text = "Loading";
    }

    private void Update()
    {
        if (Done) return;

        _elapsed += Mathf.Min(Time.unscaledDeltaTime, LongestFrame);
        string stage = Stage();
        bool timedOut = _elapsed > GiveUpSeconds;
        if (stage == null || timedOut)
        {
            Done = true;
            _label.renderer.enabled = false;
            float took = _elapsed;
            if (timedOut) Debug.LogWarning($"[LoadingPhase] Gave up waiting after {took:F1} s, at: {stage}");
            else Debug.Log($"[LoadingPhase] Loaded in {took:F1} s.");
            return;
        }

        if (stage != _shownStage)
        {
            _shownStage = stage;
            _label.text = $"Loading\n<size=55%>{stage}</size>";
        }
        FollowHead();
    }

    private string _shownStage;

    // What is still being readied, or null when everything is.
    private string Stage()
    {
        MapCarousel map = MapCarousel.Instance;
        if (map != null && !map.Failed && !map.Ready) return "Drawing the map";

        ManageDatasets datasets = ManageDatasets.Instance;
        if (!ViewCatalog.Settled) return "Listing the sheets";
        if (datasets != null && datasets.PreloadsLeft > 0)
        {
            int read = datasets.PreloadsQueued - datasets.PreloadsLeft;
            return $"Reading sheets {read} of {datasets.PreloadsQueued}";
        }

        if (datasets != null && !_measured) Measure(datasets);
        ManageSheets sheets = Scene.Sheets;
        if (sheets != null && _measured && !sheets.PrepareRoom(_room, RoomBudgetMs)) return "Building the sheets";
        SortTool sort = Scene.Sort;
        if (sort != null && _measured && !sort.Prepare(_lines, RoomBudgetMs)) return "Building the tools";

        if (map != null && map.Ready && !map.AllBuilt) return "Drawing the maps";

        // Last, with everything built: a tiny copy of each kind of thing and
        // of every mesh drawn (GraphicsWarm), so the driver's first-draw work
        // lands here, behind this text, and not as a stall the moment the
        // sheet and the maps show.
        if (!GraphicsWarm.Step()) return "Preparing the graphics";
        return null;
    }

    // The largest dataset read: a sheet has a bar per cell and a title per
    // row, column and column group; a raised row is one row of bars, a raised
    // column every row of one column group; the Sort tool a handle per row
    // and per column group.
    private void Measure(ManageDatasets datasets)
    {
        _measured = true;
        _room = default;
        _lines = 0;
        foreach (ManageDatasets.Dataset d in datasets.Datasets)
        {
            DataSource data = d.loaded ? d.source : null;
            if (data == null) continue;
            int rows = data.RowCount, cols = data.ColumnCount;
            int size = Mathf.Max(data.ColumnGroupSize, 1);
            int groups = size > 1 ? cols / size : 0;
            _room.cubes = Mathf.Max(_room.cubes, rows * cols);
            _room.labels = Mathf.Max(_room.labels, rows + cols + groups);
            _room.stripCubes = Mathf.Max(_room.stripCubes, Mathf.Max(cols, rows * size));
            _room.stripLabels = Mathf.Max(_room.stripLabels, Mathf.Max(1 + cols + groups, rows + size + 1));
            _lines = Mathf.Max(_lines, rows + cols / size);
        }
    }

    // Eases to stand ahead of wherever the user looks.
    private void FollowHead()
    {
        Transform cam = CameraRig.MainTransform;
        if (cam == null) return;
        Vector3 forward = CameraRig.FlatForward;
        Vector3 target = cam.position + forward * AheadMetres;
        Quaternion facing = Quaternion.LookRotation(forward);
        Transform t = _label.transform;
        float k = Style.Damp(FollowRate, Time.unscaledDeltaTime);
        bool first = t.position == Vector3.zero;
        t.SetPositionAndRotation(first ? target : Vector3.Lerp(t.position, target, k),
            first ? facing : Quaternion.Slerp(t.rotation, facing, k));
    }
}
