using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Oculus.Interaction;
using Oculus.Interaction.Input;
using TMPro;
using UnityEngine;

// The maps, one country at a time, centred in front of the user. The user
// reaches out, takes hold of the map itself and swipes it aside, and the next
// country slides into its place; the countries run most breaches first and
// wrap around, so the list is a ring. Its neighbours wait at either side,
// small, to say there is more to swipe to.
//
// The maps stand where the user faces each time the headset goes on, the
// given distance in front of them, as the table does.
//
// The country on the map is the country the sheets count: when one settles,
// every listed sheet is narrowed to it. Pointing a fingertip at a dot names
// its city and how many breaches it holds.
public class MapCarousel : MonoBehaviour
{
    public static MapCarousel Instance { get; private set; }

    [Header("Placement, from the user")]
    [Tooltip("How far in front of the user the middle of the map stands, in metres. Within arm's reach, " +
             "since the map is swiped by hand.")]
    public float distance = 0.55f;
    [Tooltip("How far below the user's eyes the middle of the map stands, in metres.")]
    public float belowEyes = 0.1f;
    [Tooltip("How far the map leans back from upright, in degrees.")]
    public float leanBack = 10f;

    [Header("Scale")]
    [Tooltip("Width of the widest country's map, in metres. Every map shares this scale.")]
    public float widestMapMetres = 0.8f;
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
    [Tooltip("Seconds a map takes to slide one place.")]
    public float slideSeconds = 0.35f;
    [Tooltip("Size of the maps waiting at either side, against the one in the middle.")]
    [Range(0.1f, 1f)] public float peekScale = 0.4f;

    [Header("Pointing at a city")]
    [Tooltip("How close a fingertip has to come to the map's face, in metres.")]
    public float pointDepth = 0.06f;
    [Tooltip("How far outside a dot a fingertip still names it, in metres.")]
    public float pointSlack = 0.01f;

    public static readonly Color Land = Hex(0xA7, 0xB3, 0xC2);
    public static readonly Color LandSide = Hex(0x6E, 0x7B, 0x8C);
    public static readonly Color Coast = Hex(0x40, 0x4B, 0x59);
    public static readonly Color DotTop = Hex(0xD9, 0x3A, 0x40);
    public static readonly Color DotSide = Hex(0x8A, 0x1A, 0x20);

    // The country the maps are on, by its name as the API spells it.
    public string Current => _countries.Count > 0 ? _countries[_index].name : null;
    public IReadOnlyList<BreachMap.Country> Countries => _countries;
    public bool Ready => _countries.Count > 0;

    public event Action<string> OnCountryChanged;

    private MapAtlas _atlas;
    private readonly List<BreachMap.Country> _countries = new List<BreachMap.Country>();
    private readonly Dictionary<string, CountryMap> _built = new Dictionary<string, CountryMap>();
    private readonly Dictionary<string, MapSwipe> _holds = new Dictionary<string, MapSwipe>();

    private CountryMap.Look _look;
    private float _step;
    private Vector2 _slotSize;

    private Transform _slot;
    private TextMeshPro _title;
    private TextMeshPro _cityLabel;
    private TextMeshPro _scaleLabel;

    private bool _anchored;
    private bool _recenterHooked;

    private int _index;
    private float _position;
    private string _settled;
    private bool _first = true;
    private bool _sliding;
    private float _slideFrom, _slideTo, _slideStart, _slideSeconds;
    private bool _wasGrabbed;
    private float _lastOffset;
    private float _velocity;

    private readonly List<(PokeInteractor poke, IHand hand)> _pokes = new List<(PokeInteractor, IHand)>();
    private float _nextScan;

    private void Awake()
    {
        if (Instance == null) Instance = this;
    }

    private void OnEnable() => OVRManager.HMDMounted += RecenterToUser;

