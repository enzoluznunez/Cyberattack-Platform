using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;

// The maps, one at a time: the United States first, then its six regions
// west to east. A slider under the map has a stop for each; the user pinches
// its thumb and slides it, the maps slide with it, and on letting go they
// settle on the nearest map. Only the map in the middle is seen at rest: the
// others show only while the maps are sliding, the next coming in small from
// the right and the one before from behind the panel.
//
// The maps stand beside the tool panel, to its right in its own plane, as one
// piece with it: they are hung on the panel itself, so they are carried with
// it, frozen in step, and only the user's own slide moves them on it. They
// close when it closes and open again when it opens.
//
// Every map is drawn in the national map's kilometres, and each region's map
// is the national map cut along its borders, drawn as large as fits a box the
// panel's size.
//
// A region's map holds one metro's sheet on the table at a time: when it
// settles it opens its largest metro's, and poking another metro's pin opens
// that one's. The national map shows no sheet; poking one of its regions slides
// to that region's map. When the sheet is switched another way, by the
// assistant, the map slides to its metro's region. A pin and its bars are one
// colour.
public class MapCarousel : MonoBehaviour
{
    public static MapCarousel Instance { get; private set; }

    [Header("Beside the tool panel")]
    [Tooltip("Space between the panel's right edge and the box the map is drawn in, in metres.")]
    public float gap = 0.05f;

    [Header("Scale")]
    [Tooltip("Width and height of the box each map is fitted into, in metres: the panel's own size, " +
             "so a map is never larger than the panel beside it.")]
    public Vector2 mapBox = new Vector2(0.4f, 0.4f);
    [Tooltip("Radius of a metro with one breach, in metres. A pin's area grows with its count.")]
    public float oneBreachRadius = 0.004f;

    [Header("Slide")]
    [Tooltip("Seconds a map takes to slide one place.")]
    public float slideSeconds = 0.35f;
    [Tooltip("Size of a map coming in from either side, against the one in the middle.")]
    [Range(0.1f, 1f)] public float peekScale = 0.4f;

    [Header("Poking a pin or a region")]
    [Tooltip("How near the map's face a fingertip has to come to poke it, in metres.")]
    public float pokeDepth = 0.015f;
    [Tooltip("How far outside a pin a fingertip still pokes it, in metres.")]
    public float pokeSlack = 0.005f;

    // The tool panel's own colours: a white face edged in black over a black
    // back. Only the pins and the national map's regions are coloured.
    public static readonly Color Land = Style.White;
    public static readonly Color Back = Style.Black;
    public static readonly Color Coast = Style.Black;
    public static readonly Color Border = Style.Black;

    public bool Ready => _areas.Count > 0;

    // How much room the maps take at the panel's side, gap included, in
    // metres; the panel opens that much to the left so the two are centred.
    public float SideWidth => gap + mapBox.x;

    private MapAtlas _atlas;
    private BreachMap.Answer _answer;
    private readonly List<MapAtlas.Area> _areas = new List<MapAtlas.Area>();
    private readonly Dictionary<string, Color> _regionColors = new Dictionary<string, Color>();
    private readonly Dictionary<string, RegionMap> _built = new Dictionary<string, RegionMap>();
    private readonly HashSet<string> _shown = new HashSet<string>();

    private RegionMap.Look _look;
    private float _step;

    private Transform _slot;
    private TextMeshPro _title;
    private TextMeshPro _scaleLabel;
    private CreateCube _scaleBar;
    private SlideBar _slider;

    // The metro whose sheet is on the table, while a region's map is up.
    private string _metro;

    // Which fingertips were already touching the map last frame: a poke is a
    // fingertip arriving, not one resting there.
    private readonly HashSet<int> _touching = new HashSet<int>();

    private readonly Vector3[] _corners = new Vector3[4];

    private int _index;
    private float _position;
    private string _settled;
    private bool _sliding;
    private float _slideFrom, _slideTo, _slideStart, _slideSeconds;
    private bool _settlePending;
    // Whether a hand has the slider, so the maps stand where it puts them.
    private bool _held;

    // How far below the map's box the slider and the scale bar stand, in metres.
    private const float SliderDrop = 0.03f;
    private const float ScaleDrop = 0.065f;


    private void Awake()
    {
        if (Instance == null) Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (ManageDatasets.Instance != null) ManageDatasets.Instance.OnActiveDatasetChanged -= OnSheetChanged;
    }

