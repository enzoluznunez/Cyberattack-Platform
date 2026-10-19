using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Oculus.Interaction.Input;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// The maps, one at a time: the United States first, then its six regions
// west to east. A slider under the map, the Region slider, has a stop for
// each; the user drags its thumb with a fingertip, the maps slide with it, and
// on letting go they settle on the nearest map. Only the map in the middle is
// seen at rest: the others show only while the maps are sliding, the next
// coming in small from the right and the one before from behind the panel.
//
// The maps are a panel of their own, the map panel, opened and closed from
// the wrist as the tool panel is (Watch) and moved on their own: the user
// takes them by hand as they take the tool panel (PanelGrab), one hand to
// carry, two to turn, and they turn to face the user from behind. At launch
// they are open, right of the first sheet, the two centred ahead of the user
// as a pair. Opened again, they
// stand ahead of the user, or right of the tool panel when it is open and in
// reach; the tool panel, opened while they are, stands left of them. The
// sheet is neither's: it stays where it is whichever is open.
//
// Everything on the map is flat, in the panel's own style: the sliders are
// the panel's buttons and bars (FlatSlideBar), moved by a fingertip. They,
// their labels and the scale bar are drawn at the panel's scale and as long
// as the panel is wide and tall, so they look the panel's own beside it
// whatever size the map is drawn.
//
// Every map is drawn in the national map's kilometres, and each region's map
// is the national map cut along its borders, drawn as large as fits a box the
// panel's size. A second, upright slider between the panel and the map, the
// Scale slider, sizes every map alike, from a quarter of that box to two and
// a half times it; a map grows up and away from the panel, its corner by the
// two sliders fixed, so neither slider moves.
//
// Each map holds a sheet on the table, and what is selected on it says which.
// With nothing selected, the map's own: the whole country's on the national
// map, the region's on a region's map. Poking a region on the national map
// selects it and opens its sheet; poking a pin on a region's map selects its
// metro and opens that one's; poking anywhere else on the map selects nothing
// again. What is selected is edged in black. Sliding to a map opens its own
// sheet. When the sheet is switched another way, by the assistant, the map
// follows it: a metro's sheet slides to its region and selects its pin; the
// country's slides to the national map; a region's selects the region on the
// national map if that is the map up, else slides to the region's map. A pin
// and its bars are one colour.
public class MapCarousel : MonoBehaviour
{
    public static MapCarousel Instance { get; private set; }

    [Header("Beside the tool panel")]
    [Tooltip("Space between the panel's right edge and the box the map is drawn in, in metres; the " +
             "Scale slider stands in it, as far from the box as the Region slider stands below it.")]
    public float panelGap = 0.08f;
    [Tooltip("Space between the sheet and the panel while it is open, and between the sheet and the " +
             "Scale slider while it is shut, in metres.")]
    public float sheetGap = 0.05f;

    [Header("Scale")]
    [Tooltip("Width and height of the box each map is fitted into, in metres: the panel's own size, " +
             "so a map is never larger than the panel beside it.")]
    public Vector2 mapBox = new Vector2(0.4f, 0.4f);
    [Tooltip("Radius of a metro with one breach, in metres. A pin's area grows with its count.")]
    public float oneBreachRadius = 0.004f;

    [Header("Size")]
    [Tooltip("The smallest and largest the Scale slider sizes the maps to, against the map box.")]
    public float minScale = 0.25f;
    public float maxScale = 2.5f;
    [Tooltip("Where the Scale slider starts, as a share of its track: 0.5 is midway, " +
             "(minScale + maxScale) / 2.")]
    [Range(0f, 1f)] public float startShare = 0.5f;

    [Header("Slide")]
    [Tooltip("Size of a map coming in from either side, against the one in the middle.")]
    [Range(0.1f, 1f)] public float peekScale = 0.4f;


    // The tool panel's own colours: a white face edged in black over a black
    // back. Only the pins and the national map's regions are coloured; what
    // is selected is edged in the border colour.
    public static readonly Color Land = Style.White;
    public static readonly Color Back = Style.Black;
    public static readonly Color Coast = Style.Black;
    public static readonly Color Border = Style.Black;

    public bool Ready => _areas.Count > 0;

    // The map could not be drawn, and will not be.
    public bool Failed { get; private set; }

    // Every map and its selection edges are made (BuildNext).
    public bool AllBuilt => Ready && _built.Count >= _areas.Count;

    // Whether the maps have been stood in the room, so their transform says
    // where the map is.
    public bool Anchored { get; private set; }

    // Whether the map panel is open: shown, and taking pokes and hands.
    public bool IsOpen => _open;
    private bool _open = true;

    // Whether the maps and the first sheet have been centred on the user as
    // a pair (CentrePair). Until then, or a few seconds, the maps are not
    // shown, so they do not jump into place in view.
    private bool _centred;
    private float _anchoredAt;
    private const float CentreWaitSeconds = 4f;

    // How high the Region slider's bar stands along this transform's Y, in
    // metres: the sheet's base stands level with it. It never moves, however
    // the Scale slider sizes the map.
    public float SliderLevel => -mapBox.y * 0.5f - SliderGap;

    // How far each slider's track stands out from the panel-sized square on
    // the map's corner, in metres: the Region slider below it and the Scale
    // slider left of it alike, so the two frame it as they would the panel.
    private float SliderGap => SliderDrop * _unit;

    // How large every map is drawn, against the map box.
    public float MapScale => _scale;

    private MapAtlas _atlas;
    private BreachMap.Answer _answer;
    private readonly List<MapAtlas.Area> _areas = new List<MapAtlas.Area>();
    private readonly Dictionary<string, Color> _regionColors = new Dictionary<string, Color>();
    private readonly Dictionary<string, RegionMap> _built = new Dictionary<string, RegionMap>();
    private readonly HashSet<string> _shown = new HashSet<string>();

    private RegionMap.Look _look;
    private float _step;

    private Transform _slot;
    // The maps themselves, sized by the Scale slider; the sliders stay their
    // size beside them.
    private Transform _stage;
    private TextMeshPro _title;
    private TextMeshPro _scaleLabel;
    private RectTransform _scaleBar;
    private FlatSlideBar _slider;
    private FlatSlideBar _sizer;
    private TextMeshPro _regionLabel;
    private TextMeshPro _sizeLabel;

