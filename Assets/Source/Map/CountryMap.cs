using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// One country's map, built as solid meshes in the bars' own material: the
// country's land is a slab cut to its real outline, and every city a breached
// company is headquartered in stands on it as a short round column. The map
// stands in its local XY plane, east along +X and north along +Y, centred on
// its outline; its back is at z = 0 and its face toward the viewer, at -Z.
//
// Every map shares one scale, so a larger country is a larger map, and every
// dot shares one too: a dot's area is its city's breach count, so two dots of
// one size mean the same count on any map. Each smaller dot stands a hair
// taller than the larger ones before it, so a small city is never buried
// inside a large neighbour.
public class CountryMap : MonoBehaviour
{
    public struct Look
    {
        public float metresPerKm;
        // A one-breach city's radius; a city of n breaches is √n times wider.
        public float dotRadius;
        public float thickness;
        public float dotHeight;
        public float coastWidth;
        public Color land;
        public Color landSide;
        public Color coast;
        public Color dot;
        public Color dotSide;
        public Material material;
    }

    public struct Dot
    {
        public Vector2 centre;
        public float radius;
        public BreachMap.City city;
    }

    private const int DotSegments = 32;

    // How far each smaller dot stands above the one before it, and how far
    // the coast line sits off the face: enough to never fight for depth.
    private const float DotStep = 0.00002f;
    private const float CoastLift = 0.0002f;

    private readonly List<Dot> _dots = new List<Dot>();

    public MapAtlas.Country Shape { get; private set; }
    public BreachMap.Country Data { get; private set; }
    public IReadOnlyList<Dot> Dots => _dots;

    // The outline's size in metres, at the shared scale.
    public Vector2 Size { get; private set; }

    // How far the face stands toward the viewer from the back, in metres.
    public float Thickness { get; private set; }

    public static CountryMap Build(Transform parent, MapAtlas.Country shape, BreachMap.Country data, Look look)
    {
        var go = new GameObject($"Map_{shape.code}");
        go.transform.SetParent(parent, false);
        var map = go.AddComponent<CountryMap>();
        map.Shape = shape;
        map.Data = data;
        map.Size = shape.bounds.size * look.metresPerKm;
        map.Thickness = look.thickness;

        var mesh = new Builder();
        Vector2 centre = shape.bounds.center;
        map.AddLand(mesh, look, centre);
        map.AddDots(mesh, look, centre);

        go.AddComponent<MeshFilter>().sharedMesh = mesh.Finish(shape.code);
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = look.material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        return map;
    }

    // The dot under a point in the map's plane, or false. Smaller dots stand
    // on top, so the smallest that holds the point is the one seen there.
    public bool TryDotAt(Vector2 local, float slack, out Dot found)
    {
        for (int i = _dots.Count - 1; i >= 0; i--)
        {
            if ((_dots[i].centre - local).magnitude > _dots[i].radius + slack) continue;
            found = _dots[i];
            return true;
        }
        found = default;
        return false;
    }

