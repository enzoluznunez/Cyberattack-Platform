using System;
using UnityEngine;

// Brings the sheet and maps back in front of the user when they put the
// headset on again after a while, or recenter it from the system menu. The
// layout was stood ahead of the user from where they were when it first
// opened (Home); returning, the rig is turned and shifted so the head is
// where that was again, at the same height. The content itself never moves, so every
// pose stated against it (a sheet moved off on its own, the tool panel
// beside the map, raised strips) stays as it was.
public class Recenter : MonoBehaviour
{
    // Off for less than this, the headset was only lifted to adjust it.
    private const double AwaySeconds = 5.0;

    private static Recenter _instance;

    private static bool _hasHome;
    private static Vector3 _homePos;
    private static float _homeYaw;

    private DateTime _offSince;
    private bool _off;
    private bool _pending;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        _instance = null;
        _hasHome = false;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Create() => Spawn.Once(ref _instance);

    // Where the user stood and faced when the layout was first stood ahead
    // of them. Kept from the first call only.
    public static void NoteHome()
    {
        if (_hasHome) return;
        Transform cam = CameraRig.MainTransform;
        if (cam == null) return;
        _homePos = cam.position;
        _homeYaw = YawOf(cam);
        _hasHome = true;
    }

    private void OnEnable()
    {
        OVRManager.HMDUnmounted += OnUnmounted;
        OVRManager.HMDMounted += OnMounted;
        if (OVRManager.display != null) OVRManager.display.RecenteredPose += OnRecentered;
    }

    private void OnDisable()
    {
        OVRManager.HMDUnmounted -= OnUnmounted;
        OVRManager.HMDMounted -= OnMounted;
        if (OVRManager.display != null) OVRManager.display.RecenteredPose -= OnRecentered;
    }

    private void OnUnmounted()
    {
        _off = true;
        _offSince = DateTime.UtcNow;
    }

    private void OnMounted()
    {
        if (!_off) return;
        _off = false;
        if ((DateTime.UtcNow - _offSince).TotalSeconds >= AwaySeconds) _pending = true;
    }

    private void OnRecentered() => _pending = true;

    // The head's pose is not good the moment the headset goes on: it waits
    // until it is tracked.
    private void LateUpdate()
    {
        if (!_pending || !_hasHome) return;
        if (!CameraRig.TryGetBasis(out _, out _)) return;
        _pending = false;
        Apply();
    }

    private static void Apply()
    {
        Transform cam = CameraRig.MainTransform;
        Transform rig = RigRoot(cam);
        if (cam == null || rig == null) return;

        Vector3 head = cam.position;
        Quaternion turn = Quaternion.AngleAxis(Mathf.DeltaAngle(YawOf(cam), _homeYaw), Vector3.up);
        // Level too: sitting down or standing up while away, the eyes come
        // back to the height the layout was stood for.
        rig.SetPositionAndRotation(_homePos + turn * (rig.position - head), turn * rig.rotation);
    }

    private static Transform RigRoot(Transform cam)
    {
        OVRCameraRig rig = FindAnyObjectByType<OVRCameraRig>();
        if (rig != null) return rig.transform;
        // CenterEyeAnchor under TrackingSpace under the rig.
        return cam != null && cam.parent != null ? cam.parent.parent : null;
    }

    private static float YawOf(Transform t) =>
        Quaternion.LookRotation(CameraRig.Flatten(t.forward, Vector3.forward), Vector3.up).eulerAngles.y;
}
