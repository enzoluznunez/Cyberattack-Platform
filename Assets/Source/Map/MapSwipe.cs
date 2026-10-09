using Oculus.Interaction;
using Oculus.Interaction.Grab;
using Oculus.Interaction.GrabAPI;
using Oculus.Interaction.HandGrab;
using Oculus.Interaction.Input;
using UnityEngine;

// Makes a country's map the thing the user takes hold of: a pinch or a palm
// grab on the map moves the map itself, sideways in its slot and nowhere else,
// and MapCarousel reads how far it has gone to bring the next country in.
//
// The map's hold is its own outline's box, as deep as the map and its dots,
// with a floor on its size: a country a few millimetres across is still a few
// centimetres to take hold of.
[RequireComponent(typeof(BoxCollider), typeof(Rigidbody))]
public class MapSwipe : MonoBehaviour
{
    private Grabbable _grabbable;
    private HandGrabInteractable _handGrab;

    public bool IsGrabbed => _grabbable != null && _grabbable.SelectingPointsCount > 0;

    // How far the map has been pulled from the middle of its slot, in metres,
    // right positive.
    public float Offset => transform.localPosition.x;

    // 'reach' is how far either way the map may be pulled from the middle of
    // the slot, which is where it rests whenever it can be held. The limit is
    // the slot's, not the map's starting point: a map is first built off to
    // one side, as a neighbour, before it is ever the one in the slot.
    public static MapSwipe Attach(CountryMap map, float smallestHold, float depth, float reach)
    {
        BoxCollider box = map.gameObject.AddComponent<BoxCollider>();
        box.center = new Vector3(0f, 0f, -depth * 0.5f);
        box.size = new Vector3(Mathf.Max(map.Size.x, smallestHold), Mathf.Max(map.Size.y, smallestHold), depth);

        Rigidbody body = map.gameObject.AddComponent<Rigidbody>();
        body.isKinematic = true;
        body.useGravity = false;

        var swipe = map.gameObject.AddComponent<MapSwipe>();
        swipe.Build(body, reach);
        return swipe;
    }

    private void Build(Rigidbody body, float reach)
    {
        OneGrabTranslateTransformer slide = gameObject.AddComponent<OneGrabTranslateTransformer>();
        slide.InjectOptionalConstraints(new OneGrabTranslateTransformer.OneGrabTranslateConstraints
        {
            ConstraintsAreRelative = false,
            MinX = Pin(-reach),
            MaxX = Pin(reach),
            MinY = Pin(0f),
            MaxY = Pin(0f),
            MinZ = Pin(0f),
            MaxZ = Pin(0f)
        });

        _grabbable = gameObject.AddComponent<Grabbable>();
        _grabbable.InjectOptionalRigidbody(body);
        _grabbable.InjectOptionalThrowWhenUnselected(false);
        _grabbable.InjectOptionalKinematicWhileSelected(true);
        _grabbable.MaxGrabPoints = 1;
        _grabbable.InjectOptionalOneGrabTransformer(slide);

        GrabbingRule pinch = new GrabbingRule(
            HandFingerFlags.Thumb | HandFingerFlags.Index, GrabbingRule.DefaultPinchRule);

        _handGrab = gameObject.AddComponent<HandGrabInteractable>();
        _handGrab.InjectAllHandGrabInteractable(GrabTypeFlags.Pinch | GrabTypeFlags.Palm, body,
            pinch, GrabbingRule.DefaultPalmRule);
        _handGrab.InjectOptionalPointableElement(_grabbable);
    }

    // Whether a hand may take hold: only the map in the slot, and not while
    // the maps are still sliding.
    public void SetHoldable(bool on)
    {
        if (_handGrab != null && _handGrab.enabled != on) _handGrab.enabled = on;
    }

    private static FloatConstraint Pin(float value) => new FloatConstraint { Constrain = true, Value = value };
}
