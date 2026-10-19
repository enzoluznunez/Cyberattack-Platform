using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// A flat slider in the tool panel's own style, for the map: a long thin bar
// edged in black for the track, and a thumb drawn as the panel's buttons are,
// the size of its Undo All button. It is a small world-space canvas, so the
// corners, borders and the black back seen from behind are the panel's own.
// Its track runs along its local X; turned a quarter about its Z, it stands
// upright.
//
// A fingertip that touches the thumb takes it, and the thumb follows the
// finger along the track until the finger comes away from it; what it stands
// for is its owner's, told as the share of the track it is along, 0 at the
// low end and 1 at the high. Let go, the thumb rests where its owner puts it.
//
// It is measured in the panel's units, at the panel's own scale (SetUnit), so
// its thumb is the size the Undo All button looks beside it, whatever the
// owner draws around it. A hand at a slider is the slider's: while one is on
// or near a thumb, the grabs that move the panel and the sheet are off
// (AnyBusy), so working a slider never moves them.
public class FlatSlideBar : MonoBehaviour
{
    // The Undo All button's size, in the panel's units (millimetres).
    public static readonly Vector2 ThumbSize = new Vector2(Style.TitleBarButtonWidth, Style.Button.y);
    public const float TrackHeight = 10f;

    // The thumb is pressed as a button is (PokeRule): a fingertip hovering in
    // front of it takes it as it reaches the face, and once taken it keeps it
    // however deep the finger pushes through, until the finger draws back
    // toward the user. All in millimetres, measured off the face toward the
    // viewer.
    private const float HoverDepth = PokeRule.HoverDepth * PokeRule.Millimetres;
    private const float PressDepth = PokeRule.PressDepth * PokeRule.Millimetres;
    private const float BehindDepth = PokeRule.BehindDepth * PokeRule.Millimetres;
    private const float ReleaseDepth = PokeRule.ReleaseDepth * PokeRule.Millimetres;
    private const float TouchSlack = PokeRule.EdgeSlack * PokeRule.Millimetres;

    // How near a thumb a fingertip counts as about to work it, so a grab
    // does not take the hand first: off the face, and outside the thumb.
    private const float NearDepth = 60f;
    private const float NearSlack = 40f;

    // The sliders a hand is on or near.
    private static readonly HashSet<FlatSlideBar> Busy = new HashSet<FlatSlideBar>();

    // Whether a hand is on or near any slider's thumb.
    public static bool AnyBusy => Busy.Count > 0;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => Busy.Clear();

    // The hand's tremor is taken out of the drag as it is out of every other
    // slide: steady when still, quick when moving.
    private const float SteadyCutoff = 3f;
    private const float SpeedCutoff = 10f;

    private RectTransform _canvas;
    private RectTransform _thumb;
    private Image _thumbFill;
    private float _length;
    private float _travel;
    // The panel's scale: a unit on the canvas is a millimetre of the panel.
    private float _unit = 1f;

    // The fingertip holding the thumb, by its place in Fingertips.All, or -1,
    // and where on the thumb it took hold.
    private int _tip = -1;
    private float _grip;
    // Fingertips on the thumb or pressing into it, and those hovering in front
    // of it, which are the only ones that may take it.
    private readonly HashSet<int> _touching = new HashSet<int>();
    private readonly HashSet<int> _hovering = new HashSet<int>();
    private readonly OneEuroPoseFilter _smooth = new OneEuroPoseFilter();

    public event Action<float> OnSlide;
    public event Action<float> OnRelease;

    public bool Held => _tip >= 0;

    // Whether this fingertip is on the thumb or holding it, so nothing else
    // takes it for a poke of its own.
    public bool Touches(int tip) => _tip == tip || _touching.Contains(tip);

    // Its owner draws the track (the map draws both its sliders' as one L),
    // and the thumb is drawn over it.
    public static FlatSlideBar Create(Transform parent, string name)
    {
        RectTransform canvas = FlatCanvas(parent, name);
        FlatSlideBar bar = canvas.gameObject.AddComponent<FlatSlideBar>();
        bar._canvas = canvas;
        canvas.GetComponent<Canvas>().sortingOrder = Style.SortPanels + 1;
        bar._thumb = Pill(canvas, "Thumb", ThumbSize, Style.White);
        bar._thumbFill = bar._thumb.Find("Inner").GetComponent<Image>();
        return bar;
    }