    private float _scale = 1f;
    // The panel's own scale at rest: the sliders and labels are drawn at it.
    private float _unit = 1f;
    // The scale, map and panel scale the labels and the scale bar were last
    // laid out for.
    private float _chromeScale = -1f;
    private int _chromeIndex = -1;
    private float _chromeUnit = -1f;
    // The tool panel's top part as it stands, in metres: the sliders' tracks
    // are as long as it is wide and tall.
    private Vector2 _panelSize = Style.Panel * Style.WorldScale;
    private Vector2 _chromePanel;

    // What is selected on the map up: a region ('US West') on the national
    // map, a metro on a region's map, or null for the map's own sheet.
    private string _selected;
    // What was last selected on each map, by its name, so coming back to a
    // map finds it as it was left; none held, nothing.
    private readonly Dictionary<string, string> _remembered = new Dictionary<string, string>();
    // The metros with a sheet: one none of whose breaches reports a figure
    // has none.
    private readonly HashSet<string> _charted = new HashSet<string>();

    // Each metro's region, and which region or charted metro a sheet's
    // payload draws: worked out once when the map loads, so following a
    // sheet switch is a lookup.
    private readonly Dictionary<string, string> _metroRegion = new Dictionary<string, string>();
    private readonly Dictionary<string, string> _regionBySheet = new Dictionary<string, string>();
    private readonly Dictionary<string, string> _metroBySheet = new Dictionary<string, string>();
    // A sheet the map has asked for that is not listed yet, opened once it
    // is.
    private string _pending;

    // Which fingertips were already touching the map last frame: a poke is a
    // fingertip arriving, not one resting there.
    private readonly HashSet<int> _touching = new HashSet<int>();

    // Fingertips hovering in front of the map: only these can poke it, as
    // only a fingertip coming from in front presses a button (PokeRule).
    private readonly HashSet<int> _armed = new HashSet<int>();

    // Taking hold of the map puts the index fingertip on its face as a poke
    // does, so a fingertip on the map counts as a poke only once it has
    // stayed a moment, or lifts off again first, and never if its hand closed
    // to pinch or grip meanwhile or the map is held. Each tip still deciding,
    // with where on the map it last touched.
    private const float PokeDwell = 0.1f;
    private const float GrabbingPinch = 0.5f;
    private readonly Dictionary<int, (float since, Vector3 local)> _deciding =
        new Dictionary<int, (float, Vector3)>();

    private readonly Vector3[] _corners = new Vector3[4];

    // The panel's layout, measured each frame (TryLayout): how far right of
    // its middle the maps stand open and shut, its middle, and the poses the
    // maps take.
    private float _shutAt, _openAt;
    private Vector3 _shutPos, _openPos;
    private Quaternion _layoutRot = Quaternion.identity;
    private Vector3 _layoutMiddle;
    private PanelGrab _grab;

    private int _index;
    private float _position;
    private string _settled;
    private bool _sliding;
    private float _slideFrom, _slideTo, _slideStart, _slideSeconds;
    private bool _settlePending;
    // Whether a hand has the slider, so the maps stand where it puts them.
    private bool _held;

    // How far out from the map's corner both sliders, the two sliders' labels
    // (below it for Region, left of it for Scale) and, below it, the scale
    // bar stand, in metres of the panel at its full size.
    private const float SliderDrop = 0.03f;
    private const float TitleGap = 0.012f;
    private const float AxisLabelGap = 0.06f;
    // The two sliders' labels, against body text; the map's title is 1.6.
    private const float AxisTitleScale = 1.3f;
    private const float ScaleDrop = 0.085f;

    // The scale bar's height, in the panel's units.
    private const float ScaleBarHeight = 8f;


    private void Awake()
    {
        if (Instance == null) Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (ManageDatasets.Instance != null)
        {
            ManageDatasets.Instance.OnActiveDatasetChanged -= OnSheetChanged;
            ManageDatasets.Instance.OnRequestFailed -= OnSheetFailed;
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
            Failed = true;
            Notices.Show(this, "No Map", _atlas == null
                ? "The map outlines are missing from this build."
                : CyberApi.Unreachable);
            return;
        }

        _areas.AddRange(_atlas.Areas);
        foreach (BreachMap.Region region in _answer.regions)
        {
            _regionColors[region.region] = region.color;
            _regionBySheet[ViewCatalog.RegionSheetUrl(region.region)] = region.region;
        }
        foreach (BreachMap.Metro metro in _answer.Metros())
        {
            _metroRegion[metro.name] = metro.region;
            if (metro.charted == 0) continue;
            _charted.Add(metro.name);
            _metroBySheet[ViewCatalog.SheetUrl(metro.name)] = metro.name;
        }
        _scale = Mathf.Lerp(minScale, maxScale, startShare);

        Build();

        StateChannel.SetState("mapPanel", "the map panel is open");

        if (ManageDatasets.Instance != null)
        {
            ManageDatasets.Instance.OnActiveDatasetChanged += OnSheetChanged;
            ManageDatasets.Instance.OnRequestFailed += OnSheetFailed;
        }

        _index = 0;
        _position = _index;
        Settle();
    }

    private void Build()
    {
        _step = mapBox.x * 0.75f;

        // The bars' unlit vertex colour, with the pins and line ends cut to
        // true circles, drawn from both sides: a map's outline runs either
        // way round, and its back is seen from behind.
        Material material = new Material(Resources.Load<Material>("Materials/MapMaterial")) { name = "Map" };
        material.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);

        _look = new RegionMap.Look
        {
            dotRadius = oneBreachRadius,
            // Every black line on the map is the panel's own line, its border
            // and dividers: as wide as them at the panel's full size, and
            // drawn at the panel's scale (RegionMap.SetGrow in Layout).
            coastWidth = Style.SmallBorder * Style.WorldScale,
            borderWidth = Style.SmallBorder * Style.WorldScale,
            maxGrow = 2f / Mathf.Max(minScale, 1e-3f),
            land = Land,
            back = Back,
            coast = Coast,
            border = Border,
            material = material
        };

        // A panel of its own in the room, its size real metres.
        transform.SetParent(null, false);
        transform.localScale = Vector3.one;
        _grab = PanelGrab.Attach(gameObject);

        _slot = new GameObject("Slot").transform;
        _slot.SetParent(transform, false);
        _slotShown = true;

        _stage = new GameObject("Maps").transform;
        _stage.SetParent(_slot, false);

        _title = Label(_slot, "Title", Style.TitleFont, 1.6f);
        // It stands on the map's top edge, growing upward from there.
        _title.alignment = TextAlignmentOptions.Bottom;
        _title.rectTransform.pivot = new Vector2(0.5f, 0f);

