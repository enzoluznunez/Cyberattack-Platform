using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// One map, the nation's or a region's, built as solid meshes in the bars' own
// material: the land is a slab cut to its real outline, and every metro a
// breached company is headquartered in lies on its face as a flat disc, its
// pin. The map stands in its local XY plane, east along +X and north along
// +Y, centred on its outline; its back is at z = 0 and its face toward the
// viewer, at -Z.
//
// Every map is drawn in the national map's kilometres, so a pin stands at one
// place on all of them: its cities' centre, weighted by their breaches. On a
// region's map a pin is a metro, in its metro's colour, the colour its bars on
// the sheet share, and a pin reaching over the region's border is cut along
// it. The pipeline merged the metros so that no pin's centre lies inside
// another's on its own region's map. The national map draws the pins smaller
// against the land, so there it merges them again by the same rule, within
// each region, and colours each by its region; the borders between regions
// are drawn across the land.
//
// Each map has its own scale, given in its Look, but every pin shares one: a
// pin's area is its breach count, so two pins of one size mean the same count
// on any map. Each smaller pin lies a hair above the larger ones before it,
// so a small metro is never buried under a large neighbour.
public class RegionMap : MonoBehaviour
{
    public struct Look
    {
        public float metresPerKm;
        // A one-breach city's radius; a city of n breaches is √n times wider.
        public float dotRadius;
        public float thickness;
        public float coastWidth;
        public float borderWidth;
        public Color land;
        public Color landSide;
        public Color coast;
        public Color border;
        public Material material;
    }

    public struct Dot
    {
        public Vector2 centre;
        public float radius;
        // What the pin is called: its metro, or on the national map the
        // largest metro of those merged into it.
        public string name;
        public string region;
        public int breaches;
        public Color color;
        // The cities whose breaches it holds.
        public List<string> cities;
        // Whether the region's border cuts it, so only part of it is drawn.
        public bool cut;
    }

    // Enough for a round edge on a pin a few millimetres across; more is only
    // more vertices, a few hundred cities over.
    private const int DotSegments = 16;

    // How far the coast and border lines sit off the face, how far the pins
    // sit above them, and how far each smaller pin lies above the one before
    // it: enough to never fight for depth, and too little to see.
    private const float CoastLift = 0.0002f;
    private const float DotLift = 0.0004f;
    private const float DotStep = 0.00002f;

    private readonly List<Dot> _dots = new List<Dot>();
    private Vector2[] _cell;

    public MapAtlas.Area Area { get; private set; }

    // The outline's size in metres, at this map's scale.
    public Vector2 Size { get; private set; }

    // How far the face stands toward the viewer from the back, in metres.
    public float Thickness { get; private set; }

    public IReadOnlyList<Dot> Dots => _dots;

    // 'regionColors' colours the national map's pins; a region's map colours
    // each pin by its metro.
    public static RegionMap Build(Transform parent, MapAtlas atlas, MapAtlas.Area area,
        IReadOnlyList<BreachMap.City> cities, IReadOnlyDictionary<string, Color> regionColors, Look look)
    {
        var go = new GameObject($"Map_{area.name}");
        go.transform.SetParent(parent, false);
        var map = go.AddComponent<RegionMap>();
        map.Area = area;
        map.Size = area.bounds.size * look.metresPerKm;
        map.Thickness = look.thickness;

        var mesh = new Builder();
        Vector2 centre = area.bounds.center;
        map.AddLand(mesh, look, centre);
        if (area.IsNational) map.AddBorders(mesh, look, centre);
        map.AddDots(mesh, atlas, cities, regionColors, look, centre);

        go.AddComponent<MeshFilter>().sharedMesh = mesh.Finish(area.name);
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = look.material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        return map;
    }

    // The pin under a point in the map's plane, or false. Smaller pins stand
    // on top, so the smallest that holds the point is the one seen there; a
    // pin cut by the border is only there on this side of it.
    public bool TryDotAt(Vector2 local, float slack, out Dot found)
    {
        for (int i = _dots.Count - 1; i >= 0; i--)
        {
            float reach = _dots[i].radius + slack;
            if ((_dots[i].centre - local).sqrMagnitude > reach * reach) continue;
            if (_dots[i].cut && !Inside(_cell, local)) continue;
            found = _dots[i];
            return true;
        }
        found = default;
        return false;
    }