    private async void Start()
    {
        // The outlines need nothing from the API, so they are parsed while
        // the address and key are read and the pins fetched.
        byte[] outlines = MapAtlas.ReadBytes();
        Task<MapAtlas> parse = outlines != null ? Task.Run(() => MapAtlas.Parse(outlines)) : null;

        while (!ViewCatalog.Configured) await Task.Yield();
        if (this == null) return;

        try
        {
            Task<BreachMap.Answer> fetch = BreachMap.Fetch();
            _atlas = parse != null ? await parse : null;
            _answer = await fetch;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[MapCarousel] The map could not be drawn: {e.Message}");
            _answer = null;
        }
        if (this == null) return;

        if (_atlas == null || _answer == null)
        {
            Notices.Show(this, "No Map", _atlas == null
                ? "The map outlines are missing from this build."
                : CyberApi.Unreachable);
            return;
        }

        _areas.AddRange(_atlas.Areas);
        foreach (BreachMap.Region region in _answer.regions) _regionColors[region.region] = region.color;

        Build();

        if (ManageDatasets.Instance != null) ManageDatasets.Instance.OnActiveDatasetChanged += OnSheetChanged;

        _index = 0;
        _position = _index;
        Settle();
    }

    private void Build()
    {
        _step = mapBox.x * 0.75f;

        // The bars' own material, drawn from both sides: a map's outline runs
        // either way round, and its back is seen from behind.
        Material material = new Material(Resources.Load<Material>("Materials/SheetMaterial")) { name = "Map" };
        material.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);

        _look = new RegionMap.Look
        {
            dotRadius = oneBreachRadius,
            coastWidth = 0.0012f,
            borderWidth = 0.0018f,
            land = Land,
            back = Back,
            coast = Coast,
            border = Border,
            material = material
        };

        _slot = new GameObject("Slot").transform;
        _slot.SetParent(transform, false);
        _slot.gameObject.SetActive(false);

        _title = Label(_slot, "Title", Style.TitleFont, 1.6f);
        _title.transform.localPosition = new Vector3(0f, mapBox.y * 0.5f + 0.05f, RegionMap.FaceZ);

        // A stop for each map along the map's width, the thumb a stop wide.
        // The slider runs along its own X, which is east here; turned so its
        // thumb stands out of the map's plane toward the viewer.
        _slider = SlideBar.Create(_slot, "Slider", material);
        _slider.transform.SetLocalPositionAndRotation(new Vector3(0f, -mapBox.y * 0.5f - SliderDrop, 0f),
            Quaternion.Euler(-90f, 0f, 0f));
        _slider.SetSize(mapBox.x, mapBox.x / Mathf.Max(_areas.Count, 1));
        _slider.OnSlide += OnSlide;
        _slider.OnRelease += OnRelease;