    // A world-space canvas measured as the panel's is, a unit a millimetre
    // of the panel, drawn in its order.
    public static RectTransform FlatCanvas(Transform parent, string name)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        Canvas canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = Style.SortPanels;
        var rt = (RectTransform)go.transform;
        rt.localScale = Vector3.one * Style.WorldScale;
        rt.sizeDelta = Vector2.zero;
        return rt;
    }

    // A rounded bar drawn the way the panel's buttons are: a black frame, a
    // fill inside it, and a black back seen from behind. Centred on its
    // parent; a width of zero leaves it to be sized later.
    public static RectTransform Pill(RectTransform parent, string name, Vector2 size, Color fill)
    {
        float radius = Style.RadiusFor(size.y);
        var go = new GameObject(name, typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.sizeDelta = size;

        Image frame = go.AddComponent<Image>();
        RoundedSprite.Apply(frame, radius);
        frame.color = Style.Black;
        frame.raycastTarget = false;

        Image inner = Child(rt, "Inner", Style.SmallBorder);
        RoundedSprite.Apply(inner, Style.InnerRadius(radius));
        inner.color = fill;

        Image back = Child(rt, "Back", 0f);
        RoundedSprite.Apply(back, radius, back: true);
        back.color = Style.Black;
        return rt;
    }

    private static Image Child(RectTransform parent, string name, float inset)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        UILayout.Stretch(rt, inset);
        Image image = go.AddComponent<Image>();
        image.raycastTarget = false;
        return image;
    }

    // Draws the bar at the panel's scale, as the panel's own buttons are.
    public void SetUnit(float unit)
    {
        _unit = Mathf.Max(unit, 1e-4f);
        _canvas.localScale = Vector3.one * (Style.WorldScale * _unit);
    }

    // A track 'length' of the panel's units long, centred on the bar, the
    // thumb's ends reaching its ends; the thumb keeps the Undo All button's
    // size.
    public void SetLength(float length)
    {
        _length = length;
        _travel = Mathf.Max(_length - ThumbSize.x, 0f);
    }

    // Puts the thumb at a share of the track, unless a finger has it.
    public void Rest(float share)
    {
        if (Held) return;
        _thumb.anchoredPosition = new Vector2(XOf(share), 0f);
    }

    private void LateUpdate()
    {
        IReadOnlyList<Fingertips.Source> tips = Fingertips.All;

        if (Held && !Follow(tips)) Let();

        float front = FrontSign();
        bool near = Held;
        for (int i = 0; i < tips.Count; i++)
        {
            bool live = tips[i].Live;
            Vector3 local = live ? _canvas.InverseTransformPoint(tips[i].poke.Origin) : default;
            float toward = local.z * front;
            bool over = live && InThumb(local, TouchSlack);
            bool pressing = over && toward <= PressDepth / _unit && toward >= -BehindDepth / _unit;
            bool hovering = over && toward > PressDepth / _unit && toward <= HoverDepth / _unit;

            if (pressing && !Held && _hovering.Contains(i)) Take(i, local);

            if (pressing) _touching.Add(i);
            else _touching.Remove(i);
            if (hovering) _hovering.Add(i);
            else if (!pressing) _hovering.Remove(i);

            near |= live && Mathf.Abs(local.z) <= NearDepth / _unit && InThumb(local, NearSlack);
        }
        if (near) Busy.Add(this);
        else Busy.Remove(this);
    }

    // Which way along the canvas's Z the viewer is: the face a finger
    // presses from.
    private float FrontSign()
    {
        Transform cam = CameraRig.MainTransform;
        if (cam == null) return -1f;
        return _canvas.InverseTransformPoint(cam.position).z >= 0f ? 1f : -1f;
    }

    // Whether a point in canvas space is over the thumb, up to 'slack' (real
    // millimetres, whatever scale the bar is drawn at) outside it.
    private bool InThumb(Vector3 local, float slack)
    {
        Vector2 at = _thumb.anchoredPosition;
        slack /= _unit;
        return Mathf.Abs(local.x - at.x) <= ThumbSize.x * 0.5f + slack &&
               Mathf.Abs(local.y - at.y) <= ThumbSize.y * 0.5f + slack;
    }

    private void Take(int tip, Vector3 local)
    {
        _tip = tip;
        _grip = _thumb.anchoredPosition.x - local.x;
        _smooth.Reset();
        _smooth.posMinCutoff = SteadyCutoff;
        _smooth.posBeta = SpeedCutoff;
        _thumbFill.color = Style.Black;
    }

    // The thumb follows the finger along the track; false once the finger is
    // gone or has come away from the face.
    private bool Follow(IReadOnlyList<Fingertips.Source> tips)
    {
        if (_tip >= tips.Count || !tips[_tip].Live) return false;
        Vector3 local = _canvas.InverseTransformPoint(tips[_tip].poke.Origin);
        // Drawn back toward the user, or gone far through, it lets go; along
        // the track or across it, it holds.
        float toward = local.z * FrontSign();
        if (toward > ReleaseDepth / _unit || toward < -BehindDepth / _unit) return false;

        float x = _smooth.Filter(new Pose(new Vector3(local.x + _grip, 0f, 0f), Quaternion.identity),
            Time.deltaTime).position.x;
        x = Mathf.Clamp(x, -_travel * 0.5f, _travel * 0.5f);
        _thumb.anchoredPosition = new Vector2(x, 0f);
        OnSlide?.Invoke(ShareOf(x));
        return true;
    }

    private void Let()
    {
        _tip = -1;
        _thumbFill.color = Style.White;
        OnRelease?.Invoke(ShareOf(_thumb.anchoredPosition.x));
    }

    // Shut away mid-drag, it lets go where it was.
    private void OnDisable()
    {
        _touching.Clear();
        _hovering.Clear();
        Busy.Remove(this);
        if (Held) Let();
    }

    private float XOf(float share) => -_travel * 0.5f + _travel * Mathf.Clamp01(share);

    private float ShareOf(float x) => _travel <= 1e-6f ? 0f : Mathf.Clamp01((x + _travel * 0.5f) / _travel);
}