    // The pin holding a city, or false when this map has none.
    public bool TryDotOf(string city, out Dot found)
    {
        foreach (Dot dot in _dots)
            if (dot.cities.Contains(city)) { found = dot; return true; }
        found = default;
        return false;
    }

    // The face and the back as the outline's own triangles, a wall along every
    // ring of the outline (coasts and the shores of lakes alike), and a thin
    // line around the face where the coast is.
    private void AddLand(Builder mesh, Look look, Vector2 centre)
    {
        float s = look.metresPerKm, front = -look.thickness;
        float[] v = Area.vertices;
        int count = v.Length / 2;

        foreach (float z in new[] { front, 0f })
        {
            int at = mesh.Count;
            for (int i = 0; i < count; i++)
                mesh.Vertex(new Vector3((v[2 * i] - centre.x) * s, (v[2 * i + 1] - centre.y) * s, z), look.land);
            int[] t = Area.triangles;
            for (int i = 0; i < t.Length; i += 3) mesh.Triangle(at + t[i], at + t[i + 1], at + t[i + 2]);
        }

        foreach (float[] ring in Area.outlines)
        {
            int n = ring.Length / 2;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                Vector2 p = Local(ring, i, centre, s), q = Local(ring, j, centre, s);
                if ((q - p).sqrMagnitude < 1e-12f) continue;

                mesh.Quad(new Vector3(p.x, p.y, 0f), new Vector3(q.x, q.y, 0f),
                          new Vector3(q.x, q.y, front), new Vector3(p.x, p.y, front), look.landSide);
                Line(mesh, p, q, look.coastWidth, front - CoastLift, look.coast);
            }
        }
    }

    // The lines between regions, drawn across the face as the coast is.
    private void AddBorders(Builder mesh, Look look, Vector2 centre)
    {
        float s = look.metresPerKm, z = -look.thickness - CoastLift;
        foreach (float[] line in Area.borders)
            for (int i = 0; i + 1 < line.Length / 2; i++)
                Line(mesh, Local(line, i, centre, s), Local(line, i + 1, centre, s), look.borderWidth, z, look.border);
    }

    private static Vector2 Local(float[] xy, int i, Vector2 centre, float s) =>
        new Vector2((xy[2 * i] - centre.x) * s, (xy[2 * i + 1] - centre.y) * s);

    private static void Line(Builder mesh, Vector2 p, Vector2 q, float width, float z, Color color)
    {
        if ((q - p).sqrMagnitude < 1e-12f) return;
        Vector2 side = new Vector2(p.y - q.y, q.x - p.x).normalized * (width * 0.5f);
        mesh.Quad(new Vector3(p.x - side.x, p.y - side.y, z), new Vector3(p.x + side.x, p.y + side.y, z),
                  new Vector3(q.x + side.x, q.y + side.y, z), new Vector3(q.x - side.x, q.y - side.y, z), color);
    }

    // A flat disc per metro, lying on the face; on a region's map, every
    // metro whose pin reaches into the region, cut along its border.
    private void AddDots(Builder mesh, MapAtlas atlas, IReadOnlyList<BreachMap.City> cities,
        IReadOnlyDictionary<string, Color> regionColors, Look look, Vector2 centre)
    {
        float s = look.metresPerKm;
        if (Area.cell != null)
        {
            _cell = new Vector2[Area.cell.Length / 2];
            for (int i = 0; i < _cell.Length; i++) _cell[i] = Local(Area.cell, i, centre, s);
        }

        List<Pin> pins = Metros(atlas, cities, centre, s);
        if (Area.IsNational) MergeOverlaps(pins, look.dotRadius);

        foreach (Pin pin in pins)
        {
            var dot = new Dot
            {
                centre = pin.at,
                radius = RadiusOf(pin.breaches, look.dotRadius),
                name = pin.name,
                region = pin.region,
                breaches = pin.breaches,
                color = Area.IsNational && regionColors != null &&
                        regionColors.TryGetValue(pin.region, out Color regional)
                    ? regional
                    : pin.color,
                cities = pin.cities
            };

            if (_cell != null)
            {
                bool inside = Inside(_cell, dot.centre);
                float edge = DistanceToRing(_cell, dot.centre);
                if (!inside && edge >= dot.radius) continue;
                dot.cut = edge < dot.radius;
            }
            _dots.Add(dot);
        }
        _dots.Sort((a, b) => b.radius.CompareTo(a.radius));

        float face = -look.thickness - DotLift;
        var rim = new List<Vector2>(DotSegments * 2);
        for (int d = 0; d < _dots.Count; d++)
        {
            Dot dot = _dots[d];
            rim.Clear();
            for (int k = 0; k < DotSegments; k++)
            {
                float a = k * Mathf.PI * 2f / DotSegments;
                rim.Add(dot.centre + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * dot.radius);
            }
            List<Vector2> shape = dot.cut ? ClipToCircle(_cell, rim) : rim;
            if (shape.Count < 3) continue;

            Disc(mesh, shape, face - d * DotStep, dot.color);
        }
    }

    private static float RadiusOf(int breaches, float oneBreach) => oneBreach * Mathf.Sqrt(Mathf.Max(breaches, 1));

    // One pin before it is drawn: where it stands on this map, in metres.
    private sealed class Pin
    {
        public string name;
        public string region;
        public Color color;
        public Vector2 at;
        public int breaches;
        public List<string> cities = new List<string>();
    }

    // Every metro as one pin, at its cities' centre weighted by their breaches.
    private static List<Pin> Metros(MapAtlas atlas, IReadOnlyList<BreachMap.City> cities, Vector2 centre, float s)
    {
        var byMetro = new Dictionary<string, Pin>();
        var pins = new List<Pin>();
        foreach (BreachMap.City city in cities)
        {
            if (!atlas.TryPlace(city.lon, city.lat, out Vector2 km))
            {
                Debug.LogWarning($"[RegionMap] {city.name} is off the map; it has no pin.");
                continue;
            }
            string metro = string.IsNullOrEmpty(city.metro) ? city.name : city.metro;
            if (!byMetro.TryGetValue(metro, out Pin pin))
            {
                pin = new Pin { name = metro, region = city.region, color = city.color };
                byMetro[metro] = pin;
                pins.Add(pin);
            }
            int n = Mathf.Max(city.breaches, 1);
            pin.at = (pin.at * pin.breaches + (km - centre) * s * n) / (pin.breaches + n);
            pin.breaches += n;
            pin.cities.Add(city.name);
        }
        return pins;
    }

    // The pipeline's rule again, at this map's scale: within a region, the
    // largest pin takes in the first smaller one whose centre lies inside it,
    // and the two become one at their weighted centre, as wide as their
    // breaches together; until no centre lies inside another pin. Named after
    // the largest pin merged.
    private static void MergeOverlaps(List<Pin> pins, float oneBreach)
    {
        bool merged = true;
        while (merged)
        {
            merged = false;
            pins.Sort((a, b) => a.breaches != b.breaches
                ? b.breaches.CompareTo(a.breaches)
                : string.CompareOrdinal(a.name, b.name));
            for (int i = 0; i < pins.Count && !merged; i++)
            {
                Pin big = pins[i];
                float reach = RadiusOf(big.breaches, oneBreach);
                for (int j = i + 1; j < pins.Count; j++)
                {
                    Pin small = pins[j];
                    if (small.region != big.region || (small.at - big.at).sqrMagnitude >= reach * reach) continue;
                    int n = big.breaches + small.breaches;
                    big.at = (big.at * big.breaches + small.at * small.breaches) / n;
                    big.breaches = n;
                    big.cities.AddRange(small.cities);
                    pins.RemoveAt(j);
                    merged = true;
                    break;
                }
            }
        }
    }

    // A flat polygon lying on the face.
    private static void Disc(Builder mesh, List<Vector2> shape, float z, Color color)
    {
        if (SignedArea(shape) < 0f) shape.Reverse();

        int at = mesh.Count;
        foreach (Vector2 p in shape) mesh.Vertex(new Vector3(p.x, p.y, z), color);
        foreach ((int a, int b, int c) in EarClip(shape)) mesh.Triangle(at + a, at + b, at + c);
    }

    // The region's cell clipped to a pin's circle: Sutherland-Hodgman, with
    // the circle, which is convex, as the window and the cell as the subject.
    private static List<Vector2> ClipToCircle(Vector2[] cell, List<Vector2> circle)
    {
        var output = new List<Vector2>(cell);
        int n = circle.Count;
        for (int e = 0; e < n && output.Count > 0; e++)
        {
            Vector2 a = circle[e], b = circle[(e + 1) % n];
            var input = output;
            output = new List<Vector2>(input.Count + 4);
            for (int i = 0; i < input.Count; i++)
            {
                Vector2 p = input[i], q = input[(i + 1) % input.Count];
                bool pIn = Cross(a, b, p) >= 0f, qIn = Cross(a, b, q) >= 0f;
                if (pIn) output.Add(p);
                if (pIn != qIn) output.Add(Intersect(a, b, p, q));
            }
        }
        return output;
    }

    private static float Cross(Vector2 a, Vector2 b, Vector2 p) => (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);

    private static Vector2 Intersect(Vector2 a, Vector2 b, Vector2 p, Vector2 q)
    {
        float da = Cross(a, b, p), db = Cross(a, b, q);
        return Vector2.Lerp(p, q, da / (da - db));
    }

    private static float SignedArea(List<Vector2> ring)
    {
        float area = 0f;
        for (int i = 0; i < ring.Count; i++)
        {
            Vector2 p = ring[i], q = ring[(i + 1) % ring.Count];
            area += p.x * q.y - q.x * p.y;
        }
        return area * 0.5f;
    }

    // Triangles over a simple counter-clockwise polygon, by cutting off one
    // ear at a time. A cut pin is a few dozen points, so this is cheap.
    private static IEnumerable<(int, int, int)> EarClip(List<Vector2> ring)
    {
        var left = new List<int>(ring.Count);
        for (int i = 0; i < ring.Count; i++) left.Add(i);

        int guard = ring.Count * ring.Count;
        while (left.Count > 3 && guard-- > 0)
        {
            bool cut = false;
            for (int i = 0; i < left.Count; i++)
            {
                int a = left[(i + left.Count - 1) % left.Count], b = left[i], c = left[(i + 1) % left.Count];
                if (Cross(ring[a], ring[b], ring[c]) <= 0f) continue;
                bool empty = true;
                foreach (int k in left)
                {
                    if (k == a || k == b || k == c) continue;
                    if (Cross(ring[a], ring[b], ring[k]) >= 0f && Cross(ring[b], ring[c], ring[k]) >= 0f &&
                        Cross(ring[c], ring[a], ring[k]) >= 0f) { empty = false; break; }
                }
                if (!empty) continue;
                yield return (a, b, c);
                left.RemoveAt(i);
                cut = true;
                break;
            }
            // Only slivers are left, where the cut met itself: drop one point.
            if (!cut) left.RemoveAt(0);
        }
        if (left.Count == 3) yield return (left[0], left[1], left[2]);
    }

    private static bool Inside(Vector2[] ring, Vector2 p)
    {
        if (ring == null) return true;
        bool inside = false;
        for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
        {
            Vector2 a = ring[i], b = ring[j];
            if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x)
                inside = !inside;
        }
        return inside;
    }

    private static float DistanceToRing(Vector2[] ring, Vector2 p)
    {
        float best = float.MaxValue;
        for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
        {
            Vector2 a = ring[j], ab = ring[i] - a;
            float t = ab.sqrMagnitude > 0f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
            best = Mathf.Min(best, (a + ab * t - p).sqrMagnitude);
        }
        return Mathf.Sqrt(best);
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
