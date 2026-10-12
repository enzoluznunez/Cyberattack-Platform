using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;

// The maps, one country at a time. The user reaches out, takes hold of the
// map itself and swipes it aside, and the next country slides into its place;
// the countries run most breaches first and wrap around, so the list is a
// ring. The next one waits small at the right, to say there is more to swipe
// to; the one before slides in from behind the panel.
//
// The maps stand beside the tool panel, to its right in its own plane, as one
// piece with it: they are hung on the panel itself, so they are carried with
// it, frozen in step, and only the user's own slide moves them on it. They
// close when it closes and open again when it opens.
//
// Each country is drawn as large as fits a box the panel's size, not to one
// shared scale: a small country is as easy to read as a large one.
//
// The country on the map is the country the sheets count: when one settles,
// every listed sheet is narrowed to it. Pointing a fingertip at a dot names
// its city and how many breaches it holds.
public class MapCarousel : MonoBehaviour
{
    public static MapCarousel Instance { get; private set; }

    [Header("Beside the tool panel")]
    [Tooltip("Space between the panel's right edge and the box the map is drawn in, in metres.")]
    public float gap = 0.05f;

    [Header("Scale")]
    [Tooltip("Width and height of the box each country's map is fitted into, in metres: the panel's own size, " +
             "so a map is never larger than the panel beside it.")]
    public Vector2 mapBox = new Vector2(0.4f, 0.4f);
    [Tooltip("Radius of a city with one breach, in metres. A dot's area grows with its count.")]
    public float oneBreachRadius = 0.004f;
    [Tooltip("How thick the land is, in metres.")]
    public float thickness = 0.012f;
    [Tooltip("How far a dot stands out of the land, in metres.")]
    public float dotHeight = 0.004f;
    [Tooltip("Smallest width and height a map can be taken hold of by, in metres, for countries " +
             "smaller than a hand.")]
    public float smallestHold = 0.06f;

    [Header("Swipe")]
    [Tooltip("Share of the distance between maps a swipe has to cover to move on.")]
    [Range(0.05f, 0.9f)] public float commitShare = 0.25f;
    [Tooltip("Speed of a flick that moves on however short it was, in metres per second.")]
    public float flickSpeed = 0.8f;
    [Tooltip("Shortest pull that a flick still moves on from, in metres: a tremor is not a flick.")]
    public float flickReach = 0.02f;
    [Tooltip("Seconds before a pinch lets go that are left out of the swipe: the fingers opening drag the " +
             "pinch point, which would otherwise read as a flick.")]
    public float releaseIgnore = 0.06f;
    [Tooltip("How quickly a held map settles on the hand when it is still, in hertz: lower is steadier.")]
    public float steadyCutoff = 3f;
    [Tooltip("How much faster it follows a moving hand, per metre per second of its speed.")]
    public float speedCutoff = 10f;
    [Tooltip("Seconds a map takes to slide one place.")]
    public float slideSeconds = 0.35f;
    [Tooltip("Size of the maps waiting at either side, against the one in the middle.")]
    [Range(0.1f, 1f)] public float peekScale = 0.4f;

    [Header("Pointing at a city")]
    [Tooltip("How close a fingertip has to come to the map's face, in metres.")]
    public float pointDepth = 0.06f;
    [Tooltip("How far outside a dot a fingertip still names it, in metres.")]
    public float pointSlack = 0.01f;

    public static readonly Color Land = Style.Hex(0xA7, 0xB3, 0xC2);
    public static readonly Color LandSide = Style.Hex(0x6E, 0x7B, 0x8C);
    public static readonly Color Coast = Style.Hex(0x40, 0x4B, 0x59);
    public static readonly Color DotTop = Style.Hex(0xD9, 0x3A, 0x40);
    public static readonly Color DotSide = Style.Hex(0x8A, 0x1A, 0x20);

    public bool Ready => _countries.Count > 0;

