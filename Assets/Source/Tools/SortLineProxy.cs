using Oculus.Interaction;
using Oculus.Interaction.Grab;
using UnityEngine;

[RequireComponent(typeof(BoxCollider), typeof(Rigidbody))]
public class SortLineProxy : MonoBehaviour
{
    public CreateSheet sheet;
    public bool columns;
    public int block;

    private Grabbable _grabbable;

    public bool IsGrabbed => _grabbable != null && _grabbable.SelectingPointsCount > 0;

    public float Coord => columns ? transform.localPosition.x : transform.localPosition.z;

    private const float RowBand = 0.25f;
    private const float ColumnBand = 0.75f;
    private const float BandHeight = 0.45f;

    // One handle per block, so a grouped axis is grabbed a whole metric at a time
    // and its years can never be pulled apart.
    public static SortLineProxy Create(CreateSheet owner, bool columns, int block, float height)
    {
        if (owner == null) return null;

        float cell = owner.CellSize;
        if (cell <= 1e-6f || height <= 1e-6f) return null;

        int min = owner.BlockMin(columns);
        int max = owner.BlockMax(columns);
        if (block < min || block > max) return null;

        int perpMin = columns ? owner.rowMin : owner.colMin;
        int perpMax = columns ? owner.rowMax : owner.colMax;
        float span = owner.LineCoord(!columns, perpMax) - owner.LineCoord(!columns, perpMin) + cell;
        float thickness = owner.GroupSizeOn(columns) * owner.PitchOn(columns);

        GameObject go = new GameObject($"SortLine_{(columns ? "Col" : "Row")}_{block}");
        go.transform.SetParent(owner.transform, false);

        float coord = owner.BlockCoord(columns, block);
        float bandCentre = height * (columns ? ColumnBand : RowBand);
        go.transform.localPosition = columns
            ? new Vector3(coord, bandCentre, 0f)
            : new Vector3(0f, bandCentre, coord);
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;

        SortLineProxy proxy = go.AddComponent<SortLineProxy>();
        proxy.sheet = owner;
        proxy.columns = columns;
        proxy.block = block;
        proxy.Build(thickness, span, height, owner.BlockPitch(columns),
            owner.BlockCoord(columns, min) - coord, owner.BlockCoord(columns, max) - coord);
        return proxy;
    }

    private void Build(float cell, float span, float height, float handle, float back, float forward)
    {
        BoxCollider box = GetComponent<BoxCollider>();
        box.center = Vector3.zero;
        box.size = columns
            ? new Vector3(cell, height * BandHeight, span)
            : new Vector3(span, height * BandHeight, cell);

        float lift = height * 0.5f - transform.localPosition.y;
        for (int end = -1; end <= 1; end += 2)
        {
            BoxCollider grip = gameObject.AddComponent<BoxCollider>();
            float along = (span + handle) * 0.5f * end;
            grip.center = columns ? new Vector3(0f, lift, along) : new Vector3(along, lift, 0f);
            grip.size = columns ? new Vector3(cell, height, handle) : new Vector3(handle, height, cell);
        }

        _grabbable = SlideGrab.Attach(gameObject, GetComponent<Rigidbody>(), Limits(back, forward),
            GrabTypeFlags.Pinch).grabbable;
    }

    private OneGrabTranslateTransformer.OneGrabTranslateConstraints Limits(float back, float forward)
    {
        var limits = new OneGrabTranslateTransformer.OneGrabTranslateConstraints
        {
            ConstraintsAreRelative = true,
            MinX = SlideGrab.Pin(0f),
            MaxX = SlideGrab.Pin(0f),
            MinY = SlideGrab.Pin(0f),
            MaxY = SlideGrab.Pin(0f),
            MinZ = SlideGrab.Pin(0f),
            MaxZ = SlideGrab.Pin(0f)
        };

        if (columns) { limits.MinX = SlideGrab.Pin(back); limits.MaxX = SlideGrab.Pin(forward); }
        else { limits.MinZ = SlideGrab.Pin(back); limits.MaxZ = SlideGrab.Pin(forward); }

        return limits;
    }
}
