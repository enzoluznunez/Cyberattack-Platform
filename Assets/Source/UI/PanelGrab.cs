using System.Collections;
using Oculus.Interaction;
using Oculus.Interaction.Grab;
using Oculus.Interaction.GrabAPI;
using Oculus.Interaction.HandGrab;
using Oculus.Interaction.Input;
using UnityEngine;

// Lets a hand take hold of something the way it takes the tool panel: pinch
// or grip anywhere inside its box to carry it with one hand, take it with
// both to turn it about the upright axis. It swells a little while held, as
// the panel does, and turns itself to face the user when they walk round
// behind it. A hand at one of the map's sliders never takes it
// (FlatSlideBar.AnyBusy). The map panel moves this way.
public class PanelGrab : MonoBehaviour
{
    private BoxCollider _box;
    private Grabbable _grabbable;
    private Transform _pivot;
    private GrabSwell _swell;
    private Coroutine _flip;

    public bool Held => _grabbable != null && _grabbable.SelectingPointsCount > 0;

    // The middle of the box a hand takes it by, in its own space.
    public Vector3 Center => _box != null ? _box.center : Vector3.zero;
    public Vector3 Size => _box != null ? _box.size : Vector3.zero;

    // Whether it may be taken hold of at all, as while it is shown.
    public bool Allowed { get; set; } = true;

    public static PanelGrab Attach(GameObject go)
    {
        PanelGrab grab = go.AddComponent<PanelGrab>();
        grab.Build();
        return grab;
    }

    private void Build()
    {
        Rigidbody body = gameObject.AddComponent<Rigidbody>();
        body.isKinematic = true;
        body.useGravity = false;

        _box = gameObject.AddComponent<BoxCollider>();

        _pivot = new GameObject("GrabPivot").transform;
        _pivot.SetParent(transform, false);

        OneGrabTranslateTransformer carry = gameObject.AddComponent<OneGrabTranslateTransformer>();

        TwoGrabRotateTransformer spin = PanelPlacement.UprightSpin(gameObject, _pivot);

        _grabbable = gameObject.AddComponent<Grabbable>();
        _grabbable.InjectOptionalRigidbody(body);
        _grabbable.InjectOptionalThrowWhenUnselected(false);
        _grabbable.InjectOptionalKinematicWhileSelected(true);
        _grabbable.InjectOptionalOneGrabTransformer(carry);
        _grabbable.InjectOptionalTwoGrabTransformer(spin);

        GrabbingRule pinch = new GrabbingRule(
            HandFingerFlags.Thumb | HandFingerFlags.Index, GrabbingRule.DefaultPinchRule);
        HandGrabInteractable handGrab = gameObject.AddComponent<HandGrabInteractable>();
        handGrab.InjectAllHandGrabInteractable(GrabTypeFlags.Pinch | GrabTypeFlags.Palm, body, pinch,
            GrabbingRule.DefaultPalmRule);
        handGrab.InjectOptionalPointableElement(_grabbable);
    }

    // The box a hand takes it by, in its own space; the two-hand turn spins
    // about the box's middle.
    public void SetBox(Vector3 center, Vector3 size)
    {
        _box.center = center;
        _box.size = size;
        _pivot.localPosition = center;
    }

    private void LateUpdate()
    {
        bool held = Held;
        bool on = Allowed && (held || !FlatSlideBar.AnyBusy);
        if (_box.enabled != on && !held) _box.enabled = on;

        _swell.Set(transform, held);
        Face(held);
    }

    // Seen from behind, it turns half round to face the user again.
    private void Face(bool held)
    {
        if (_flip != null || held || !Allowed) return;
        Transform cam = CameraRig.MainTransform;
        if (cam == null) return;
        if (!PanelPlacement.ShouldFlip(transform.position, transform.forward, cam.position)) return;
        _flip = StartCoroutine(Flip());
    }

    private IEnumerator Flip()
    {
        yield return PanelPlacement.Flip(transform, _pivot.position, () => Held);
        _flip = null;
    }

    private void OnDisable()
    {
        if (_flip != null) StopCoroutine(_flip);
        _flip = null;
    }
}