    // How much room the maps take at the panel's side, gap included, in
    // metres; the panel opens that much to the left so the two are centred.
    public float SideWidth => gap + mapBox.x;

    private MapAtlas _atlas;
    private readonly List<BreachMap.Country> _countries = new List<BreachMap.Country>();
    private readonly Dictionary<string, (CountryMap map, MapSwipe hold)> _built =
        new Dictionary<string, (CountryMap, MapSwipe)>();
    private readonly HashSet<string> _shown = new HashSet<string>();

    private CountryMap.Look _look;
    private float _step;

    private Transform _slot;
    private TextMeshPro _title;
    private TextMeshPro _cityLabel;
    private BreachMap.City _labelled;
    private TextMeshPro _scaleLabel;
    private CreateCube _scaleBar;

    private readonly Vector3[] _corners = new Vector3[4];

    private int _index;
    private float _position;
    private string _settled;
    private bool _sliding;
    private float _slideFrom, _slideTo, _slideStart, _slideSeconds;
    private bool _settlePending;
    private bool _wasGrabbed;

    // The held map's smoothed pull and when it was there, for the last half
    // second: the release reads its speed from these.
    private readonly List<(float time, float offset)> _pull = new List<(float, float)>();
    private readonly OneEuroPoseFilter _smooth = new OneEuroPoseFilter();
    private const float PullHistorySeconds = 0.5f;
    private const float FlickWindowSeconds = 0.1f;

    // How far either side of the middle the maps are made ahead of being seen.
    private const int BuildAhead = 2;

    private void Awake()
    {
        if (Instance == null) Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private async void Start()
    {
        // The outlines need nothing from the API, so they are parsed while
        // the address and key are read and the dots fetched.
        byte[] outlines = MapAtlas.ReadBytes();
        Task<MapAtlas> parse = outlines != null ? Task.Run(() => MapAtlas.Parse(outlines)) : null;

        while (!ViewCatalog.Configured) await Task.Yield();
        if (this == null) return;

        List<BreachMap.Country> found;
        try
        {
            Task<List<BreachMap.Country>> fetch = BreachMap.Fetch();
            _atlas = parse != null ? await parse : null;
            found = await fetch;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[MapCarousel] The map could not be drawn: {e.Message}");
            found = null;
        }
        if (this == null) return;

        if (_atlas == null || found == null)
        {
            // Without a map there is no country to narrow to, so the sheets
            // count every breach.
            if (Scene.Datasets != null) Scene.Datasets.SetCountry(null);
            Notices.Show(this, "No Map", _atlas == null
                ? "The country outlines are missing from this build."
                : CyberApi.Unreachable);
            return;
        }

        foreach (BreachMap.Country country in found)
        {
            if (_atlas.Find(country.name) != null) _countries.Add(country);
            else Debug.LogWarning($"[MapCarousel] No outline for {country.name}; it is left out of the maps.");
        }
        if (_countries.Count == 0)
        {
            if (Scene.Datasets != null) Scene.Datasets.SetCountry(null);
            return;
        }

        Build();

        string startAt = Scene.Datasets != null ? Scene.Datasets.Country : null;
        _index = Mathf.Max(_countries.FindIndex(c => c.name == startAt), 0);
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

        _look = new CountryMap.Look
        {
            dotRadius = oneBreachRadius,
            thickness = thickness,
            dotHeight = dotHeight,
            coastWidth = 0.0012f,
            land = Land,
            landSide = LandSide,
            coast = Coast,
            dot = DotTop,
            dotSide = DotSide,
            material = material
        };

        _slot = new GameObject("Slot").transform;
        _slot.SetParent(transform, false);
        _slot.gameObject.SetActive(false);

        _title = Label(_slot, "Title", Style.TitleFont, 1.6f);
        _title.transform.localPosition = new Vector3(0f, mapBox.y * 0.5f + 0.05f, -thickness);

        _scaleBar = Bar(_slot, material);
        _scaleLabel = Label(_slot, "Scale", Style.BodyFont, 1f);
        _scaleLabel.alignment = TextAlignmentOptions.Left;
        _scaleLabel.rectTransform.pivot = new Vector2(0f, 0.5f);

        _cityLabel = Label(_slot, "City", Style.BodyFont, 1.2f);
        _cityLabel.gameObject.SetActive(false);
    }

    private void LateUpdate()
    {
        if (_slot == null) return;

        // A slide that landed last frame is drawn landed before the sheets
        // switch, so the work of switching them falls on a frame where
        // nothing on the map moves.
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
            // sheets still follow it, and a map let go of by closing is not
            // held any more.
            if (_sliding) FinishSlide();
            else if (_wasGrabbed) _position = _index;
            _wasGrabbed = false;
            return;
        }

        Swipe();
        if (!_sliding && !_wasGrabbed) BuildNext();
        Layout();
        Point();
    }