        _scaleBar = Bar(_slot, material);
        _scaleLabel = Label(_slot, "Scale", Style.BodyFont, 1f);
        _scaleLabel.alignment = TextAlignmentOptions.Left;
        _scaleLabel.rectTransform.pivot = new Vector2(0f, 0.5f);
    }

    private void LateUpdate()
    {
        if (_slot == null) return;

        // A slide that landed last frame is drawn landed before the sheet
        // switches, so the work of switching it falls on a frame where nothing
        // on the map moves.
        if (_settlePending)
        {
            _settlePending = false;
            Settle();
        }

        bool shown = Anchor();
        if (_slot.gameObject.activeSelf != shown) _slot.gameObject.SetActive(shown);
        if (!shown)
        {
            // Closed with the panel: a slide under way lands at once, so the
            // sheet still follows it, and a slider let go of by closing lands
            // on the map nearest where it was.
            if (_held)
            {
                _held = false;
                _slideTo = Mathf.Clamp(Mathf.RoundToInt(_position), 0, _areas.Count - 1);
                FinishSlide();
            }
            else if (_sliding) FinishSlide();
            _touching.Clear();
            return;
        }

        Slide();
        if (!_sliding && !_held) BuildNext();
        Layout();
        Poke();
    }

    // Makes one map that has not been made yet, nearest the middle first, a
    // frame at a time while the maps rest: the slider can pass every map, so
    // a slide never waits on one being built.
    private void BuildNext()
    {
        for (int k = 1; k < _areas.Count; k++)
        {
            foreach (int side in new[] { k, -k })
            {
                int at = _index + side;
                if (at < 0 || at >= _areas.Count) continue;
                MapAtlas.Area area = _areas[at];
                if (_built.TryGetValue(area.name, out RegionMap built) && built != null) continue;
                MapFor(area);
                return;
            }
        }
    }

    // Beside the tool panel's top part, the part that never changes size, in
    // its plane and facing the way it faces. The maps hang on the panel
    // itself and are placed in its own space, so whatever moves the panel
    // (a hand, the assistant, its turn to face the user) carries them with it
    // in the same frame, and they swell with it while it is held. Shut with
    // it: false while it is closed.
    private bool Anchor()
    {
        ToolPanelUI panel = Scene.ToolPanel;
        RectTransform body = panel != null && panel.IsVisible ? panel.PrimaryRect : null;
        if (body == null) return false;

        Transform host = panel.transform;
        if (transform.parent != host) transform.SetParent(host, false);

        // Bottom left, top left, top right, bottom right, in the panel's space.
        body.GetWorldCorners(_corners);
        for (int i = 0; i < 4; i++) _corners[i] = host.InverseTransformPoint(_corners[i]);
        Vector3 right = (_corners[3] - _corners[0]).normalized;
        Vector3 up = (_corners[1] - _corners[0]).normalized;
        Vector3 edge = (_corners[2] + _corners[3]) * 0.5f;

        // The maps are measured in metres; the panel's own scale at rest is
        // undone, so they stand their real size beside it.
        float scale = Mathf.Max(panel.RestScale.x, 1e-4f);
        transform.localScale = Vector3.one / scale;
        transform.SetLocalPositionAndRotation(edge + right * ((gap + mapBox.x * 0.5f) / scale),
            Quaternion.LookRotation(Vector3.Cross(right, up), up));
        return true;
    }

    // The slider's thumb, in a hand, puts the maps where it is along the
    // track: one stop per map. Let go, they slide on to the nearest stop.
    private void OnSlide(float share)
    {
        if (!Ready) return;
        _held = true;
        _sliding = false;
        _position = share * (_areas.Count - 1);
    }

    private void OnRelease(float share)
    {
        if (!Ready || !_held) return;
        _held = false;
        _position = share * (_areas.Count - 1);
        SlideTo(Mathf.Clamp(Mathf.RoundToInt(_position), 0, _areas.Count - 1));
    }

    // A slide under way runs on; the thumb, when no hand has it, stands
    // where the maps are.
    private void Slide()
    {
        if (_sliding)
        {
            float t = Mathf.Clamp01((Time.time - _slideStart) / Mathf.Max(_slideSeconds, 1e-3f));
            float eased = 1f - Mathf.Pow(1f - t, 3f);
            _position = Mathf.LerpUnclamped(_slideFrom, _slideTo, eased);
            if (t >= 1f) FinishSlide();
        }
        _slider.Rest(_areas.Count > 1 ? _position / (_areas.Count - 1) : 0f);
    }

    private void SlideTo(float target)
    {
        _slideFrom = _position;
        _slideTo = target;
        _slideStart = Time.time;
        _slideSeconds = slideSeconds * Mathf.Clamp(Mathf.Sqrt(Mathf.Abs(target - _position)), 0.5f, 3f);
        _sliding = true;
    }

    private void FinishSlide()
    {
        _sliding = false;
        _index = Mathf.Clamp(Mathf.RoundToInt(_slideTo), 0, _areas.Count - 1);
        _position = _index;
        _settlePending = true;
    }

    // Each map near the middle stands where the continuous position puts it:
    // the one in the middle at full size, the next smaller and set back at
    // its right, the one before at its left, behind the panel. At rest only
    // the one in the middle is there; the others show only while sliding.
    private void Layout()
    {
        int first = Mathf.Max(Mathf.FloorToInt(_position), 0);
        int last = Mathf.Min(Mathf.CeilToInt(_position), _areas.Count - 1);
        _shown.Clear();

        for (int k = first; k <= last; k++)
        {
            float d = k - _position;
            if (Mathf.Abs(d) >= 0.999f) continue;
            MapAtlas.Area area = _areas[k];
            if (!_shown.Add(area.name)) continue;

            RegionMap map = MapFor(area);
            if (!map.gameObject.activeSelf) map.gameObject.SetActive(true);

            float away = Mathf.Clamp01(Mathf.Abs(d));
            map.transform.localPosition = new Vector3(d * _step, 0f, away * 0.08f);
            map.transform.localRotation = Quaternion.identity;
            map.transform.localScale = Vector3.one * Mathf.Lerp(1f, peekScale, away);
        }

        foreach (KeyValuePair<string, RegionMap> pair in _built)
            if (!_shown.Contains(pair.Key) && pair.Value.gameObject.activeSelf)
                pair.Value.gameObject.SetActive(false);
    }

    // A fingertip arriving at the map's face pokes what it lands on: on a
    // region's map, one of the region's own pins, which opens its metro's
    // sheet; on the national map, a region, which slides to its map. Only
    // the map at rest in the middle can be poked.
    private void Poke()
    {
        bool idle = !_sliding && !_held;
        RegionMap map = idle ? MapFor(_areas[_index]) : null;
        IReadOnlyList<Fingertips.Source> tips = Fingertips.All;

        for (int i = 0; i < tips.Count; i++)
        {
            Fingertips.Source source = tips[i];
            bool touching = false;
            Vector3 local = default;
            if (map != null && source.Live)
            {
                local = map.transform.InverseTransformPoint(source.poke.Origin);
                touching = Mathf.Abs(local.z - RegionMap.FaceZ) <= pokeDepth &&
                           Mathf.Abs(local.x) <= mapBox.x * 0.5f && Mathf.Abs(local.y) <= mapBox.y * 0.5f;
            }

            bool arrived = touching && !_touching.Contains(i);
            if (touching) _touching.Add(i);
            else _touching.Remove(i);
            if (!arrived) continue;

            if (map.Area.IsNational)
            {
                if (map.TryRegionAt(local, out string region)) Show(region, out _);
            }
            else if (map.TryDotAt(local, pokeSlack, out RegionMap.Dot dot) && dot.region == map.Area.region)
            {
                OpenMetro(dot.name);
            }
        }
    }

    // Opens a metro's sheet, and says so on the map's title.
    private void OpenMetro(string metro)
    {
        if (string.IsNullOrEmpty(metro)) return;
        _metro = metro;
        Title(_areas[_index]);

        ManageDatasets datasets = Scene.Datasets;
        if (datasets == null) return;
        int index = datasets.IndexOf(ViewCatalog.SheetUrl(metro));
        if (index >= 0 && index != datasets.ActiveIndex) datasets.SwitchDataset(index);
    }

    // The map's name, its breaches and, on a region's map, the metro whose
    // sheet is on the table.
    private void Title(MapAtlas.Area area)
    {
        int breaches = 0;
        foreach (BreachMap.Region region in _answer.regions)
            if (area.IsNational || region.region == area.region) breaches += region.breaches;
        string count = $"{Formatter.Count(breaches)} {(breaches == 1 ? "breach" : "breaches")}";
        _title.text = area.IsNational || string.IsNullOrEmpty(_metro)
            ? $"{area.name}\n<size=60%>{count}</size>"
            : $"{area.name}\n<size=60%>{count} · showing {_metro}</size>";
    }

    // The map now in the middle. A region's map opens the sheet of the metro
    // already on the table if it is one of the region's, else its largest
    // metro's; the national map takes the sheet off the table.
    private void Settle()
    {
        MapAtlas.Area area = _areas[_index];
        float metresPerKm = MetresPerKm(area);
        float barKm = NiceLength(0.15f * mapBox.x / metresPerKm);
        float barMetres = barKm * metresPerKm;
        float barY = -mapBox.y * 0.5f - ScaleDrop;
        float barLeft = -mapBox.x * 0.5f;
        _scaleBar.SetBox(new Vector3(barLeft + barMetres * 0.5f, barY, RegionMap.FaceZ * 0.5f),
            new Vector3(barMetres, 0.003f, -RegionMap.FaceZ));
        _scaleLabel.transform.localPosition = new Vector3(barLeft + barMetres + 0.01f, barY, RegionMap.FaceZ);
        _scaleLabel.text = $"{barKm:#,0.##} km";

        bool first = _settled == null;
        bool changed = _settled != area.name;
        _settled = area.name;

        string state;
        if (area.IsNational)
        {
            _metro = null;
            Title(area);
            if (Scene.Datasets != null) Scene.Datasets.CloseActive();
            state = "the map shows the whole United States, its regions in their colours, and no sheet is on " +
                    "the table; poking a region on it slides to that region's map";
        }
        else
        {
            if (MetroRegion(_metro) != area.region) _metro = ViewCatalog.LargestMetro(_answer, area.region);
            OpenMetro(_metro);
            state = $"the map shows the {area.name} region, its pins coloured by metro, and the sheet on the " +
                    $"table is the {_metro} metro's; poking another pin opens that metro's sheet";
        }
        // The map it opens on is known, not announced; a slide is news.
        if (first) StateChannel.SetState("map", state);
        else if (changed) StateChannel.RecordState("map", state);
    }

    // The region a metro is in, or null.
    private string MetroRegion(string metro)
    {
        if (string.IsNullOrEmpty(metro)) return null;
        foreach (BreachMap.City city in _answer.cities)
            if (city.metro == metro) return city.region;
        return null;
    }

    // A sheet switched another way, by the assistant, brings its metro's
    // region with it.
    private void OnSheetChanged(int index)
    {
        if (!Ready) return;
        ManageDatasets datasets = Scene.Datasets;
        if (datasets == null || index < 0 || index >= datasets.DatasetCount) return;

        string payload = datasets.Datasets[index].payload;
        foreach (BreachMap.Metro metro in _answer.Metros())
        {
            if (ViewCatalog.SheetUrl(metro.name) != payload) continue;
            _metro = metro.name;
            MapAtlas.Area area = _areas.Find(a => a.region == metro.region);
            if (area == null) return;
            if (_areas[_index] == area && !_sliding) Title(area);
            else Show(area.name, out _);
            return;
        }
    }

    // Slides the maps to one by name ('United States', 'West', or 'US West'),
    // through the maps between, or, while they are closed with the panel,
    // goes straight there. False, with the reason, when there is no such map.
    public bool Show(string name, out string reason)
    {
        reason = null;
        if (!Ready) { reason = "The map has not loaded."; return false; }

        int target = _areas.FindIndex(a =>
            string.Equals(a.name, name?.Trim(), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a.region, name?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (target < 0)
        {
            reason = $"There is no map of '{name}'; the maps are the United States and its regions: " +
                     string.Join(", ", _areas.ConvertAll(a => a.name)) + ".";
            return false;
        }
        if (_held)
        {
            reason = "The user is holding the map's slider.";
            return false;
        }

        // Counted from where the maps are headed: mid-slide, that is the
        // slide's end, not the map being left.
        int from = _sliding ? Mathf.RoundToInt(_slideTo) : _index;
        if (target == from) return true;

        if (!_slot.gameObject.activeSelf)
        {
            _sliding = false;
            _index = target;
            _position = _index;
            Settle();
            return true;
        }

        SlideTo(target);
        return true;
    }

    private RegionMap MapFor(MapAtlas.Area area)
    {
        if (_built.TryGetValue(area.name, out RegionMap built) && built != null) return built;
        RegionMap.Look look = _look;
        look.metresPerKm = MetresPerKm(area);
        RegionMap map = RegionMap.Build(_slot, _atlas, area, _answer.cities, _regionColors, look);
        _built[area.name] = map;
        return map;
    }

    // As large as the map fits the map box, whichever of its width and height
    // meets the box first.
    private float MetresPerKm(MapAtlas.Area area)
    {
        Rect bounds = area.bounds;
        return Mathf.Min(mapBox.x / Mathf.Max(bounds.width, 1e-3f), mapBox.y / Mathf.Max(bounds.height, 1e-3f));
    }

    // 1, 2 or 5 times a power of ten, at most the length given.
    private static float NiceLength(float km)
    {
        float power = Mathf.Pow(10f, Mathf.Floor(Mathf.Log10(Mathf.Max(km, 1e-3f))));
        foreach (float step in new[] { 5f, 2f, 1f })
            if (step * power <= km) return step * power;
        return power;
    }

    private static TextMeshPro Label(Transform parent, string name, TMP_FontAsset font, float scale)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        TextMeshPro label = go.AddComponent<TextMeshPro>();
        Style.ApplyBody(label);
        if (font != null) label.font = font;
        label.alignment = TextAlignmentOptions.Center;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.overflowMode = TextOverflowModes.Overflow;
        label.rectTransform.sizeDelta = new Vector2(40f, 8f);
        label.color = Style.White;
        go.transform.localScale = Vector3.one * (Style.WorldTextScale * scale);
        return label;
    }

    // The scale bar, a bar like the table's: one of the app's cubes, in the
    // land's colour. Its length is set for each map as it settles.
    private static CreateCube Bar(Transform parent, Material material)
    {
        var go = new GameObject("Scale Bar", typeof(MeshFilter), typeof(MeshRenderer), typeof(BoxCollider));
        go.transform.SetParent(parent, false);
        var cube = go.AddComponent<CreateCube>();
        cube.Init(material);
        cube.SetColor(Land);
        go.GetComponent<BoxCollider>().enabled = false;
        return cube;
    }
}
