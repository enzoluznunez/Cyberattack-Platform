using UnityEngine;

// Shared base for the tools that hand the sheet to the user's grab: Move, Rotate
// and Scale. Each subclass only says how to arm the grab and how to report it.
public abstract class GrabTool : Tool
{
    private bool _armPending;

    protected abstract EditKind RecordKind { get; }

    protected abstract string Describe(float distance);

    protected abstract void Arm();

    // What this tool's grab adds to a sheet, added while the app loads
    // (ManageSheets.PrepareRoom) rather than the first time the tool is used.
    protected virtual void PrepareSheet(CreateSheet sheet) { }

    protected override void OnToolStart()
    {
        ManageSheets.PrepareGrabs += PrepareSheet;
        if (sheetManager == null) return;
        sheetManager.OnSheetsChanged += ApplyGrabEnabled;
        sheetManager.OnSheetMoveCommitted += OnSheetMoveCommitted;
    }

    protected override void OnToolDestroy()
    {
        ManageSheets.PrepareGrabs -= PrepareSheet;
        if (sheetManager == null) return;
        sheetManager.OnSheetsChanged -= ApplyGrabEnabled;
        sheetManager.OnSheetMoveCommitted -= OnSheetMoveCommitted;
    }

    // Whether resetting this tool drops the sheet's moves, which puts it back
    // in its place.
    protected virtual bool ReattachOnReset => false;

    protected override void OnResetTool()
    {
        if (sheetManager != null) sheetManager.ResetGrabs(ReattachOnReset);
    }

    protected override void OnActiveChanged(bool active)
    {
        if (sheetManager == null) return;
        if (active) { _armPending = true; return; }
        _armPending = false;
        sheetManager.SetGrabbable(false);
    }

    private void OnSheetMoveCommitted(CreateSheet sheet, Vector3 prePos, Quaternion preRot, Vector3 preScale)
    {
        if (!Active || sheet == null || toolManager == null || sheetManager == null) return;

        // A hold that changed nothing is no edit: one hand on a sheet the
        // Rotate or Scale tool turns or sizes only with two, or a pinch let go
        // where it was taken. Recorded, it would take an Undo press that does
        // nothing, and a Move would cut the sheet loose from the map.
        Transform t = sheet.transform;
        if (MoveRecord.SamePose(t.localPosition, t.localRotation, t.localScale, prePos, preRot, preScale)) return;

        float distance = sheetManager.transform
            .TransformVector(sheet.transform.localPosition - prePos).magnitude;

        Report(Describe(distance));

        ManageDatasets.ActiveEdits.PushMove(new MoveRecord
        {
            sheetId = sheet.sheetId,
            prePos = prePos,
            preRot = preRot,
            preScale = preScale,
            postPos = sheet.transform.localPosition,
            postRot = sheet.transform.localRotation,
            postScale = sheet.transform.localScale,
            distance = distance,
            anchorPos = sheetManager.transform.position,
            anchorRot = sheetManager.transform.rotation
        }, RecordKind);
    }

    private void ApplyGrabEnabled()
    {
        if (Active) _armPending = true;
    }

    private void LateUpdate()
    {
        if (!_armPending) return;
        _armPending = false;
        if (!Active || sheetManager == null) return;
        Arm();
    }
}
