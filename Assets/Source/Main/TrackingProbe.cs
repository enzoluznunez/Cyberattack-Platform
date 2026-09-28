using Meta.XR.MRUtilityKit;
using Oculus.Interaction;
using Oculus.Interaction.Input;
using UnityEngine;

public class TrackingProbe : MonoBehaviour
{
    [SerializeField, Interface(typeof(IHand))]
    private UnityEngine.Object _hand;

    public float logInterval = 0.5f;

    private IHand _ihand;
    private Transform _trackingSpace;
    private Transform _centerEye;
    private float _nextLog;
    private Vector3 _baseline;
    private bool _hasBaseline;

    private void Awake()
    {
        _ihand = _hand as IHand;

        OVRCameraRig rig = FindAnyObjectByType<OVRCameraRig>();
        if (rig != null)
        {
            _trackingSpace = rig.trackingSpace;
            _centerEye = rig.centerEyeAnchor;
        }
    }

    private void LateUpdate()
    {
        if (Time.unscaledTime < _nextLog) return;
        _nextLog = Time.unscaledTime + logInterval;

        if (_trackingSpace == null || _centerEye == null) return;

        bool worldLock = MRUK.Instance != null && MRUK.Instance.IsWorldLockActive;

        Pose wristPose = default;
        bool haveHand = _ihand != null &&
                        _ihand.IsTrackedDataValid &&
                        _ihand.GetJointPose(HandJointId.HandWristRoot, out wristPose);

        Vector3 headRelative = haveHand
            ? _centerEye.InverseTransformPoint(wristPose.position)
            : Vector3.zero;

        if (haveHand && !_hasBaseline)
        {
            _baseline = headRelative;
            _hasBaseline = true;
        }

        Vector3 drift = haveHand ? headRelative - _baseline : Vector3.zero;

        Debug.Log(
            $"[Probe] worldLock={worldLock} " +
            $"ts={_trackingSpace.position.ToString("F4")} " +
            $"eyeY={_centerEye.position.y:F4} " +
            $"handValid={haveHand} " +
            $"handHeadRel={headRelative.ToString("F4")} " +
            $"drift={drift.ToString("F4")}");
    }
}