    // Makes one map near the middle that has not been made yet, a frame at a
    // time while the maps rest, so a swipe never waits on one being built.
    private void BuildNext()
    {
        for (int k = 1; k <= BuildAhead; k++)
        {
            foreach (int side in new[] { k, -k })
            {
                BreachMap.Country country = _countries[Wrap(_index + side)];
                if (_built.TryGetValue(country.name, out var built) && built.map != null) continue;
                Built(country);
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

    // While the map in the middle is held, it follows the hand and its
    // neighbours follow it; on release they slide on to the next country or
    // back to this one.
    private void Swipe()
    {
        MapSwipe hold = HoldFor(_countries[_index]);
        bool grabbed = hold.IsGrabbed;

        if (grabbed)
        {
            if (!_wasGrabbed)
            {
                _sliding = false;
                _pull.Clear();
                _smooth.Reset();
                _smooth.posMinCutoff = steadyCutoff;
                _smooth.posBeta = speedCutoff;
            }

            // The hand's tremor is taken out of the pull: the map is put
            // where the filtered pull says, and the hand's next frame starts
            // from the hand again, not from here.
            float now = Time.time;
            float offset = _smooth.Filter(new Pose(new Vector3(hold.Offset, 0f, 0f), Quaternion.identity),
                Time.deltaTime).position.x;
            hold.transform.localPosition = new Vector3(offset, 0f, 0f);

            _pull.Add((now, offset));
            while (_pull.Count > 2 && now - _pull[0].time > PullHistorySeconds) _pull.RemoveAt(0);
            _position = _index - offset / _step;
        }
        else if (_wasGrabbed) SlideTo(ReleaseTarget());
        _wasGrabbed = grabbed;

        if (_sliding)
        {
            float t = Mathf.Clamp01((Time.time - _slideStart) / Mathf.Max(_slideSeconds, 1e-3f));
            float eased = 1f - Mathf.Pow(1f - t, 3f);
            _position = Mathf.LerpUnclamped(_slideFrom, _slideTo, eased);
            if (t >= 1f) FinishSlide();
        }

        // Only the map in the middle can be held, and only while it rests.
        string middle = _countries[_index].name;
        foreach (KeyValuePair<string, (CountryMap map, MapSwipe hold)> pair in _built)
            pair.Value.hold.SetHoldable(pair.Key == middle && !_sliding);
    }

    private void SlideTo(float target)
    {
        _slideFrom = _position;
        _slideTo = target;
        _slideStart = Time.time;
        _slideSeconds = slideSeconds * Mathf.Clamp(Mathf.Sqrt(Mathf.Abs(target - _position)), 0.5f, 3f);
        _sliding = true;
    }

    // Where a let-go map goes: on, back or nowhere. Read from the pull as it
    // was just before the fingers opened, so the opening is not a flick; a
    // flick has to be fast and also go somewhere.
    private int ReleaseTarget()
    {
        if (_pull.Count == 0) return _index;

        float end = Time.time - releaseIgnore;
        int last = _pull.Count - 1;
        while (last > 0 && _pull[last].time > end) last--;
        int first = last;
        while (first > 0 && _pull[last].time - _pull[first - 1].time <= FlickWindowSeconds) first--;

        float offset = _pull[last].offset;
        float seconds = _pull[last].time - _pull[first].time;
        float speed = seconds > 1e-3f ? (offset - _pull[first].offset) / seconds : 0f;

        if (offset < -commitShare * _step || (speed < -flickSpeed && offset < -flickReach)) return _index + 1;
        if (offset > commitShare * _step || (speed > flickSpeed && offset > flickReach)) return _index - 1;
        return _index;
    }

    private void FinishSlide()
    {
        _sliding = false;
        _index = Wrap(Mathf.RoundToInt(_slideTo));
        _position = _index;
        _settlePending = true;
    }

    // Each map near the middle stands where the continuous position puts it:
    // the one in the middle at full size, the next smaller and set back at
    // its right, and the rest out of sight. The one before waits out of sight
    // too, since its place is behind the panel, and is only seen sliding in
    // or out. A map in the hand is where the hand has it.
    private void Layout()
    {
        int first = Mathf.FloorToInt(_position) - 1, last = Mathf.CeilToInt(_position) + 1;
        _shown.Clear();
        string held = _wasGrabbed ? _countries[_index].name : null;

        for (int k = first; k <= last; k++)
        {
            float d = k - _position;
            BreachMap.Country country = _countries[Wrap(k)];
            // The held map stays out wherever the hand takes it; the cut-off
            // only puts away the ones waiting at the sides.
            bool inHand = country.name == held;
            if (!inHand && (d > 1.4f || d <= -0.999f)) continue;
            if (!_shown.Add(country.name)) continue;

            CountryMap map = MapFor(country);
            if (!map.gameObject.activeSelf) map.gameObject.SetActive(true);
            if (inHand) continue;

            float away = Mathf.Clamp01(Mathf.Abs(d));
            map.transform.localPosition = new Vector3(d * _step, 0f, away * 0.08f);
            map.transform.localRotation = Quaternion.identity;
            map.transform.localScale = Vector3.one * Mathf.Lerp(1f, peekScale, away);
        }

        foreach (KeyValuePair<string, (CountryMap map, MapSwipe hold)> pair in _built)
            if (!_shown.Contains(pair.Key) && pair.Value.map.gameObject.activeSelf)
                pair.Value.map.gameObject.SetActive(false);
    }

    // A fingertip at the map names the dot it is on.
    private void Point()
    {
        bool idle = !_sliding && !_wasGrabbed;
        CountryMap map = idle ? MapFor(_countries[_index]) : null;
        CountryMap.Dot dot = default;
        bool found = false;
        float face = 0f;

        if (map != null)
        {
            float best = float.MaxValue;
            face = -map.Thickness - dotHeight;
            foreach (Fingertips.Source source in Fingertips.All)
            {
                if (!source.Live) continue;
                Vector3 local = map.transform.InverseTransformPoint(source.poke.Origin);
                if (Mathf.Abs(local.z - face) > pointDepth) continue;
                if (!map.TryDotAt(local, pointSlack, out CountryMap.Dot hit)) continue;
                float distanceSq = ((Vector2)local - hit.centre).sqrMagnitude;
                if (distanceSq >= best) continue;
                best = distanceSq;
                dot = hit;
                found = true;
            }
        }

        if (found != _cityLabel.gameObject.activeSelf) _cityLabel.gameObject.SetActive(found);
        if (!found) { _labelled = null; return; }

        if (dot.city != _labelled)
        {
            _labelled = dot.city;
            int count = dot.city.breaches;
            _cityLabel.text = $"{dot.city.name}\n<size=80%>{Formatter.Count(count)} {(count == 1 ? "breach" : "breaches")}</size>";
        }
        _cityLabel.transform.localPosition = new Vector3(dot.centre.x, dot.centre.y + dot.radius + 0.025f, face - 0.01f);
    }

    // The country now in the middle is the one the sheets count.
    private void Settle()
    {
        BreachMap.Country country = _countries[_index];
        int cities = country.cities.Count;
        float metresPerKm = MetresPerKm(country);
        float barKm = NiceLength(0.15f * mapBox.x / metresPerKm);
        float barMetres = barKm * metresPerKm;
        float barY = -mapBox.y * 0.5f - 0.04f;
        float barLeft = -mapBox.x * 0.5f;
        _scaleBar.SetBox(new Vector3(barLeft + barMetres * 0.5f, barY, -thickness * 0.5f),
            new Vector3(barMetres, 0.003f, thickness));
        _scaleLabel.transform.localPosition = new Vector3(barLeft + barMetres + 0.01f, barY, -thickness);
        _scaleLabel.text = $"{barKm:#,0.##} km";

        _title.text = $"{country.name}\n<size=60%>{Formatter.Count(country.breaches)} {(country.breaches == 1 ? "breach" : "breaches")}" +
                      $" · {cities} {(cities == 1 ? "city" : "cities")}</size>";

        var datasets = Scene.Datasets;
        bool first = _settled == null;
        bool changed = _settled != country.name;
        _settled = country.name;
        if (datasets != null) datasets.SetCountry(country.name);

        string state = $"the map shows {country.name}, and the listed sheets count only breaches at companies " +
                       $"headquartered there ({country.breaches} breaches)";
        // The country it opens on is known, not announced; a swipe is news.
        if (first) StateChannel.SetState("country", state);
        else if (changed) StateChannel.RecordState("country", state);
    }

    // Slides the maps to a country by name, the short way round the ring, or,
    // while they are closed with the panel, goes straight there: the sheets
    // are narrowed to it now, and the map opens on it. False, with the
    // reason, when there is no map of it.
    public bool Show(string name, out string reason)
    {
        reason = null;
        if (!Ready) { reason = "The map has not loaded."; return false; }

        int target = _countries.FindIndex(c => string.Equals(c.name, name?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (target < 0)
        {
            reason = $"There is no map of '{name}'; no breached company is headquartered there.";
            return false;
        }
        if (_wasGrabbed)
        {
            reason = "The user is holding the map.";
            return false;
        }

        // Counted from where the maps are headed: mid-slide, that is the
        // slide's end, not the country being left.
        int from = _sliding ? Mathf.RoundToInt(_slideTo) : _index;
        int n = _countries.Count;
        int delta = ((target - Wrap(from)) % n + n) % n;
        if (delta > n / 2) delta -= n;
        if (delta == 0) return true;

        if (!_slot.gameObject.activeSelf)
        {
            _sliding = false;
            _index = Wrap(from + delta);
            _position = _index;
            Settle();
            return true;
        }

        SlideTo(from + delta);
        return true;
    }

    private CountryMap MapFor(BreachMap.Country country) => Built(country).map;

    private MapSwipe HoldFor(BreachMap.Country country) => Built(country).hold;

    private (CountryMap map, MapSwipe hold) Built(BreachMap.Country country)
    {
        if (_built.TryGetValue(country.name, out var built) && built.map != null) return built;
        CountryMap.Look look = _look;
        look.metresPerKm = MetresPerKm(country);
        CountryMap map = CountryMap.Build(_slot, _atlas.Find(country.name), country, look);
        built = (map, MapSwipe.Attach(map, smallestHold, thickness + dotHeight, _step * 1.5f));
        _built[country.name] = built;
        return built;
    }

    // As large as the country fits the map box, whichever of its width and
    // height meets the box first.
    private float MetresPerKm(BreachMap.Country country)
    {
        Rect bounds = _atlas.Find(country.name).bounds;
        return Mathf.Min(mapBox.x / Mathf.Max(bounds.width, 1e-3f), mapBox.y / Mathf.Max(bounds.height, 1e-3f));
    }

    private int Wrap(int k)
    {
        int n = _countries.Count;
        return ((k % n) + n) % n;
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
    // land's colour. Its length is set for each country as it settles.
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
