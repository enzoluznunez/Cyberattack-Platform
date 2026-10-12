using System.Collections.Generic;
using UnityEngine;
using Oculus.Interaction;
using Oculus.Interaction.Input;

[RequireComponent(typeof(ReadSheets))]
public class SheetTouch : MonoBehaviour
{
    [Tooltip("How far past the fingertip a bar starts responding, in metres.")]
    public float hoverEnter = 0.02f;

    [Tooltip("How far the fingertip must withdraw before the bar stops responding. Keep above hoverEnter.")]
    public float hoverExit = 0.05f;

    [Tooltip("Furthest a fingertip can credibly be from the head, in metres. Guards against parked interactors.")]
    public float maxReachFromHead = 1.2f;

    private ReadSheets _hub;

    private PokeInteractor _driver;
    private CreateCube _cube;
    private bool _selected;

    private void Awake() => _hub = GetComponent<ReadSheets>();

    private void OnDisable() => Drop();

    private void Update()
    {
        if (_hub == null) return;

        if (!_hub.Listening) { Drop(); return; }

        if (_driver != null && !Track(_driver)) Drop();
        if (_driver != null) return;

        IReadOnlyList<Fingertips.Source> pokes = Fingertips.All;
        for (int i = 0; i < pokes.Count; i++)
        {
            Fingertips.Source source = pokes[i];
            if (!Live(source)) continue;
            if (!Acquire(source)) continue;
            _driver = source.poke;
            return;
        }
    }

    // Live, and within reach of the head: a parked interactor is not a finger.
    private bool Live(Fingertips.Source source)
    {
        if (!source.Live) return false;
        PokeInteractor poke = source.poke;

        Transform head = CameraRig.MainTransform;
        if (head == null) return false;

        return (poke.Origin - head.position).sqrMagnitude <=
            maxReachFromHead * maxReachFromHead;
    }

    private Fingertips.Source Current()
    {
        IReadOnlyList<Fingertips.Source> pokes = Fingertips.All;
        for (int i = 0; i < pokes.Count; i++)
            if (pokes[i].poke == _driver) return pokes[i];
        return default;
    }

    private static Vector3 WristOf(Fingertips.Source source, Vector3 tip) =>
        source.hand != null && source.hand.GetJointPose(HandJointId.HandWristRoot, out Pose wrist)
            ? wrist.position
            : tip;

    private bool Acquire(Fingertips.Source source)
    {
        PokeInteractor poke = source.poke;
        Vector3 tip = poke.Origin;
        float reach = Mathf.Max(poke.Radius, 0f) + Mathf.Max(hoverEnter, 0f);
        if (!SheetRaycast.NearestCube(tip, reach, out SheetRaycast.Hit hit)) return false;

        _cube = hit.cube;
        _selected = false;
        _hub.Hover(ReadSheets.Describe(hit, tip, WristOf(source, tip)));
        return true;
    }

    private bool Track(PokeInteractor poke)
    {
        Fingertips.Source source = Current();
        if (!Live(source)) return false;

        Vector3 tip = poke.Origin;
        float radius = Mathf.Max(poke.Radius, 0f);
        float reach = radius + Mathf.Max(hoverExit, hoverEnter);

        if (!SheetRaycast.NearestCube(tip, reach, out SheetRaycast.Hit hit)) return false;

        ReadSheets.Reading reading = ReadSheets.Describe(hit, tip, WristOf(source, tip));
        _cube = hit.cube;

        if (_selected)
        {
            if (hit.distance <= radius + Mathf.Max(hoverExit, 0f))
            {
                _hub.Hover(reading);
                return true;
            }

            _selected = false;
            _hub.Release(reading);
            return true;
        }

        if (hit.distance <= radius || SheetRaycast.Contains(hit.cube, tip))
        {
            _selected = true;
            _hub.Select(reading);
            return true;
        }

        _hub.Hover(reading);
        return true;
    }

    private void Drop()
    {
        if (_driver == null && _cube == null && !_selected) return;

        bool wasSelected = _selected;
        CreateCube cube = _cube;

        _driver = null;
        _cube = null;
        _selected = false;

        if (_hub == null) return;

        if (wasSelected && cube != null)
            _hub.Release(ReadSheets.Describe(cube, cube.transform.position));

        _hub.Cleared();
    }
}
