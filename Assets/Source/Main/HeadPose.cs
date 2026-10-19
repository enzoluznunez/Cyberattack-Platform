using UnityEngine;

// Whether the head can be placed against yet: tracked, on the head, the
// floor origin the rig asks for in effect, and its height steady a moment.
// For the first frames after launch the headset can report the head at the
// eye-level origin, near the floor, before the floor offset applies, and
// anything stood ahead of it then opens far too low.
public class HeadPose : MonoBehaviour
{
    private const float SteadySeconds = 0.3f;
    private const float SteadyMetres = 0.02f;

    // On the floor origin a head this low is the floor offset not applied
    // yet; seated, a head is well above it.
    private const float LowestHead = 0.5f;

    // Tracked this long, the pose is taken as it is, steady or not.
    private const float GiveUpSeconds = 3f;

    public static bool Settled { get; private set; }

    private static HeadPose _instance;

    private float _height;
    private float _steadyFor;
    private float _trackedFor;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        _instance = null;
        Settled = false;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Create() => Spawn.Once(ref _instance);

    private void Update()
    {
        Transform cam = CameraRig.MainTransform;
        if (cam == null || !Tracked())
        {
            Settled = false;
            _trackedFor = 0f;
            _steadyFor = 0f;
            return;
        }

        float dt = Time.unscaledDeltaTime;
        _trackedFor += dt;

        // Height in tracking space: above the floor, whatever the rig's own pose.
        float y = cam.localPosition.y;
        if (Mathf.Abs(y - _height) > SteadyMetres)
        {
            _height = y;
            _steadyFor = 0f;
        }
        else _steadyFor += dt;

        bool steady = _steadyFor >= SteadySeconds && OriginApplied() && (!OnFloor() || y >= LowestHead);
        Settled = steady || _trackedFor >= GiveUpSeconds;
    }

    private static bool Tracked()
    {
        if (!OVRManager.OVRManagerinitialized) return true;
        return OVRPlugin.userPresent && OVRPlugin.GetNodePositionTracked(OVRPlugin.Node.EyeCenter);
    }

    private static bool OnFloor() =>
        OVRManager.instance != null && OVRManager.instance.trackingOriginType != OVRManager.TrackingOrigin.EyeLevel;

    private static bool OriginApplied()
    {
        if (!OVRManager.OVRManagerinitialized || OVRManager.instance == null) return true;
        return (int)OVRPlugin.GetTrackingOriginType() == (int)OVRManager.instance.trackingOriginType;
    }
}
