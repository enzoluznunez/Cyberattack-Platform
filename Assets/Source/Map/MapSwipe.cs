using Oculus.Interaction;
using Oculus.Interaction.Grab;
using Oculus.Interaction.HandGrab;
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

        var swipe = map.gameObject.AddComponent<MapSwipe>();
        swipe.Build(body, reach);
        return swipe;
    }

    private void Build(Rigidbody body, float reach)
    {
        (_grabbable, _handGrab) = SlideGrab.Attach(gameObject, body,
            new OneGrabTranslateTransformer.OneGrabTranslateConstraints
            {
                ConstraintsAreRelative = false,
                MinX = SlideGrab.Pin(-reach),
                MaxX = SlideGrab.Pin(reach),
                MinY = SlideGrab.Pin(0f),
                MaxY = SlideGrab.Pin(0f),
                MinZ = SlideGrab.Pin(0f),
                MaxZ = SlideGrab.Pin(0f)
            },
            GrabTypeFlags.Pinch | GrabTypeFlags.Palm);
    }

    // Whether a hand may take hold: only the map in the slot, and not while
    // the maps are still sliding.
    public void SetHoldable(bool on)
    {
        if (_handGrab != null && _handGrab.enabled != on) _handGrab.enabled = on;
    }
}
