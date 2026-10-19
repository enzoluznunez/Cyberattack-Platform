using UnityEngine;

// A bar on a sheet. It has no collider: a fingertip finds the bar it is at
// by where it stands among the sheet's lines (SheetRaycast), so a sheet of
// thousands of bars puts nothing in the physics scene, and the sheet's grab
// has one box to test rather than a collider per bar.
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class CreateCube : MonoBehaviour
{
    public int visRow = -1;
    public int visCol = -1;
    public int dataRow = -1;
    public int dataCol = -1;

    public CreateSheet Sheet { get; private set; }

    public void SetSheet(CreateSheet sheet) => Sheet = sheet;

    private MeshFilter _filter;
    private MeshRenderer _renderer;
    private Mesh _mesh;

    private readonly Color[] _colors = new Color[24];
    private Color _baseColor = Color.white;
    private Vector3 _baseScale = Vector3.one;
    private float _swell;
    private bool _painted;

    // Drawn only while its line is on the sheet (visible) and its sheet is up
    // (shown); touchable only while drawn, while its sheet takes pokes, and
    // while it is not rising or falling with its whole sheet (moving), as a
    // sheet mid-switch is not touched.
    private bool _visible = true;
    private bool _shown = true;
    private bool _pickable = true;
    private bool _moving;

    public bool Touchable => _visible && _shown && _pickable && !_moving;

    // The point of the bar nearest a world point, the point itself when it
    // is inside: the bar is the unit cube its transform stretches.
    public Vector3 ClosestPoint(Vector3 world)
    {
        Vector3 p = transform.InverseTransformPoint(world);
        p.x = Mathf.Clamp(p.x, -0.5f, 0.5f);
        p.y = Mathf.Clamp(p.y, -0.5f, 0.5f);
        p.z = Mathf.Clamp(p.z, -0.5f, 0.5f);
        return transform.TransformPoint(p);
    }

    public bool Contains(Vector3 world)
    {
        Vector3 p = transform.InverseTransformPoint(world);
        return Mathf.Abs(p.x) <= 0.5f && Mathf.Abs(p.y) <= 0.5f && Mathf.Abs(p.z) <= 0.5f;
    }

    // The colour it is drawn in, before any lighting.
    public Color BaseColor => _baseColor;

    public bool IsVisible => _visible;

    private static readonly Vector3[] Verts =
    {
        new Vector3(-0.5f,  0.5f, -0.5f), new Vector3( 0.5f,  0.5f, -0.5f),
        new Vector3( 0.5f,  0.5f,  0.5f), new Vector3(-0.5f,  0.5f,  0.5f),

        new Vector3(-0.5f, -0.5f,  0.5f), new Vector3( 0.5f, -0.5f,  0.5f),
        new Vector3( 0.5f, -0.5f, -0.5f), new Vector3(-0.5f, -0.5f, -0.5f),

        new Vector3(-0.5f, -0.5f, -0.5f), new Vector3( 0.5f, -0.5f, -0.5f),
        new Vector3( 0.5f,  0.5f, -0.5f), new Vector3(-0.5f,  0.5f, -0.5f),

        new Vector3( 0.5f, -0.5f,  0.5f), new Vector3(-0.5f, -0.5f,  0.5f),
        new Vector3(-0.5f,  0.5f,  0.5f), new Vector3( 0.5f,  0.5f,  0.5f),

        new Vector3(-0.5f, -0.5f,  0.5f), new Vector3(-0.5f, -0.5f, -0.5f),
        new Vector3(-0.5f,  0.5f, -0.5f), new Vector3(-0.5f,  0.5f,  0.5f),

        new Vector3( 0.5f, -0.5f, -0.5f), new Vector3( 0.5f, -0.5f,  0.5f),
        new Vector3( 0.5f,  0.5f,  0.5f), new Vector3( 0.5f,  0.5f, -0.5f),
    };

    private static readonly int[] Tris =
    {
         0,  1,  2,  0,  2,  3,
         4,  5,  6,  4,  6,  7,
         8,  9, 10,  8, 10, 11,
        12, 13, 14, 12, 14, 15,
        16, 17, 18, 16, 18, 19,
        20, 21, 22, 20, 22, 23,
    };

    public void Init(Material material)
    {
        if (_filter == null) _filter = GetComponent<MeshFilter>();
        if (_renderer == null) _renderer = GetComponent<MeshRenderer>();

        transform.localRotation = Quaternion.identity;

        if (material != null) _renderer.sharedMaterial = material;

        if (_mesh == null)
        {
            _mesh = new Mesh { name = "CreateCube" };
            _mesh.SetVertices(Verts);
            _mesh.SetTriangles(Tris, 0, false);
            _mesh.bounds = new Bounds(Vector3.zero, Vector3.one);
            _filter.sharedMesh = _mesh;
        }
    }

    public void SetCell(int vRow, int vCol, int dRow, int dCol)
    {
        visRow = vRow;
        visCol = vCol;
        dataRow = dRow;
        dataCol = dCol;
    }

    public void SetBox(Vector3 localCenter, Vector3 size)
    {
        transform.SetLocalPositionAndRotation(localCenter, Quaternion.identity);
        _baseScale = new Vector3(
            Mathf.Max(size.x, 1e-4f),
            Mathf.Max(size.y, 1e-4f),
            Mathf.Max(size.z, 1e-4f));
        ApplyScale();
    }

    public void SetColor(Color color)
    {
        if (_painted && _baseColor == color) return;
        _baseColor = color;
        Paint(color);
    }

    public void SetHighlight(float swell)
    {
        if (Mathf.Approximately(_swell, swell)) return;
        _swell = swell;
        ApplyScale();
    }

    public void ClearHighlight() => SetHighlight(0f);

    private void ApplyScale()
    {
        float spread = 1f + Mathf.Max(_swell, 0f);
        transform.localScale = new Vector3(
            _baseScale.x * spread, _baseScale.y, _baseScale.z * spread);
    }

    private void Paint(Color color)
    {
        if (_mesh == null) return;
        for (int i = 0; i < _colors.Length; i++) _colors[i] = color;
        _mesh.SetColors(_colors);
        _painted = true;
    }

    public void SetVisible(bool visible)
    {
        if (_visible == visible) return;
        _visible = visible;
        Apply();
    }

    // The sheet's own showing, apart from whether this bar's line is on it.
    public void SetShown(bool shown)
    {
        if (_shown == shown) return;
        _shown = shown;
        Apply();
    }

    public void SetPickable(bool pickable)
    {
        if (_pickable == pickable) return;
        _pickable = pickable;
        Apply();
    }

    public void SetMoving(bool moving)
    {
        if (_moving == moving) return;
        _moving = moving;
        Apply();
    }

    private void Apply()
    {
        if (_renderer != null) _renderer.enabled = _visible && _shown;
    }

    private void OnDestroy()
    {
        if (_mesh != null) Destroy(_mesh);
    }
}
