using System;
using Oculus.Interaction;
using Oculus.Interaction.Grab;
using Oculus.Interaction.HandGrab;
using UnityEngine;

// A slider the hand takes hold of: a track along the bar's local X and a
// thumb on it, standing out along its local Y. Pinching the thumb and sliding
// it moves it along the track and nowhere else; what it stands for is its
// owner's, told as the share of the track it is along, 0 at the low end and
// 1 at the high. Letting go leaves the thumb where its owner rests it.
//
// The sheet's scroll bar and the maps' slider are both one of these, turned
// to lie along the sheet's rows and under the map.
public class SlideBar : MonoBehaviour
{
    private const float TrackWidth = 0.006f;
    private const float TrackHeight = 0.004f;
    private const float ThumbWidth = 0.014f;
    public const float ThumbHeight = 0.012f;

    // Room around the thumb that still takes hold of it, in metres.
    private const float HoldSlack = 0.01f;

    // The hand's tremor is taken out of the slide as it is out of every other
    // slide: steady when still, quick when moving.
    private const float SteadyCutoff = 3f;
    private const float SpeedCutoff = 10f;

    // The tool panel's own colours: a black track, a white thumb, grey while
    // held.
    private static readonly Color TrackColor = Style.Black;
    private static readonly Color ThumbColor = Style.White;
    private static readonly Color HeldColor = Style.Hex(0xA0, 0xA0, 0xA0);

    private CreateCube _track;
    private Transform _thumb;
    private CreateCube _thumbLook;
    private BoxCollider _hold;
    private Grabbable _grabbable;
    private HandGrabInteractable _handGrab;
    private OneGrabTranslateTransformer _slide;
    private readonly OneEuroPoseFilter _smooth = new OneEuroPoseFilter();

    private float _travel;
    private bool _held;

    // The share of the track the thumb is along, each frame it is held, and
    // the share it was let go at.
    public event Action<float> OnSlide;
    public event Action<float> OnRelease;

    public bool Held => _held;

    public static SlideBar Create(Transform parent, string name, Material material)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        SlideBar bar = go.AddComponent<SlideBar>();
        bar.Build(material);
        return bar;
    }

    private void Build(Material material)
    {
        _track = Look(transform, "Track", material, TrackColor);

        var thumb = new GameObject("Thumb");
        _thumb = thumb.transform;
        _thumb.SetParent(transform, false);
        _hold = thumb.AddComponent<BoxCollider>();
        Rigidbody body = thumb.AddComponent<Rigidbody>();
        (_grabbable, _handGrab) = SlideGrab.Attach(thumb, body, Limits(0f), GrabTypeFlags.Pinch);
        _slide = thumb.GetComponent<OneGrabTranslateTransformer>();

        _thumbLook = Look(_thumb, "Look", material, ThumbColor);
    }

    // One of the app's cubes, drawn and never touched: its collider is off,
    // so a fingertip reading bars never takes the slider for one.
    private static CreateCube Look(Transform parent, string name, Material material, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        CreateCube cube = go.AddComponent<CreateCube>();
        cube.Init(material);
        cube.SetColor(color);
        cube.Collider.enabled = false;
        return cube;
    }

    // The thumb slides along the track and nowhere else, its centre no
    // further than half its travel either way.
    private static OneGrabTranslateTransformer.OneGrabTranslateConstraints Limits(float travel) =>
        new OneGrabTranslateTransformer.OneGrabTranslateConstraints
        {
            ConstraintsAreRelative = false,
            MinX = SlideGrab.Pin(-travel * 0.5f),
            MaxX = SlideGrab.Pin(travel * 0.5f),
            MinY = SlideGrab.Pin(ThumbHeight * 0.5f),
            MaxY = SlideGrab.Pin(ThumbHeight * 0.5f),
            MinZ = SlideGrab.Pin(0f),
            MaxZ = SlideGrab.Pin(0f)
        };

    // A track 'length' long, centred on the bar, and a thumb 'thumb' long on it.
    public void SetSize(float length, float thumb)
    {
        thumb = Mathf.Clamp(thumb, 0f, length);
        _travel = length - thumb;

        _track.SetBox(new Vector3(0f, TrackHeight * 0.5f, 0f), new Vector3(length, TrackHeight, TrackWidth));
        _thumbLook.SetBox(Vector3.zero, new Vector3(thumb, ThumbHeight, ThumbWidth));
        _hold.center = Vector3.zero;
        _hold.size = new Vector3(thumb, ThumbHeight + HoldSlack * 2f, ThumbWidth + HoldSlack * 2f);
        if (_slide != null) _slide.InjectOptionalConstraints(Limits(_travel));
    }

    // Puts the thumb at a share of the track, unless a hand has it.
    public void Rest(float share)
    {
        if (_held) return;
        _thumb.localPosition = new Vector3(XOf(share), ThumbHeight * 0.5f, 0f);
    }

    // Whether a hand may take hold of the thumb.
    public void SetHoldable(bool on)
    {
        if (_handGrab != null && _handGrab.enabled != on) _handGrab.enabled = on;
    }

    private void LateUpdate()
    {
        bool grabbed = _grabbable != null && _grabbable.SelectingPointsCount > 0;
        if (grabbed)
        {
            if (!_held)
            {
                _held = true;
                _smooth.Reset();
                _smooth.posMinCutoff = SteadyCutoff;
                _smooth.posBeta = SpeedCutoff;
                _thumbLook.SetColor(HeldColor);
            }

            // The hand's next frame starts from the hand again, not from here.
            float x = _smooth.Filter(new Pose(new Vector3(_thumb.localPosition.x, 0f, 0f), Quaternion.identity),
                Time.deltaTime).position.x;
            x = Mathf.Clamp(x, -_travel * 0.5f, _travel * 0.5f);
            _thumb.localPosition = new Vector3(x, ThumbHeight * 0.5f, 0f);
            OnSlide?.Invoke(ShareOf(x));
            return;
        }

        if (!_held) return;
        _held = false;
        _thumbLook.SetColor(ThumbColor);
        OnRelease?.Invoke(ShareOf(_thumb.localPosition.x));
    }

    private float XOf(float share) => -_travel * 0.5f + _travel * Mathf.Clamp01(share);

    private float ShareOf(float x) => _travel <= 1e-6f ? 0f : Mathf.Clamp01((x + _travel * 0.5f) / _travel);
}
