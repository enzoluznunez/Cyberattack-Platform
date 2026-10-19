using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Draws, during the loading phase's last step, a tiny copy of everything the
// scene will draw, so the graphics driver does its first-draw work behind
// the loading text rather than the first time the sheet and the maps show.
// That work is of two kinds. A pipeline per shader, mesh layout and render
// state: one copy of each kind builds those. And a GPU buffer per mesh: every
// bar is its own mesh, so the first sheet up is thousands of them, and
// setting up their buffers on the frames it showed stalled the app for over
// a second after every launch. So every distinct mesh is drawn once too,
// a share a frame, as many as the frames keep up with.
//
// The meshes are taken from the scene as it stands once everything is
// built: every mesh renderer, hidden ones included (the sheet waiting to
// rise, every map, the strips and copies put away). They are looked through
// a share a frame, and without making garbage, as one pass over all of them
// in a frame grew the heap for a third of a second.
public static class GraphicsWarm
{
    private struct Draw
    {
        public Mesh mesh;
        public int submesh;
        public RenderParams parameters;
        public float size;
    }

    private static readonly List<Draw> Kinds = new List<Draw>();
    private static readonly List<Draw> Meshes = new List<Draw>();
    private static readonly HashSet<(int, MeshTopology, int, ShadowCastingMode, bool, int)> SeenKinds =
        new HashSet<(int, MeshTopology, int, ShadowCastingMode, bool, int)>();
    private static readonly HashSet<int> SeenMeshes = new HashSet<int>();
    private static readonly List<Material> Materials = new List<Material>();
    private static readonly List<VertexAttributeDescriptor> Attributes = new List<VertexAttributeDescriptor>();

    private static MeshRenderer[] _renderers;
    private static int _scanned;
    private static int _kindFrames;
    private static int _drawn;
    private static int _batch;
    private static int _settleFrames;
    private static int _frames;
    private static float _started;
    private static bool _done;

    // How far ahead of the eyes the copies stand: just behind the loading
    // text (LoadingPhase), and each this wide across, a speck at most.
    private const float Ahead = 1.25f;
    private const float Width = 0.001f;

    // Renderers looked through a frame.
    private const int ScanPerFrame = 2000;
    // Frames each kind is drawn for: the first builds what each needs, the
    // rest let that work finish.
    private const int KindFrames = 4;
    // Meshes drawn a frame: starting here, halved after a frame longer than
    // SlowMs, half again as many after one that kept up, within these.
    private const int FirstBatch = 64;
    private const int LeastBatch = 16;
    private const int MostBatch = 1024;
    private const float SlowMs = 25f;
    // Frames waited once everything is drawn, for the last of it to finish
    // before the reveal.
    private const int SettleFrames = 4;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        Kinds.Clear();
        Meshes.Clear();
        SeenKinds.Clear();
        SeenMeshes.Clear();
        _renderers = null;
        _scanned = 0;
        _kindFrames = 0;
        _drawn = 0;
        _batch = FirstBatch;
        _settleFrames = 0;
        _frames = 0;
        _done = false;
    }

    // One frame's share of the warming. True once all of it is done.
    public static bool Step()
    {
        if (_done) return true;
        Transform cam = CameraRig.MainTransform;
        if (cam == null) return false;

        if (_renderers == null)
        {
            _started = Time.unscaledTime;
            _batch = FirstBatch;
            _renderers = Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        }
        _frames++;

        if (_scanned < _renderers.Length)
        {
            int end = Mathf.Min(_scanned + ScanPerFrame, _renderers.Length);
            for (; _scanned < end; _scanned++) Collect(_renderers[_scanned]);
            if (_scanned == _renderers.Length)
                Debug.Log($"[GraphicsWarm] Drawing {Kinds.Count} kinds of thing and {Meshes.Count} meshes once, " +
                          $"from {_renderers.Length} renderers.");
            return false;
        }

        Vector3 at = cam.position + cam.forward * Ahead;
        Quaternion facing = cam.rotation;

        if (_kindFrames < KindFrames)
        {
            for (int i = 0; i < Kinds.Count; i++) Render(Kinds[i], at, facing);
            _kindFrames++;
            return false;
        }

        if (_drawn < Meshes.Count)
        {
            // The length of the frame before says whether the last share kept up.
            if (_drawn > 0)
            {
                bool slow = Time.unscaledDeltaTime * 1000f > SlowMs;
                _batch = slow ? Mathf.Max(LeastBatch, _batch / 2) : Mathf.Min(MostBatch, _batch + _batch / 2);
            }
            int end = Mathf.Min(_drawn + _batch, Meshes.Count);
            for (; _drawn < end; _drawn++) Render(Meshes[_drawn], at, facing);
            return false;
        }

        if (_settleFrames++ < SettleFrames) return false;

        _done = true;
        Debug.Log($"[GraphicsWarm] Drew {Meshes.Count} meshes in {_frames} frames, " +
                  $"{Time.unscaledTime - _started:F1} s.");
        _renderers = null;
        Kinds.Clear();
        Meshes.Clear();
        SeenKinds.Clear();
        SeenMeshes.Clear();
        return true;
    }

    private static void Render(Draw d, Vector3 at, Quaternion facing)
    {
        if (d.mesh == null || d.parameters.material == null) return;
        Graphics.RenderMesh(d.parameters, d.mesh, d.submesh, Matrix4x4.TRS(at, facing, Vector3.one * d.size));
    }

    private static void Collect(MeshRenderer r)
    {
        if (r == null) return;
        MeshFilter filter = r.GetComponent<MeshFilter>();
        Mesh mesh = filter != null ? filter.sharedMesh : null;
        if (mesh == null || mesh.vertexCount == 0) return;

        r.GetSharedMaterials(Materials);
        int submeshes = Mathf.Min(Materials.Count, mesh.subMeshCount);
        if (submeshes == 0) return;

        mesh.GetVertexAttributes(Attributes);
        int layout = 17;
        for (int i = 0; i < Attributes.Count; i++)
        {
            VertexAttributeDescriptor a = Attributes[i];
            layout = layout * 31 + ((int)a.attribute << 16 | (int)a.format << 8 | a.dimension << 4 | a.stream);
        }

        float size = Width / Mathf.Max(mesh.bounds.size.magnitude, 1e-4f);
        bool newMesh = SeenMeshes.Add(mesh.GetInstanceID());
        for (int s = 0; s < submeshes; s++)
        {
            Material material = Materials[s];
            if (material == null) continue;

            bool newKind = SeenKinds.Add((material.GetInstanceID(), mesh.GetTopology(s), r.gameObject.layer,
                                          r.shadowCastingMode, r.receiveShadows, layout));
            if (!newKind && !newMesh) continue;

            var draw = new Draw
            {
                mesh = mesh,
                submesh = s,
                size = size,
                parameters = new RenderParams(material)
                {
                    layer = r.gameObject.layer,
                    shadowCastingMode = r.shadowCastingMode,
                    receiveShadows = r.receiveShadows,
                    renderingLayerMask = r.renderingLayerMask
                }
            };
            if (newKind) Kinds.Add(draw);
            // One draw of a mesh sets up its buffers, whatever it is drawn with.
            if (newMesh)
            {
                Meshes.Add(draw);
                newMesh = false;
            }
        }
    }
}