    private void OnDisable()
    {
        OVRManager.HMDMounted -= RecenterToUser;
        if (_recenterHooked && OVRManager.display != null)
        {
            OVRManager.display.RecenteredPose -= RecenterToUser;
            _recenterHooked = false;
        }
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    // Put on again, or recentred: the maps move to where the user now faces.
    public void RecenterToUser() => _anchored = false;

    private async void Start()
    {
        while (!ViewCatalog.Configured) await Task.Yield();
        if (this == null) return;

        string text = MapAtlas.ReadText();
        List<BreachMap.Country> found;
        try
        {
            _atlas = text != null ? await Task.Run(() => MapAtlas.Parse(text)) : null;
            found = await BreachMap.Fetch();
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
        float metresPerKm = widestMapMetres / Mathf.Max(_atlas.WidestKm, 1f);
        _step = widestMapMetres * 0.75f;
        _slotSize = new Vector2(widestMapMetres, _atlas.TallestKm * metresPerKm);

        // The bars' own material, drawn from both sides: a map's walls follow
        // its coast whichever way round the outline runs.
        Material material = new Material(Resources.Load<Material>("Materials/SheetMaterial")) { name = "Map" };
        material.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);

        _look = new CountryMap.Look
        {
            metresPerKm = metresPerKm,
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
        _title.transform.localPosition = new Vector3(0f, _slotSize.y * 0.5f + 0.05f, -thickness);

        float barKm = NiceLength(0.15f * widestMapMetres / metresPerKm);
        float barMetres = barKm * metresPerKm;
        float barY = -_slotSize.y * 0.5f - 0.04f;
        float barLeft = -_slotSize.x * 0.5f;
        Bar(_slot, new Vector3(barLeft + barMetres * 0.5f, barY, -thickness * 0.5f),
            new Vector3(barMetres, 0.003f, thickness), material);
        _scaleLabel = Label(_slot, "Scale", Style.BodyFont, 1f);
        _scaleLabel.alignment = TextAlignmentOptions.Left;
        _scaleLabel.rectTransform.pivot = new Vector2(0f, 0.5f);
        _scaleLabel.transform.localPosition = new Vector3(barLeft + barMetres + 0.01f, barY, -thickness);
        _scaleLabel.text = $"{barKm:#,0} km";

        _cityLabel = Label(_slot, "City", Style.BodyFont, 1.2f);
        _cityLabel.gameObject.SetActive(false);
    }

    private void Update()
    {
        if (!_recenterHooked && OVRManager.display != null)
        {
            OVRManager.display.RecenteredPose += RecenterToUser;
            _recenterHooked = true;
        }
    }

    private void LateUpdate()
    {
        if (_slot == null) return;

        if (!_anchored) _anchored = Anchor();
        if (_slot.gameObject.activeSelf != _anchored) _slot.gameObject.SetActive(_anchored);
        if (!_anchored) return;

        Swipe();
        Layout();
        Point();
    }

    // Centred on where the user looks, level, the set distance ahead: the
    // same head pose the table is placed from.
    private bool Anchor()
    {
        if (!ManageSheets.TryGetCameraBasis(out Vector3 eyes, out Quaternion yaw)) return false;
        transform.SetPositionAndRotation(eyes + yaw * new Vector3(0f, -belowEyes, distance),
            yaw * Quaternion.Euler(leanBack, 0f, 0f));
        return true;
    }

    // While the map in the middle is held, it follows the hand and its
    // neighbours follow it; on release they slide on to the next country or
    // back to this one.
    private void Swipe()
    {
        MapSwipe hold = HoldFor(_countries[_index]);
        bool grabbed = hold.IsGrabbed;
        float offsetNow = hold.Offset;

        if (grabbed)
        {
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            if (!_wasGrabbed) { _sliding = false; _velocity = 0f; _lastOffset = offsetNow; }
            _velocity = Mathf.Lerp(_velocity, (offsetNow - _lastOffset) / dt, 0.5f);
            _lastOffset = offsetNow;
            _position = _index - offsetNow / _step;
        }
        else if (_wasGrabbed)
        {
            int target = _index;
            if (offsetNow < -commitShare * _step || _velocity < -flickSpeed) target = _index + 1;
            else if (offsetNow > commitShare * _step || _velocity > flickSpeed) target = _index - 1;
            SlideTo(target);
        }
        _wasGrabbed = grabbed;

        if (_sliding)
        {
            float t = Mathf.Clamp01((Time.time - _slideStart) / Mathf.Max(_slideSeconds, 1e-3f));
            float eased = 1f - Mathf.Pow(1f - t, 3f);
            _position = Mathf.LerpUnclamped(_slideFrom, _slideTo, eased);
            if (t >= 1f)
            {
                _sliding = false;
                _index = Wrap(Mathf.RoundToInt(_slideTo));
                _position = _index;
                Settle();
            }
        }

        // Only the map in the middle can be held, and only while it rests.
        string middle = _countries[_index].name;
        foreach (KeyValuePair<string, MapSwipe> pair in _holds)
            pair.Value.SetHoldable(pair.Key == middle && !_sliding);
    }

    private void SlideTo(float target)
    {
        _slideFrom = _position;
        _slideTo = target;
        _slideStart = Time.time;
        _slideSeconds = slideSeconds * Mathf.Clamp(Mathf.Sqrt(Mathf.Abs(target - _position)), 0.5f, 3f);
        _sliding = true;
    }

    // Each map near the middle stands where the continuous position puts it:
    // the one in the middle at full size, its neighbours smaller and set back
    // at either side, and the rest out of sight. A map in the hand is where
    // the hand has it.
    private void Layout()
    {
        int first = Mathf.FloorToInt(_position) - 1, last = Mathf.CeilToInt(_position) + 1;
        var shown = new HashSet<string>();
        string held = _wasGrabbed ? _countries[_index].name : null;

        for (int k = first; k <= last; k++)
        {
            float d = k - _position;
            if (Mathf.Abs(d) > 1.4f) continue;
            BreachMap.Country country = _countries[Wrap(k)];
            if (!shown.Add(country.name)) continue;

            CountryMap map = MapFor(country);
            if (!map.gameObject.activeSelf) map.gameObject.SetActive(true);
            if (country.name == held) continue;

            float away = Mathf.Clamp01(Mathf.Abs(d));
            map.transform.localPosition = new Vector3(d * _step, 0f, away * 0.08f);
            map.transform.localRotation = Quaternion.identity;
            map.transform.localScale = Vector3.one * Mathf.Lerp(1f, peekScale, away);
        }

        foreach (KeyValuePair<string, CountryMap> pair in _built)
            if (!shown.Contains(pair.Key) && pair.Value.gameObject.activeSelf)
                pair.Value.gameObject.SetActive(false);
    }

    // A fingertip at the map names the dot it is on.
    private void Point()
    {
        bool idle = !_sliding && !_wasGrabbed;
        CountryMap map = idle ? MapFor(_countries[_index]) : null;
        CountryMap.Dot dot = default;
        bool found = false;

        if (map != null)
        {
            Rescan();
            float best = float.MaxValue;
            float face = -map.Thickness - dotHeight;
            foreach ((PokeInteractor poke, IHand hand) in _pokes)
            {
                if (poke == null || !poke.isActiveAndEnabled || hand == null || !hand.IsTrackedDataValid) continue;
                Vector3 local = map.transform.InverseTransformPoint(poke.Origin);
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
        if (!found) return;

        int count = dot.city.breaches;
        _cityLabel.text = $"{dot.city.name}\n<size=80%>{count:#,0} {(count == 1 ? "breach" : "breaches")}</size>";
        _cityLabel.transform.localPosition = new Vector3(dot.centre.x, dot.centre.y + dot.radius + 0.025f,
            -map.Thickness - dotHeight - 0.01f);
    }

    private void Rescan()
    {
        if (Time.unscaledTime < _nextScan && _pokes.Count > 0) return;
        _nextScan = Time.unscaledTime + 2f;
        _pokes.Clear();
        foreach (PokeInteractor poke in FindObjectsByType<PokeInteractor>())
        {
            IHand hand = poke.GetComponentInParent<IHand>();
            if (hand != null) _pokes.Add((poke, hand));
        }
    }

    // The country now in the middle is the one the sheets count.
    private void Settle()
    {
        BreachMap.Country country = _countries[_index];
        int cities = country.cities.Count;
        _title.text = $"{country.name}\n<size=60%>{country.breaches:#,0} {(country.breaches == 1 ? "breach" : "breaches")}" +
                      $" · {cities} {(cities == 1 ? "city" : "cities")}</size>";

        var datasets = Scene.Datasets;
        bool changed = _settled != country.name;
        _settled = country.name;
        if (datasets != null) datasets.SetCountry(country.name);

        string state = $"the map shows {country.name}, and the listed sheets count only breaches at companies " +
                       $"headquartered there ({country.breaches} breaches)";
        // The country it opens on is known, not announced; a swipe is news.
        if (_first) StateChannel.SetState("country", state);
        else if (changed) StateChannel.RecordState("country", state);
        _first = false;
        if (changed) OnCountryChanged?.Invoke(country.name);
    }

    // Slides the maps to a country by name, the short way round the ring.
    // False, with the reason, when there is no map of it.
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

        int n = _countries.Count;
        int delta = ((target - _index) % n + n) % n;
        if (delta > n / 2) delta -= n;
        if (delta == 0) return true;

        SlideTo(_index + delta);
        return true;
    }

    private CountryMap MapFor(BreachMap.Country country)
    {
        if (_built.TryGetValue(country.name, out CountryMap map) && map != null) return map;
        map = CountryMap.Build(_slot, _atlas.Find(country.name), country, _look);
        _built[country.name] = map;
        _holds[country.name] = MapSwipe.Attach(map, smallestHold, thickness + dotHeight, _step * 1.5f);
        return map;
    }

    private MapSwipe HoldFor(BreachMap.Country country)
    {
        MapFor(country);
        return _holds[country.name];
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
    // land's colour.
    private static void Bar(Transform parent, Vector3 centre, Vector3 size, Material material)
    {
        var go = new GameObject("Scale Bar", typeof(MeshFilter), typeof(MeshRenderer), typeof(BoxCollider));
        go.transform.SetParent(parent, false);
        var cube = go.AddComponent<CreateCube>();
        cube.Init(material);
        cube.SetBox(centre, size);
        cube.SetColor(Land);
        go.GetComponent<BoxCollider>().enabled = false;
    }

    private static Color Hex(byte r, byte g, byte b) => new Color32(r, g, b, 255);
}