        // A stop for each map, running east under the map, centred across
        // its box and as long as the panel is wide, flat on the map's plane
        // level with its face.
        _slider = FlatSlideBar.Create(_slot, "Region Slider");
        _slider.SetLength(Style.Panel.x);
        _slider.OnSlide += OnSlide;
        _slider.OnRelease += OnRelease;
        _regionLabel = Label(_slot, "Region Label", Style.BodyFont, AxisTitleScale);
        _regionLabel.text = "Region";

        // The same slider stood upright in the gap between the panel and the
        // map, centred up its box and as long as the panel is tall: turned
        // a quarter, its track runs up and its thumb stands on end. Low is
        // small, high is large.
        _sizer = FlatSlideBar.Create(_slot, "Scale Slider");
        _sizer.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
        _sizer.SetLength(Style.Panel.y);
        _sizer.OnSlide += OnSize;
        _sizer.OnRelease += OnSize;
        // Its label stands upright beside it as a chart's y title does,
        // reading up.
        _sizeLabel = Label(_slot, "Scale Label", Style.BodyFont, AxisTitleScale);
        _sizeLabel.text = "Scale";
        _sizeLabel.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);

        // The two sliders' tracks, drawn here rather than by each slider:
        // two rounded bars apart from one another, each with its own outline,
        // one along the bottom of the map and one up its side. Each image is
        // laid out in Chrome.
        _axes = FlatSlideBar.FlatCanvas(_slot, "Axes");
        float radius = Style.RadiusFor(FlatSlideBar.TrackHeight);
        for (int i = 0; i < 2; i++) _axisBacks[i] = AxisImage(_axes, "Back", radius, Style.Black, back: true);
        for (int i = 0; i < 2; i++) _axisFrames[i] = AxisImage(_axes, "Frame", radius, Style.Black);
        for (int i = 0; i < 2; i++)
            _axisFills[i] = AxisImage(_axes, "Fill", Style.InnerRadius(radius), Style.White);

        // The scale bar, flat as the sliders' tracks are, in the land's
        // colour. Its length is set for each map as it settles.
        RectTransform bar = FlatSlideBar.FlatCanvas(_slot, "Scale Bar");
        _scaleBar = FlatSlideBar.Pill(bar, "Bar", new Vector2(0f, ScaleBarHeight), Land);
        _scaleLabel = Label(_slot, "Scale Bar Label", Style.BodyFont, 1f);
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

        bool shown = Place() && _open &&
                     (_centred || Time.unscaledTime - _anchoredAt > CentreWaitSeconds);
        ShowSlot(shown);
        _grab.Allowed = shown;

        if (_pending != null && TryOpen(_pending)) _pending = null;

        // Every map and every edge it draws is made at startup, a map a frame,
        // whether or not the panel is open, so nothing is made on first open.
        if (!_sliding && !_held) BuildNext();

        if (!shown)
        {
            // Before the maps are stood: a slide under way lands at once, so
            // the sheet still follows it, and a slider let go of lands on the
            // map nearest where it was.
            if (_held)
            {
                _held = false;
                _slideTo = Mathf.Clamp(Mathf.RoundToInt(_position), 0, _areas.Count - 1);
                FinishSlide();
            }
            else if (_sliding) FinishSlide();
            FinishScaling();
            _touching.Clear();
            _deciding.Clear();
            _armed.Clear();
            return;
        }

        Slide();
        StepScaling();
        if (!_sizer.Held) _sizer.Rest(Mathf.InverseLerp(minScale, maxScale, _scale));
        Layout();
        Chrome();
        Poke();
    }

    // Hides or shows the map panel without switching anything off, so opening
    // it never rebuilds it: what it draws stops or starts drawing, and its
    // sliders stop or start taking fingertips.
    private void ShowSlot(bool on)
    {
        if (on == _slotShown) return;
        _slotShown = on;
        _slot.GetComponentsInChildren(true, _renderers);
        for (int i = 0; i < _renderers.Count; i++) _renderers[i].enabled = on;
        _slot.GetComponentsInChildren(true, _canvases);
        for (int i = 0; i < _canvases.Count; i++) _canvases[i].enabled = on;
        // The loop above reached every map's renderers; only those Layout
        // stands up are drawn.
        foreach (KeyValuePair<string, RegionMap> pair in _built)
            if (pair.Value != null) pair.Value.SetVisible(on && _shown.Contains(pair.Key));
        _slider.enabled = on;
        _sizer.enabled = on;
    }

    private bool _slotShown;
    private readonly List<Renderer> _renderers = new List<Renderer>();
    private readonly List<Canvas> _canvases = new List<Canvas>();

    // Makes one map that has not been made yet, nearest the middle first, a
    // frame at a time while the maps rest: the slider can pass every map, so
    // a slide never waits on one being built.
    private void BuildNext()
    {
        if (_built.Count >= _areas.Count) return;
        for (int k = 1; k < _areas.Count; k++)
        {
            for (int side = 0; side < 2; side++)
            {
                int at = side == 0 ? _index + k : _index - k;
                if (at < 0 || at >= _areas.Count) continue;
                MapAtlas.Area area = _areas[at];
                if (_built.TryGetValue(area.name, out RegionMap built) && built != null) continue;
                MapFor(area);
                return;
            }
        }
    }

    // Stands the maps ahead of the user beside the shut panel the first time
    // there is a panel to measure and the head is settled where it is (the
    // sheet and the map open ahead of the user, with the panel shut between
    // them), and from then on leaves them to the user. False until they are
    // stood.
    private bool Place()
    {
        if (!Anchored && HeadPose.Settled && LoadingPhase.Done)
        {
            ToolPanelUI panel = Scene.ToolPanel;
            if (panel != null && !panel.IsVisible) panel.PlaceAhead();
            if (TryLayout(panel))
            {
                transform.SetPositionAndRotation(_shutPos, _layoutRot);
                Anchored = true;
                _anchoredAt = Time.unscaledTime;
                Recenter.NoteHome();
                Transform cam = CameraRig.MainTransform;
                if (cam != null)
                    Debug.Log($"[MapCarousel] Stood the layout with the head {cam.localPosition.y:F2} m above " +
                              $"the floor origin, {cam.position.y:F2} m in the world.");
            }
        }
        else if (Anchored) TryLayout(Scene.ToolPanel);
        return Anchored;
    }

    // Opens or closes the map panel. Opened, it stands ahead of the user, or
    // right of the tool panel when that is open and in reach.
    public void SetOpen(bool open)
    {
        if (open == _open) return;
        _open = open;
        if (open && Anchored) PlaceAhead();
        StateChannel.RecordState("mapPanel", open
            ? "the map panel is open"
            : "the map panel is closed; the sheet stays up, and the map still follows what it shows");
    }

    public void Toggle() => SetOpen(!_open);

    private void PlaceAhead()
    {
        ToolPanelUI panel = Scene.ToolPanel;
        if (panel != null && panel.IsVisible && InReach(panel.transform.position) && TryLayout(panel))
        {
            transform.SetPositionAndRotation(_openPos, _layoutRot);
            return;
        }

        Transform cam = CameraRig.MainTransform;
        if (cam == null) return;
        Vector3 forward = CameraRig.FlatForward;
        Quaternion rot = Quaternion.LookRotation(forward);
        float ahead = panel != null ? panel.zOffsetFromCamera : 0.45f;
        // Its middle, sliders, title and all, straight ahead.
        transform.SetPositionAndRotation(cam.position + forward * ahead - rot * _grab.Center, rot);
    }

    // Stands the tool panel, just opened, left of the open map panel as the
    // two stand side by side, turned as the map is: the mirror of the map
    // opening right of an open tool panel (PlaceAhead), by the same test of
    // whether the other is there. False, leaving it ahead of the user where
    // it opened, when the map panel is closed or out of reach. Put there at
    // once: panels never slide into place.
    public bool PlacePanelBeside(ToolPanelUI panel)
    {
        if (panel == null || !_open || !Anchored || !InReach(transform.position)) return false;
        if (!TryLayout(panel)) return false;

        Transform p = panel.transform;
        Quaternion turn = transform.rotation * Quaternion.Inverse(_layoutRot);
        p.SetPositionAndRotation(_layoutMiddle + turn * (p.position - _layoutMiddle), turn * p.rotation);
        if (!TryLayout(panel)) return false;
        p.position += transform.position - _openPos;
        TryLayout(panel);
        return true;
    }

    // Near enough ahead of the user that opening beside it keeps the two
    // together in front of them.
    private static bool InReach(Vector3 point)
    {
        Transform cam = CameraRig.MainTransform;
        if (cam == null) return false;
        Vector3 to = point - cam.position;
        return to.magnitude <= ReachMetres &&
               Vector3.Dot(CameraRig.FlatForward, to) > 0f;
    }

    private const float ReachMetres = 1.5f;

    // Where the panel's layout puts the maps, measured off the panel's top
    // part, the part that never changes size, in its plane and facing the way
    // it faces: open, right of the panel past a gap; shut, close up to its
    // middle with the sheet the other side of the Scale slider. The panel's
    // layout holds while it is shut, so it still says where the panel stands.
    // False with no panel.
    private bool TryLayout(ToolPanelUI panel)
    {
        RectTransform body = panel != null ? panel.PrimaryRect : null;
        if (body == null) return false;

        // Bottom left, top left, top right, bottom right; while a hand holds
        // the panel it swells, which is undone.
        body.GetWorldCorners(_corners);
        float rest = Mathf.Max(panel.RestScale.x, 1e-4f);
        float swell = Mathf.Max(panel.transform.localScale.x / rest, 1e-4f);
        Vector3 right = _corners[3] - _corners[0];
        Vector3 up = _corners[1] - _corners[0];
        Vector3 middle = (_corners[0] + _corners[1] + _corners[2] + _corners[3]) * 0.25f;
        _layoutMiddle = middle;

        _unit = rest;
        _panelSize = new Vector2(right.magnitude, up.magnitude) / swell;
        float half = _panelSize.x * 0.5f;
        float sizerHalf = FlatSlideBar.ThumbSize.y * Style.WorldScale * rest * 0.5f;
        _shutAt = sheetGap * 0.5f + sizerHalf + SliderGap + mapBox.x * 0.5f;
        _openAt = half + panelGap + mapBox.x * 0.5f;

        right.Normalize();
        up.Normalize();
        _layoutRot = Quaternion.LookRotation(Vector3.Cross(right, up), up);
        _shutPos = middle + right * _shutAt;
        _openPos = middle + right * _openAt;
        return true;
    }

    // Centres the maps and the first sheet, 'sheetWidth' metres wide, on the
    // user as one pair where the shut panel was stood ahead of them: the map
    // panel, the gap and the sheet together, across and up. Only once,
    // before the sheet first stands beside them.
    public void CentrePair(float sheetWidth)
    {
        if (_centred || !Anchored || !TryLayout(Scene.ToolPanel)) return;
        _centred = true;
        Chrome();

        float mapLeft = _grab.Center.x - _grab.Size.x * 0.5f;
        float sheetRight = SheetLeft + sheetWidth;
        var middle = new Vector3((mapLeft + sheetRight) * 0.5f, _grab.Center.y, 0f);
        transform.SetPositionAndRotation(_layoutMiddle - _layoutRot * middle, _layoutRot);
    }

    // Where a sheet's left edge stands along this transform's X: a gap right
    // of everything the map panel draws, its Region slider's track, scale bar
    // and map up at the size it is drawn.
    private float SheetLeft => _grab.Center.x + _grab.Size.x * 0.5f + sheetGap;

    // Where a sheet, 'sheetWidth' metres wide, opens: its frame, its right
    // edge, so that its left edge is a gap right of the map panel wherever
    // the maps stand now, shown or not, level with the Region slider's bar.
    // False until the maps are stood.
    public bool TrySheetFrame(float sheetWidth, out Vector3 pos, out Quaternion rot)
    {
        pos = Vector3.zero;
        rot = Quaternion.identity;
        if (!Ready || !Anchored) return false;

        Chrome();
        pos = transform.TransformPoint(new Vector3(SheetLeft + sheetWidth, SliderLevel, 0f));
        Vector3 flat = Vector3.ProjectOnPlane(transform.right, Vector3.up);
        if (flat.sqrMagnitude < 1e-6f) return false;
        rot = Quaternion.LookRotation(Vector3.Cross(flat.normalized, Vector3.up), Vector3.up);
        return true;
    }

    // Let go on a map, what was selected on it last is selected again, and
    // its sheet opens: the map's own when nothing was.
    private void OnSlide(float share)
    {
        if (!Ready) return;
        if (!_held) Supersede();
        _held = true;
        _sliding = false;
        _position = share * (_areas.Count - 1);
    }

    // The map a slide under way lands on, or the one up.
    private int Headed => _sliding ? Mathf.RoundToInt(_slideTo) : _index;

    private void OnRelease(float share)
    {
        if (!Ready || !_held) return;
        _held = false;
        _position = share * (_areas.Count - 1);
        int target = Mathf.Clamp(Mathf.RoundToInt(_position), 0, _areas.Count - 1);
        SlideTo(target);
    }

    // The Scale slider's thumb sizes every map as it moves: its low end a
    // quarter of the map box, its high end two and a half times it, evenly
    // between.
    private void OnSize(float share)
    {
        if (!Ready) return;
        _scaling = false;
        _scale = Mathf.Lerp(minScale, maxScale, share);
    }

    // Sizes every map, as the Scale slider does: 1 is the map box. False,
    // with the reason, while a hand has the slider.
    public bool SetScale(float scale, out string reason)
    {
        reason = null;
        if (!Ready) { reason = "The map has not loaded."; return false; }
        if (_sizer.Held) { reason = "The user is holding the map's Scale slider."; return false; }

        // The assistant's resize plays out at its speed, the maps growing or
        // shrinking and the thumb riding along, and lands at once at Instant.
        float target = Mathf.Clamp(scale, minScale, maxScale);
        // The map grows from its corner by the sliders, so its far corner,
        // the point that moves furthest, travels the box's diagonal.
        float seconds = AgentSeconds(Mathf.Abs(target - _scale) * mapBox.magnitude * Mathf.Abs(transform.lossyScale.x));
        if (seconds > 0f)
        {
            _scaleFrom = _scale;
            _scaleTo = target;
            _scaleStart = Time.time;
            _scaleSeconds = seconds;
            _scaling = true;
        }
        else
        {
            _scaling = false;
            _scale = target;
        }
        return true;
    }

    private bool _scaling;
    private float _scaleFrom, _scaleTo, _scaleStart, _scaleSeconds;

    // Whether the slide under way is the assistant's.
    private bool _agentSlide;

    // The assistant's motion on the maps, a resize or a slide, put where it
    // ends, as the sheet's is when the user takes over (AgentTurn). A slide
    // the user set going is theirs and runs on.
    public void CompleteAgentMotion()
    {
        FinishScaling();
        if (_sliding && _agentSlide) FinishSlide();
    }

    private void StepScaling()
    {
        if (!_scaling) return;
        // A hand on the slider takes over from the assistant.
        if (_sizer.Held) { _scaling = false; return; }
        float t = Mathf.Clamp01((Time.time - _scaleStart) / Mathf.Max(_scaleSeconds, 1e-3f));
        _scale = Mathf.Lerp(_scaleFrom, _scaleTo, Style.EaseOut(t));
        if (t >= 1f) _scaling = false;
    }

    private void FinishScaling()
    {
        if (!_scaling) return;
        _scaling = false;
        _scale = _scaleTo;
    }

    // How long the assistant's motion over 'meters' takes at its speed: zero
    // at Instant, and -1 when it is not the assistant moving the maps.
    private static float AgentSeconds(float meters)
    {
        ManageSheets sheets = Scene.Sheets;
        return sheets != null ? sheets.AgentMotionSeconds(meters) : -1f;
    }

    // A slide under way runs on; the thumb, when no hand has it, stands
    // where the maps are.
    private void Slide()
    {
        if (_sliding)
        {
            float t = Mathf.Clamp01((Time.time - _slideStart) / Mathf.Max(_slideSeconds, 1e-3f));
            float eased = Style.EaseOut(t);
            _position = Mathf.LerpUnclamped(_slideFrom, _slideTo, eased);
            if (t >= 1f) FinishSlide();
        }
        _slider.Rest(_areas.Count > 1 ? _position / (_areas.Count - 1) : 0f);
    }

    // A slide takes the speed of whoever set it going over the distance the
    // maps travel: the user's motion speed for theirs, the assistant's for
    // its own, at once when that is Instant.
    private void SlideTo(float target)
    {
        Supersede();
        float meters = _step * Mathf.Abs(target - _position) * Mathf.Abs(transform.lossyScale.x);
        float agent = AgentSeconds(meters);
        _slideTo = target;
        _agentSlide = agent > 0f;
        if (agent == 0f)
        {
            FinishSlide();
            return;
        }
        _slideFrom = _position;
        _slideStart = Time.time;
        ManageSheets sheets = Scene.Sheets;
        _slideSeconds = agent > 0f ? agent
            : sheets != null ? sheets.UserMotionSeconds(meters)
            : 0.35f;
        _sliding = true;
    }

    // The maps are on the move: a sheet asked for before, still being read
    // or not yet listed, is no longer wanted, so landing late it cannot
    // select itself on the map they move to. The map they land on opens its
    // own.
    private void Supersede()
    {
        _pending = null;
        if (Scene.Datasets != null) Scene.Datasets.CancelRequest();
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
        // The maps grow up and away from the panel: the box's corner by the
        // two sliders, the edge nearest the panel and its bottom, stays where
        // it is.
        _stage.localPosition = new Vector3((_scale - 1f) * mapBox.x * 0.5f, (_scale - 1f) * mapBox.y * 0.5f, 0f);
        _stage.localScale = Vector3.one * _scale;
        // The lines, edges and the depth between the layers keep their size
        // however large the maps are drawn, so they stay crisp: the panel's
        // own, at the panel's scale.
        RegionMap.SetGrow(_look.material, _unit / _scale);

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
            if (map.Visible != _slotShown) map.SetVisible(_slotShown);


            float away = Mathf.Clamp01(Mathf.Abs(d));
            map.transform.localPosition = new Vector3(d * _step, 0f, away * 0.08f);
            map.transform.localRotation = Quaternion.identity;
            map.transform.localScale = Vector3.one * Mathf.Lerp(1f, peekScale, away);
        }

        foreach (KeyValuePair<string, RegionMap> pair in _built)
            if (!_shown.Contains(pair.Key) && pair.Value != null && pair.Value.Visible)
                pair.Value.SetVisible(false);
    }

    // The title, the sliders and the scale bar, kept at the map's edges as
    // the Scale slider sizes it, drawn at the panel's scale, and the scale
    // bar measured for the map up.
    private void Chrome()
    {
        if (Mathf.Approximately(_chromeScale, _scale) && _chromeIndex == _index &&
            Mathf.Approximately(_chromeUnit, _unit) && _chromePanel == _panelSize) return;
        _chromeScale = _scale;
        _chromeIndex = _index;
        _chromeUnit = _unit;
        _chromePanel = _panelSize;

        // The map's bottom and its edge nearest the panel never move, so the
        // sliders, their labels and the scale bar stand from that corner and
        // stay where they are; only the title rides on the map's top. A
        // millimetre of the panel is 'mm' metres here.
        float mm = Style.WorldScale * _unit;
        float bottom = -mapBox.y * 0.5f;
        float left = -mapBox.x * 0.5f;
        float middle = (_scale - 1f) * mapBox.x * 0.5f;
        // On the top of the map as drawn, which a wide map leaves below the
        // top of its box.
        float drawnTop = (_scale - 1f) * mapBox.y * 0.5f + MapFor(_areas[_index]).Size.y * _scale * 0.5f;
        _title.transform.localPosition = new Vector3(middle, drawnTop + TitleGap * _unit, RegionMap.FaceZ);
        _title.transform.localScale = Vector3.one * (Style.WorldTextScale * 1.6f * _unit);

        // The two tracks are the map's axes (Axes), apart from one another: the
        // Region track a slider's gap below the map, as long as the panel is
        // wide and centred across the map's box at its own size, and the Scale
        // track a gap left of it, as long as the panel is tall and centred up
        // that box. The box's middle is this transform's origin, so neither
        // moves as the Scale slider sizes the map.
        float sizerX = left - SliderGap;
        float regionHalf = _panelSize.x * 0.5f;
        float sizerHalf = _panelSize.y * 0.5f;
        float half = FlatSlideBar.TrackHeight * mm * 0.5f;
        _axes.localScale = Vector3.one * mm;
        _axes.localPosition = new Vector3(0f, 0f, RegionMap.FaceZ);
        LayAxis(0, new Vector2(-regionHalf, SliderLevel - half), new Vector2(regionHalf, SliderLevel + half), mm);
        LayAxis(1, new Vector2(sizerX - half, -sizerHalf), new Vector2(sizerX + half, sizerHalf), mm);

        // Each thumb travels the whole of its track, end to end, and each
        // label stands at its track's middle.
        _slider.SetUnit(_unit);
        _slider.SetLength(_panelSize.x / mm);
        _slider.transform.localPosition = new Vector3(0f, SliderLevel, RegionMap.FaceZ);
        _regionLabel.transform.localPosition = new Vector3(0f, bottom - AxisLabelGap * _unit, RegionMap.FaceZ);
        _regionLabel.transform.localScale = Vector3.one * (Style.WorldTextScale * AxisTitleScale * _unit);

        _sizer.SetUnit(_unit);
        _sizer.SetLength(_panelSize.y / mm);
        _sizer.transform.localPosition = new Vector3(sizerX, 0f, RegionMap.FaceZ);
        // Halfway up its track and as far left of it as the Region label
        // stands below its own.
        _sizeLabel.transform.localPosition = new Vector3(left - AxisLabelGap * _unit, 0f, RegionMap.FaceZ);
        _sizeLabel.transform.localScale = Vector3.one * (Style.WorldTextScale * AxisTitleScale * _unit);

        float metresPerKm = MetresPerKm(_areas[_index]) * _scale;
        float barKm = NiceLength(0.15f * mapBox.x / metresPerKm);
        float barMetres = barKm * metresPerKm;
        float barY = bottom - ScaleDrop * _unit;
        RectTransform barCanvas = (RectTransform)_scaleBar.parent;
        barCanvas.localScale = Vector3.one * mm;
        barCanvas.localPosition = new Vector3(left + barMetres * 0.5f, barY, RegionMap.FaceZ);
        _scaleBar.sizeDelta = new Vector2(barMetres / mm, ScaleBarHeight);
        _scaleLabel.transform.localPosition = new Vector3(left + barMetres + 0.01f * _unit, barY, RegionMap.FaceZ);
        _scaleLabel.transform.localScale = Vector3.one * (Style.WorldTextScale * _unit);
        // Retexted only when the length changes, not on every frame of a resize.
        if (barKm != _scaleBarKm)
        {
            _scaleBarKm = barKm;
            _scaleLabel.text = $"{barKm:#,0.##} km";
        }

        // A hand takes the map panel anywhere on what is drawn: the map up,
        // at the size it is drawn, its title, the two sliders with their
        // labels and the scale bar. Measured again whenever the map or its
        // size changes, so the box a hand takes and the middle two hands
        // turn it about fit the panel as it is.
        float thumbHalf = FlatSlideBar.ThumbSize.y * mm * 0.5f;
        Vector2 drawn = MapFor(_areas[_index]).Size * _scale;
        Vector2 centre = new Vector2(middle, (_scale - 1f) * mapBox.y * 0.5f);
        float boxLeft = Mathf.Min(left - (AxisLabelGap + 0.015f) * _unit, centre.x - drawn.x * 0.5f,
            -regionHalf - thumbHalf) - 0.01f;
        float boxRight = Mathf.Max(regionHalf + thumbHalf, centre.x + drawn.x * 0.5f,
            left + barMetres + 0.06f * _unit) + 0.01f;
        float boxBottom = Mathf.Min(barY - 0.015f * _unit, -sizerHalf - thumbHalf - 0.01f);
        float boxTop = Mathf.Max(centre.y + drawn.y * 0.5f, _title.transform.localPosition.y + 0.06f * _unit,
            sizerHalf + thumbHalf + 0.01f);
        _grab.SetBox(new Vector3((boxLeft + boxRight) * 0.5f, (boxBottom + boxTop) * 0.5f, RegionMap.FaceZ),
            new Vector3(boxRight - boxLeft, boxTop - boxBottom, 0.03f));
    }

    // A fingertip arriving at the map's face pokes what it lands on: on the
    // national map a region, which selects it; on a region's map one of the
    // region's own pins, which selects its metro; anywhere else on the map,
    // nothing, which opens the map's own sheet. Only the map at rest in the
    // middle can be poked.
    private void Poke()
    {
        bool idle = !_sliding && !_held;
        RegionMap map = idle ? MapFor(_areas[_index]) : null;
        IReadOnlyList<Fingertips.Source> tips = Fingertips.All;
        // The map's own units are the map box's; the reach of a fingertip is
        // the hand's, whatever size the map is drawn, and so is how far the
        // face stands from the back (RegionMap.SetGrow).
        float slack = PokeRule.EdgeSlack / _scale, face = RegionMap.FaceZ * _unit / _scale;
        float hover = PokeRule.HoverDepth / _scale, press = PokeRule.PressDepth / _scale;
        float behind = PokeRule.BehindDepth / _scale;

        // The side the viewer is on is the map's front, whichever way it faces.
        Transform cam = CameraRig.MainTransform;
        float front = map != null && cam != null && map.transform.InverseTransformPoint(cam.position).z < face
            ? -1f : 1f;

        float now = Time.unscaledTime;

        for (int i = 0; i < tips.Count; i++)
        {
            Fingertips.Source source = tips[i];
            bool touching = false;
            Vector3 local = default;
            // A fingertip on a slider is the slider's: sliding off its end onto
            // the map is not a poke.
            if (_slider.Touches(i) || _sizer.Touches(i))
            {
                _touching.Add(i);
                _deciding.Remove(i);
                continue;
            }
            bool pressing = false, hovering = false;
            if (map != null && source.Live)
            {
                local = map.transform.InverseTransformPoint(source.poke.Origin);
                bool over = Mathf.Abs(local.x) <= mapBox.x * 0.5f && Mathf.Abs(local.y) <= mapBox.y * 0.5f;
                float toward = (local.z - face) * front;
                pressing = over && toward <= press && toward >= -behind;
                hovering = over && toward > press && toward <= hover;
            }
            // Only a fingertip that came from in front presses; one already
            // pressing stays pressing however it moves on the map.
            touching = pressing && (_touching.Contains(i) || _armed.Contains(i));
            if (hovering) _armed.Add(i);
            else if (!pressing) _armed.Remove(i);

            bool grabbing = _grab.Held ||
                            (source.Live && source.hand.GetFingerPinchStrength(HandFinger.Index) >= GrabbingPinch);

            if (!touching)
            {
                _touching.Remove(i);
                // A quick tap is in and out before the dwell is up.
                if (_deciding.TryGetValue(i, out var tap))
                {
                    _deciding.Remove(i);
                    if (map != null && !grabbing) PokeAt(map, tap.local, slack);
                }
                continue;
            }

            if (_touching.Add(i)) _deciding[i] = (now, local);
            if (!_deciding.TryGetValue(i, out var held)) continue;

            // Closing the hand on the map makes this touch a grab, not a poke,
            // until the fingertip lifts off.
            if (grabbing) { _deciding.Remove(i); continue; }

            if (now - held.since >= PokeDwell)
            {
                _deciding.Remove(i);
                PokeAt(map, local, slack);
            }
            else _deciding[i] = (held.since, local);
        }
    }

    // A poke on a region or a pin clicks as a button does, and on the one
    // already selected takes the selection off again; one on the map around
    // them only clears what was selected. With nothing selected, the map's
    // own whole sheet is the one open.
    private void PokeAt(RegionMap map, Vector3 local, float slack)
    {
        if (map.Area.IsNational)
        {
            bool onRegion = map.TryRegionAt(local, out string region);
            if (onRegion) UIPressFeedback.Play();
            Select(onRegion && region != _selected ? region : null);
        }
        else if (map.TryDotAt(local, slack, out RegionMap.Dot dot) && dot.region == map.Area.region)
        {
            // A metro none of whose breaches reports a figure has no
            // sheet; poking it says so and leaves the sheet as it was.
            UIPressFeedback.Play();
            if (dot.name == _selected) Select(null);
            else if (_charted.Contains(dot.name)) Select(dot.name);
            else Notices.Show(this, "No Figures",
                $"None of the breaches in {dot.name} reported a figure, so it has no sheet.");
        }
        else Select(null);
    }

    // Selects a region on the national map, a metro on a region's map, or
    // with null nothing: edges it in black, says so on the title, and opens
    // its sheet.
    private void Select(string selected, bool open = true)
    {
        MapAtlas.Area area = _areas[_index];
        bool changed = _selected != selected;
        _selected = selected;
        Remember(area, selected);
        MapFor(area).Select(selected);
        Title(area);
        if (open) Open(SheetFor(area, selected));
        if (changed) StateChannel.SetState("map", MapState(area));
    }

    // The sheet a map opens with something selected on it, or nothing.
    private static string SheetFor(MapAtlas.Area area, string selected)
    {
        if (area.IsNational)
            return selected != null ? ViewCatalog.RegionSheetUrl(selected) : ViewCatalog.UsSheetUrl();
        return selected != null ? ViewCatalog.SheetUrl(selected) : ViewCatalog.RegionSheetUrl(area.region);
    }

    // Opens a listed sheet now, or once it is listed: the catalog and the map
    // are both drawn from /map and either may be ready first.
    private void Open(string payload)
    {
        _pending = null;
        if (!TryOpen(payload)) _pending = payload;
    }

    private static bool TryOpen(string payload)
    {
        ManageDatasets datasets = Scene.Datasets;
        int index = datasets != null ? datasets.IndexOf(payload) : -1;
        if (index < 0) return false;
        // Asked even when it is the one open: that cancels a read still under
        // way for another.
        datasets.SwitchDataset(index);
        return true;
    }

    // The map's name, its breaches and what is selected on it.
    private void Title(MapAtlas.Area area)
    {
        int breaches = 0;
        foreach (BreachMap.Region region in _answer.regions)
            if (area.IsNational || region.region == area.region) breaches += region.breaches;
        string count = $"{Formatter.Count(breaches)} {(breaches == 1 ? "breach" : "breaches")}";
        string shown = area.IsNational ? ViewCatalog.Title(_selected) : _selected;
        _title.text = string.IsNullOrEmpty(shown)
            ? $"{area.name}\n<size=60%>{count}</size>"
            : $"{area.name}\n<size=60%>{count} · showing {shown}</size>";
    }

    private string MapState(MapAtlas.Area area)
    {
        if (area.IsNational)
            return "the map shows the whole United States, its regions in their colours; " +
                   (_selected != null
                       ? $"the {ViewCatalog.Title(_selected)} region is selected, edged in black, and its sheet is on the table"
                       : "nothing is selected and the whole country's sheet is on the table") +
                   "; poking a region opens that region's sheet, and poking anywhere else the country's";
        return $"the map shows the {area.name} region, its pins coloured by metro; " +
               (_selected != null
                   ? $"the {_selected} metro's pin is selected, edged in black, and its sheet is on the table"
                   : "nothing is selected and the whole region's sheet is on the table") +
               "; poking a pin opens that metro's sheet, and poking anywhere else the region's";
    }

    // The map now in the middle opens its sheet: what was last selected on
    // it, a metro of its region or a region on the national map, or its own.
    private void Settle()
    {
        MapAtlas.Area area = _areas[_index];
        Chrome();

        bool first = _settled == null;
        bool changed = _settled != area.name;
        _settled = area.name;

        _selected = Remembered(area);
        MapFor(area).Select(_selected);
        Title(area);
        Open(SheetFor(area, _selected));

        // The map it opens on is known, not announced; a slide is news.
        string state = MapState(area);
        if (first) StateChannel.SetState("map", state);
        else if (changed) StateChannel.RecordState("map", state);
    }

    private string Remembered(MapAtlas.Area area)
    {
        if (!_remembered.TryGetValue(area.name, out string selected)) return null;
        return Holds(area, selected) ? selected : null;
    }

    // Edged on its map at once, so a slide past or onto it shows what it
    // will land on.
    private void Remember(MapAtlas.Area area, string selected)
    {
        if (selected == null) _remembered.Remove(area.name);
        else _remembered[area.name] = selected;
        if (_built.TryGetValue(area.name, out RegionMap map) && map != null) map.Select(selected);
    }

    // Whether a selection belongs on a map: a region on the national map, one
    // of the region's metros on its map.
    private bool Holds(MapAtlas.Area area, string selected)
    {
        if (selected == null) return false;
        if (area.IsNational) return _regionColors.ContainsKey(selected);
        return MetroRegion(selected) == area.region;
    }

    // The region a metro is in, or null.
    private string MetroRegion(string metro) =>
        !string.IsNullOrEmpty(metro) && _metroRegion.TryGetValue(metro, out string region) ? region : null;

    // A sheet switched another way, by the assistant, brings the map with it.
    private void OnSheetChanged(int index)
    {
        if (!Ready) return;
        ManageDatasets datasets = Scene.Datasets;
        if (datasets == null || index < 0 || index >= datasets.DatasetCount) return;

        string payload = datasets.Datasets[index].payload;
        if (payload == null) return;
        int headed = Headed;
        int target;
        string selected;

        if (payload == ViewCatalog.UsSheetUrl())
        {
            target = 0;
            selected = null;
        }
        else if (_regionBySheet.TryGetValue(payload, out string region))
        {
            if (_areas[headed].IsNational)
            {
                target = headed;
                selected = region;
            }
            else
            {
                target = _areas.FindIndex(a => a.region == region);
                selected = null;
            }
        }
        else if (_metroBySheet.TryGetValue(payload, out string metro))
        {
            string metroRegion = MetroRegion(metro);
            target = _areas.FindIndex(a => a.region == metroRegion);
            selected = metro;
        }
        else return;
        if (target < 0) return;

        // The sheet is open already.
        _pending = null;
        if (target == headed && !_sliding && !_held)
        {
            if (_selected != selected) Select(selected, false);
            return;
        }
        // Landing, the map selects what it remembers: this.
        Remember(_areas[target], selected);
        GoTo(target);
    }

    // A selection whose sheet would not read is taken back: the map goes back
    // to showing what is actually on the table.
    private void OnSheetFailed(string payload)
    {
        if (_pending == payload) _pending = null;
        if (!Ready || payload != SheetFor(_areas[_index], _selected)) return;

        ManageDatasets datasets = Scene.Datasets;
        int active = datasets != null ? datasets.ActiveIndex : -1;
        if (active >= 0) OnSheetChanged(active);
        if (payload == SheetFor(_areas[_index], _selected)) Select(null, false);
    }

    // Slides the maps to one by name ('United States', 'West', or 'US West'),
    // through the maps between, or, before they are stood,
    // goes straight there; as the slider does, it opens with what was last
    // selected on it, or with nothing and its own sheet. False, with the
    // reason, when there is no such map.
    public bool Show(string name, out string reason)
    {
        reason = null;
        if (!Ready) { reason = "The map has not loaded."; return false; }

        int target = _areas.IndexOf(_atlas.Find(name?.Trim()));
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

        int from = Headed;
        if (target == from)
        {
            if (!_sliding) Open(SheetFor(_areas[_index], _selected));
            return true;
        }
        GoTo(target);
        return true;
    }

    // Slides to a map, or, before the maps are stood, goes
    // straight there.
    private void GoTo(int target)
    {
        if (!_slotShown)
        {
            _sliding = false;
            _index = target;
            _position = _index;
            Settle();
            return;
        }
        SlideTo(target);
    }

    private RegionMap MapFor(MapAtlas.Area area)
    {
        if (_built.TryGetValue(area.name, out RegionMap built) && built != null) return built;
        RegionMap.Look look = _look;
        look.metresPerKm = MetresPerKm(area);
        RegionMap map = RegionMap.Build(_stage, _atlas, area, _answer.cities, _regionColors, look);
        // Its selection edges too, so selecting never makes one; and put
        // away until Layout stands it in the middle.
        map.PrebuildEdges();
        map.SetVisible(false);
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

    // The length the scale bar last showed, in kilometres.
    private float _scaleBarKm = -1f;

    // 1, 2 or 5 times a power of ten, at most the length given.
    private static float NiceLength(float km)
    {
        float power = Mathf.Pow(10f, Mathf.Floor(Mathf.Log10(Mathf.Max(km, 1e-3f))));
        if (5f * power <= km) return 5f * power;
        if (2f * power <= km) return 2f * power;
        return power;
    }

    private RectTransform _axes;
    private readonly RectTransform[] _axisBacks = new RectTransform[2];
    private readonly RectTransform[] _axisFrames = new RectTransform[2];
    private readonly RectTransform[] _axisFills = new RectTransform[2];

    // Index 0 of each is the Region slider's bar, 1 the Scale slider's.
    private static RectTransform AxisImage(RectTransform parent, string name, float radius, Color color,
        bool back = false)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        Image image = go.AddComponent<Image>();
        RoundedSprite.Apply(image, radius, back);
        image.color = color;
        image.raycastTarget = false;
        return rt;
    }

    // Lays one axis out, as a rectangle from 'min' to 'max' on the
    // map's plane, in metres; the fill sits a border inside the frame.
    private void LayAxis(int index, Vector2 min, Vector2 max, float mm)
    {
        Vector2 centre = (min + max) * 0.5f / mm;
        Vector2 size = (max - min) / mm;
        _axisBacks[index].anchoredPosition = _axisFrames[index].anchoredPosition = centre;
        _axisBacks[index].sizeDelta = _axisFrames[index].sizeDelta = size;
        _axisFills[index].anchoredPosition = centre;
        _axisFills[index].sizeDelta = size - Vector2.one * (Style.SmallBorder * 2f);
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

}
