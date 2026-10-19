using Oculus.Interaction;
using Oculus.Interaction.Grab;
using Oculus.Interaction.HandGrab;
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

    // Where a row's and a column's handle stand, as shares of the bars' full
    // height, and how tall each is: rows below, columns above, a gap between.
    public const float RowBand = 0.25f;
    public const float ColumnBand = 0.75f;
    public const float BandHeight = 0.45f;

    private BoxCollider _box;
    private BoxCollider[] _grips;
    private HandGrabInteractable _handGrab;
    private OneGrabTranslateTransformer _slide;

    // One handle per block, so a grouped axis is grabbed a whole metric at a time
    // and its years can never be pulled apart. Made with nothing to hold yet,
    // put away, while the app loads, and stood on a sheet's line by Place
    // whenever the Sort tool is out.
    public static SortLineProxy Make(Transform parent)
    {
        GameObject go = new GameObject("SortLine");
        go.transform.SetParent(parent, false);
        SortLineProxy proxy = go.AddComponent<SortLineProxy>();
        proxy.Init();
        proxy.SetLive(false);
        return proxy;
    }

    private void Init()
    {
        _box = GetComponent<BoxCollider>();
        _grips = new[] { gameObject.AddComponent<BoxCollider>(), gameObject.AddComponent<BoxCollider>() };
        (_grabbable, _handGrab) = SlideGrab.Attach(gameObject, GetComponent<Rigidbody>(), Limits(0f, 0f, 0f),
            GrabTypeFlags.Pinch);
        _slide = GetComponent<OneGrabTranslateTransformer>();
    }

    // Stands the handle on a block of a sheet's lines, sliding along that
    // axis from the first block's place to the last. False, leaving it put
    // away, when there is no such block.
    public bool Place(CreateSheet owner, bool columns, int block, float height)
    {
        if (owner == null) return false;

        float cell = owner.CellSize;
        if (cell <= 1e-6f || height <= 1e-6f) return false;

        int min = owner.BlockMin(columns);
        int max = owner.BlockMax(columns);
        if (block < min || block > max) return false;

        // Across every place on the other axis, empty ones included, as the
        // sheet itself spans them: a line filtered off at either end leaves
        // the sheet its size and its middle where it was, so the handle,
        // centred on that middle, would otherwise slide toward the empty end
        // and its grip stand over the first lines at the other. The Filter
        // tool measures the ends the same way (FilterTool.Outside).
        bool across = !columns;
        float span = owner.SlotCoord(across, owner.LastSlot(across))
                     - owner.SlotCoord(across, owner.FirstSlot(across)) + owner.PitchOn(across);
        float thickness = owner.GroupSizeOn(columns) * owner.PitchOn(columns);

        sheet = owner;
        this.columns = columns;
        this.block = block;
        gameObject.name = $"SortLine_{(columns ? "Col" : "Row")}_{block}";
        if (transform.parent != owner.transform) transform.SetParent(owner.transform, false);

        float coord = owner.BlockCoord(columns, block);
        float bandCentre = height * (columns ? ColumnBand : RowBand);
        transform.localPosition = columns
            ? new Vector3(coord, bandCentre, 0f)
            : new Vector3(0f, bandCentre, coord);
        transform.localRotation = Quaternion.identity;
        transform.localScale = Vector3.one;

        Size(thickness, span, height, owner.BlockPitch(columns));
        _slide.InjectOptionalConstraints(Limits(coord, owner.BlockCoord(columns, min) - coord,
            owner.BlockCoord(columns, max) - coord));
        _slide.Initialize(_grabbable);
        SetLive(true);
        return true;
    }

    // Put away, it takes no hand and no poke; nothing of it is drawn either way.
    public void SetLive(bool on)
    {
        _box.enabled = on;
        for (int i = 0; i < _grips.Length; i++) _grips[i].enabled = on;
        _grabbable.enabled = on;
        _handGrab.enabled = on;
    }

    private void Size(float cell, float span, float height, float handle)
    {
        _box.center = Vector3.zero;
        _box.size = columns
            ? new Vector3(cell, height * BandHeight, span)
            : new Vector3(span, height * BandHeight, cell);

        float lift = height * 0.5f - transform.localPosition.y;
        for (int g = 0; g < _grips.Length; g++)
        {
            int end = g == 0 ? -1 : 1;
            float along = (span + handle) * 0.5f * end;
            _grips[g].center = columns ? new Vector3(0f, lift, along) : new Vector3(along, lift, 0f);
            _grips[g].size = columns ? new Vector3(cell, height, handle) : new Vector3(handle, height, cell);
        }
    }

    // Stated in the sheet's own space rather than from where the handle stood
    // when it was readied, so a handle can be stood on any line again.
    private OneGrabTranslateTransformer.OneGrabTranslateConstraints Limits(float coord, float back, float forward)
    {
        var limits = new OneGrabTranslateTransformer.OneGrabTranslateConstraints
        {
            ConstraintsAreRelative = false,
            MinX = SlideGrab.Pin(columns ? coord + back : 0f),
            MaxX = SlideGrab.Pin(columns ? coord + forward : 0f),
            MinY = SlideGrab.Pin(transform.localPosition.y),
            MaxY = SlideGrab.Pin(transform.localPosition.y),
            MinZ = SlideGrab.Pin(columns ? 0f : coord + back),
            MaxZ = SlideGrab.Pin(columns ? 0f : coord + forward)
        };
        return limits;
    }
}
