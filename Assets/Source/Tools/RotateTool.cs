using UnityEngine;
using Oculus.Interaction;

public class RotateTool : GrabTool
{
    private static ITransformer YawTransformerFor(CreateSheet sheet) => PanelPlacement.FreeSpin(sheet.gameObject);

    protected override void PrepareSheet(CreateSheet sheet) => YawTransformerFor(sheet);

    protected override ToolType Kind => ToolType.Rotate;

    protected override EditKind RecordKind => EditKind.Rotate;

    protected override string Describe(float distance) => "rotated the sheet";

    protected override void Arm()
    {
        sheetManager.SetGrabbable(true);
        sheetManager.SetTwoGrab(YawTransformerFor);
    }
}