    // The face and the back as the outline's own triangles, a wall along every
    // ring of the outline (coasts and the shores of lakes alike), and a thin
    // line around the face where the coast is.
    private void AddLand(Builder mesh, Look look, Vector2 centre)
    {
        float s = look.metresPerKm, front = -look.thickness;
        float[] v = Shape.vertices;
        int count = v.Length / 2;

        foreach (float z in new[] { front, 0f })
        {
            int at = mesh.Count;
            for (int i = 0; i < count; i++)
                mesh.Vertex(new Vector3((v[2 * i] - centre.x) * s, (v[2 * i + 1] - centre.y) * s, z), look.land);
            int[] t = Shape.triangles;
            for (int i = 0; i < t.Length; i += 3) mesh.Triangle(at + t[i], at + t[i + 1], at + t[i + 2]);
        }

        float half = look.coastWidth * 0.5f;
        foreach (float[] ring in Shape.outlines)
        {
            int n = ring.Length / 2;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                var p = new Vector2((ring[2 * i] - centre.x) * s, (ring[2 * i + 1] - centre.y) * s);
                var q = new Vector2((ring[2 * j] - centre.x) * s, (ring[2 * j + 1] - centre.y) * s);
                if ((q - p).sqrMagnitude < 1e-12f) continue;

                mesh.Quad(new Vector3(p.x, p.y, 0f), new Vector3(q.x, q.y, 0f),
                          new Vector3(q.x, q.y, front), new Vector3(p.x, p.y, front), look.landSide);

                Vector2 side = new Vector2(p.y - q.y, q.x - p.x).normalized * half;
                float z = front - CoastLift;
                mesh.Quad(new Vector3(p.x - side.x, p.y - side.y, z), new Vector3(p.x + side.x, p.y + side.y, z),
                          new Vector3(q.x + side.x, q.y + side.y, z), new Vector3(q.x - side.x, q.y - side.y, z),
                          look.coast);
            }
        }
    }

    // A round column per city, standing out of the face.
    private void AddDots(Builder mesh, Look look, Vector2 centre)
    {
        _dots.Clear();
        if (Data != null)
            foreach (BreachMap.City city in Data.cities)
            {
                if (!Shape.TryPlace(city.lon, city.lat, out Vector2 km))
                {
                    Debug.LogWarning($"[CountryMap] {city.name} is off the {Shape.name} map; it has no dot.");
                    continue;
                }
                _dots.Add(new Dot
                {
                    centre = (km - centre) * look.metresPerKm,
                    radius = look.dotRadius * Mathf.Sqrt(Mathf.Max(city.breaches, 1)),
                    city = city
                });
            }
        _dots.Sort((a, b) => b.radius.CompareTo(a.radius));

        float face = -look.thickness;
        for (int d = 0; d < _dots.Count; d++)
        {
            Dot dot = _dots[d];
            float top = face - look.dotHeight - d * DotStep;

            int hub = mesh.Count;
            mesh.Vertex(new Vector3(dot.centre.x, dot.centre.y, top), look.dot);
            for (int k = 0; k < DotSegments; k++)
                mesh.Vertex(Rim(dot, k, top), look.dot);
            for (int k = 0; k < DotSegments; k++)
                mesh.Triangle(hub, hub + 1 + k, hub + 1 + (k + 1) % DotSegments);

            for (int k = 0; k < DotSegments; k++)
            {
                int next = (k + 1) % DotSegments;
                mesh.Quad(Rim(dot, k, face), Rim(dot, next, face), Rim(dot, next, top), Rim(dot, k, top), look.dotSide);
            }
        }
    }

    private static Vector3 Rim(Dot dot, int k, float z)
    {
        float a = k * Mathf.PI * 2f / DotSegments;
        return new Vector3(dot.centre.x + Mathf.Cos(a) * dot.radius, dot.centre.y + Mathf.Sin(a) * dot.radius, z);
    }

    private sealed class Builder
    {
        private readonly List<Vector3> _vertices = new List<Vector3>();
        private readonly List<Color> _colors = new List<Color>();
        private readonly List<int> _triangles = new List<int>();

        public int Count => _vertices.Count;

        public void Vertex(Vector3 at, Color color)
        {
            _vertices.Add(at);
            // Handed over as the bars' colours are, so a map and the bars
            // beside it read as one palette.
            _colors.Add(color);
        }

        public void Triangle(int a, int b, int c)
        {
            _triangles.Add(a); _triangles.Add(b); _triangles.Add(c);
        }

        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color)
        {
            int at = Count;
            Vertex(a, color); Vertex(b, color); Vertex(c, color); Vertex(d, color);
            Triangle(at, at + 1, at + 2);
            Triangle(at, at + 2, at + 3);
        }

        public Mesh Finish(string name)
        {
            var mesh = new Mesh { name = $"{name} map" };
            if (_vertices.Count > ushort.MaxValue) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(_vertices);
            mesh.SetColors(_colors);
            mesh.SetTriangles(_triangles, 0, true);
            return mesh;
        }
    }

    private void OnDestroy()
    {
        MeshFilter filter = GetComponent<MeshFilter>();
        if (filter != null && filter.sharedMesh != null) Destroy(filter.sharedMesh);
    }
}
