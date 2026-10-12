using UnityEngine;
using Oculus.Interaction;

public class RotateTool : GrabTool
{
    private static ITransformer YawTransformerFor(CreateSheet sheet)
    {
        TwoGrabRotateTransformer yaw = sheet.GetComponent<TwoGrabRotateTransformer>();
        if (yaw != null) return yaw;

        yaw = sheet.gameObject.AddComponent<TwoGrabRotateTransformer>();
        yaw.InjectOptionalConstraints(new TwoGrabRotateTransformer.TwoGrabRotateConstraints
        {
            MinAngle = new FloatConstraint(),
            MaxAngle = new FloatConstraint()
        });
        return yaw;
    }

    protected override ToolType Kind => ToolType.Rotate;

    protected override EditKind RecordKind => EditKind.Rotate;

    protected override string Describe(float distance) => "rotated the sheet";

    protected override void Arm()
    {
        sheetManager.SetGrabbable(true);
        sheetManager.SetTwoGrab(YawTransformerFor);
    }
}
