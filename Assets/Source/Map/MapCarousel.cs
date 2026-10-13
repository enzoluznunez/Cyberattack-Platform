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
// The map is the sheet on the table: when a map settles, its sheet is opened,
// every breach in the region or, for the national map, in the country; and
// when the sheet is switched another way the map slides to it. A pin and its
// bars are one colour, and pointing at either lights the other.
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
    [Tooltip("Radius of a city with one breach, in metres. A pin's area grows with its count.")]
    public float oneBreachRadius = 0.004f;
    [Tooltip("How thick the land is, in metres.")]
    public float thickness = 0.012f;

    [Header("Slide")]
    [Tooltip("Seconds a map takes to slide one place.")]
    public float slideSeconds = 0.35f;
    [Tooltip("Size of a map coming in from either side, against the one in the middle.")]
    [Range(0.1f, 1f)] public float peekScale = 0.4f;

    [Header("Pointing at a city")]
    [Tooltip("How close a fingertip has to come to the map's face, in metres.")]
    public float pointDepth = 0.06f;
    [Tooltip("How far outside a pin a fingertip still names it, in metres.")]
    public float pointSlack = 0.01f;

    // The tool panel's own colours: black land edged in white, as the panel
    // is a black face in a white border. Only the pins are coloured.
    public static readonly Color Land = Style.Black;
    public static readonly Color LandSide = Style.White;
    public static readonly Color Coast = Style.White;
    public static readonly Color Border = Style.White;
    public static readonly Color Halo = Style.White;

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
    private TextMeshPro _cityLabel;
    private Transform _halo;
    private string _labelled;
    private TextMeshPro _scaleLabel;
    private CreateCube _scaleBar;
    private SlideBar _slider;

    // The city whose bars a fingertip is on at the sheet, whose pin the map
    // rings; and the pin a fingertip is on at the map, whose bars the sheet
    // lights.
    private string _barCity;
    private string _glowing;
    private ReadSheets _readSheets;

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
        if (_readSheets != null)
        {
            _readSheets.OnHover -= OnBarHover;
            _readSheets.OnCleared -= OnBarCleared;
        }
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
        _readSheets = FindAnyObjectByType<ReadSheets>();
        if (_readSheets != null)
        {
            _readSheets.OnHover += OnBarHover;
            _readSheets.OnCleared += OnBarCleared;
        }

        _index = 0;
        _position = _index;
        Settle();
    }

    private void Build()
    {
        _step = mapBox.x * 0.75f;

        // The bars' own material, drawn from both sides: a map's walls follow
        // its coast whichever way round the outline runs.
        Material material = new Material(Resources.Load<Material>("Materials/SheetMaterial")) { name = "Map" };
        material.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);

        _look = new RegionMap.Look
        {
            dotRadius = oneBreachRadius,
            thickness = thickness,
            coastWidth = 0.0012f,
            borderWidth = 0.0018f,
            land = Land,
            landSide = LandSide,
            coast = Coast,
            border = Border,
            material = material
        };

        _slot = new GameObject("Slot").transform;
        _slot.SetParent(transform, false);
        _slot.gameObject.SetActive(false);

        _title = Label(_slot, "Title", Style.TitleFont, 1.6f);
        _title.transform.localPosition = new Vector3(0f, mapBox.y * 0.5f + 0.05f, -thickness);

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

        _cityLabel = Label(_slot, "City", Style.BodyFont, 1.2f);
        _cityLabel.gameObject.SetActive(false);

        _halo = HaloRing(_slot, material);
        _halo.gameObject.SetActive(false);
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
            Glow(null);
            return;
        }

        Slide();
        if (!_sliding && !_held) BuildNext();
        Layout();
        Point();
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

    // A fingertip at the map names the pin it is on and lights that pin's
    // bars on the sheet; a fingertip at the sheet rings the pin of the bar it
    // is on.
    private void Point()
    {
        bool idle = !_sliding && !_held;
        RegionMap map = idle ? MapFor(_areas[_index]) : null;
        RegionMap.Dot dot = default;
        bool pointed = false;
        float face = -thickness;

        if (map != null)
        {
            float best = float.MaxValue;
            foreach (Fingertips.Source source in Fingertips.All)
            {
                if (!source.Live) continue;
                Vector3 local = map.transform.InverseTransformPoint(source.poke.Origin);
                if (Mathf.Abs(local.z - face) > pointDepth) continue;
                if (!map.TryDotAt(local, pointSlack, out RegionMap.Dot hit)) continue;
                float distanceSq = ((Vector2)local - hit.centre).sqrMagnitude;
                if (distanceSq >= best) continue;
                best = distanceSq;
                dot = hit;
                pointed = true;
            }
        }

        Glow(pointed ? dot : (RegionMap.Dot?)null);

        bool ringed = pointed || (map != null && _barCity != null && map.TryDotOf(_barCity, out dot));
        if (ringed != _cityLabel.gameObject.activeSelf) _cityLabel.gameObject.SetActive(ringed);
        if (ringed != _halo.gameObject.activeSelf) _halo.gameObject.SetActive(ringed);
        if (!ringed) { _labelled = null; return; }

        if (dot.name != _labelled)
        {
            _labelled = dot.name;
            int count = dot.breaches, cities = dot.cities.Count;
            string around = cities > 1 ? $"\n<size=70%>{cities} cities</size>" : "";
            _cityLabel.text = $"{dot.name}\n<size=80%>{Formatter.Count(count)} " +
                              $"{(count == 1 ? "breach" : "breaches")}</size>{around}";
        }
        _cityLabel.transform.localPosition = new Vector3(dot.centre.x, dot.centre.y + dot.radius + 0.025f, face - 0.01f);
        _halo.localPosition = new Vector3(dot.centre.x, dot.centre.y, face - 0.002f);
        _halo.localScale = new Vector3(dot.radius, dot.radius, 1f);
    }

    // Lights the open sheet's bars for a pin's breaches, once each time the
    // pin pointed at changes.
    private void Glow(RegionMap.Dot? dot)
    {
        string name = dot?.name;
        if (name == _glowing) return;
        _glowing = name;
        List<string> cities = dot?.cities;

        ManageSheets sheets = Scene.Sheets;
        DataSource data = Scene.Data;
        if (sheets == null) return;

        var rows = new HashSet<int>();
        if (cities != null && data != null)
            for (int r = 0; r < data.RowCount; r++)
                if (cities.Contains(data.RowCityAt(r))) rows.Add(r);
        sheets.SetRowGlow(rows);
    }

    private void OnBarHover(ReadSheets.Reading reading)
    {
        DataSource data = Scene.Data;
        _barCity = reading.valid && data != null ? data.RowCityAt(reading.dataRow) : null;
    }

    private void OnBarCleared() => _barCity = null;

    // The map now in the middle, which opens its sheet.
    private void Settle()
    {
        MapAtlas.Area area = _areas[_index];
        float metresPerKm = MetresPerKm(area);
        float barKm = NiceLength(0.15f * mapBox.x / metresPerKm);
        float barMetres = barKm * metresPerKm;
        float barY = -mapBox.y * 0.5f - ScaleDrop;
        float barLeft = -mapBox.x * 0.5f;
        _scaleBar.SetBox(new Vector3(barLeft + barMetres * 0.5f, barY, -thickness * 0.5f),
            new Vector3(barMetres, 0.003f, thickness));
        _scaleLabel.transform.localPosition = new Vector3(barLeft + barMetres + 0.01f, barY, -thickness);
        _scaleLabel.text = $"{barKm:#,0.##} km";

        int breaches = 0, cities = 0;
        foreach (BreachMap.Region region in _answer.regions)
            if (area.IsNational || region.region == area.region) breaches += region.breaches;
        foreach (BreachMap.City city in _answer.cities)
            if (area.IsNational || city.region == area.region) cities++;
        _title.text = $"{area.name}\n<size=60%>{Formatter.Count(breaches)} {(breaches == 1 ? "breach" : "breaches")}" +
                      $" · {cities} {(cities == 1 ? "city" : "cities")}</size>";

        bool first = _settled == null;
        bool changed = _settled != area.name;
        _settled = area.name;

        OpenSheet(area.region);

        string state = area.IsNational
            ? $"the map shows the whole United States ({breaches} breaches), its pins coloured by region, " +
              "and the sheet on the table is the national one, every US breach coloured by its region"
            : $"the map shows the {area.name} region ({breaches} breaches), its pins coloured by metro, " +
              $"and the sheet on the table is the {area.name}'s";
        // The map it opens on is known, not announced; a slide is news.
        if (first) StateChannel.SetState("map", state);
        else if (changed) StateChannel.RecordState("map", state);
    }

    private static void OpenSheet(string region)
    {
        ManageDatasets datasets = Scene.Datasets;
        if (datasets == null) return;
        int index = datasets.IndexOf(ViewCatalog.SheetUrl(region));
        if (index >= 0 && index != datasets.ActiveIndex) datasets.SwitchDataset(index);
    }

    // A sheet switched another way, by the assistant, brings its map with it.
    private void OnSheetChanged(int index)
    {
        if (!Ready) return;
        ManageDatasets datasets = Scene.Datasets;
        if (datasets == null || index < 0 || index >= datasets.DatasetCount) return;

        string payload = datasets.Datasets[index].payload;
        foreach (MapAtlas.Area area in _areas)
            if (ViewCatalog.SheetUrl(area.region) == payload)
            {
                Show(area.name, out _);
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

    // A white ring for a pin of radius one, set around whichever pin is
    // pointed at, on the map or through its bars; scaled to the pin.
    private static Transform HaloRing(Transform parent, Material material)
    {
        const int segments = 32;
        const float inner = 1.15f, outer = 1.6f;
        var vertices = new List<Vector3>(segments * 2);
        var colors = new List<Color>(segments * 2);
        var triangles = new List<int>(segments * 6);
        for (int k = 0; k < segments; k++)
        {
            float a = k * Mathf.PI * 2f / segments;
            var dir = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
            vertices.Add(dir * inner);
            vertices.Add(dir * outer);
            colors.Add(Halo);
            colors.Add(Halo);
            int i = k * 2, j = (k + 1) % segments * 2;
            triangles.AddRange(new[] { i, j, i + 1, i + 1, j, j + 1 });
        }
        var mesh = new Mesh { name = "Halo" };
        mesh.SetVertices(vertices);
        mesh.SetColors(colors);
        mesh.SetTriangles(triangles, 0);

        var go = new GameObject("Halo", typeof(MeshFilter), typeof(MeshRenderer));
        go.transform.SetParent(parent, false);
        go.GetComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return go.transform;
    }
}
