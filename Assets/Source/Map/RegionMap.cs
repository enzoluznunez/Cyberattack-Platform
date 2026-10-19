using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// One map, the nation's or a region's, drawn flat as the tool panel is, in the
// bars' own material: the land a white face cut to its real outline and edged
// in thin black coast lines, over a black back. The map stands in its local XY
// plane, east along +X and north along +Y, centred on its outline; its back is
// at z = 0 and its face a hair toward the viewer, at FaceZ.
//
// Every map is drawn in the national map's kilometres. The national map fills
// each region with its colour, with no lines between them, and has no pins:
// a fingertip there names a region. On a region's map every metro is a flat
// disc, its pin, at its cities' centre weighted by their breaches, in the
// colour its bars on the sheet share, drawn a true circle by the map's shader
// rather than as a polygon; a pin reaching over the region's border is cut
// along it. A metro with no colour of its own is grey, darker the more
// breaches it holds against the region's largest metro: white would be none
// and black the largest, which always has a colour, so no pin is either.
// The pipeline merged the metros so that no pin's centre lies inside
// another's on its region's map.
//
// What is selected, a region on the national map or a pin on a region's, is
// edged in black: the region along its own outline, the pin by a ring.
//
// Each map has its own scale, given in its Look, but every pin shares one: a
// pin's area is its breach count, so two pins of one size mean the same count
// on any map. Each smaller pin lies a hair above the larger ones before it,
// so a small metro is never buried under a large neighbour.
//
// However large the Scale slider draws the map, the lines, the selection
// edges and the depth between the layers keep their real size on the
// headset; only the land and the pins scale. The map's shader widens them by
// the material's _Grow, what one of the map's units is in the Look's widths
// (SetGrow), so a
// coast line is never thinner than a pixel at the smallest scale nor a band
// at the largest. Widths in the Look are those sizes at a _Grow of 1.
public class RegionMap : MonoBehaviour
{
    // How far the face stands in front of the back, in metres: enough that the
    // two never fight for depth, and too little to see as thickness.
    public const float FaceZ = -0.001f;

    public struct Look
    {
        public float metresPerKm;
        // A one-breach metro's radius; a metro of n breaches is √n times wider.
        public float dotRadius;
        public float coastWidth;
        // The black edge around what is selected.
        public float borderWidth;
        // The most _Grow will be: a ring the border cuts is cut wide enough
        // for its edge at that.
        public float maxGrow;
        public Color land;
        public Color back;
        public Color coast;
        public Color border;
        public Material material;
    }

    public struct Dot
    {
        public Vector2 centre;
        public float radius;
        // The metro it stands for.
        public string name;
        public string region;
        public int breaches;
        public Color color;
        // Whether the region's border cuts it, so only part of it is drawn.
        public bool cut;
        // How far in front of the back it lies.
        public float z;
    }

    // How far the region fills, the lines and the pins sit in front of the
    // face, and how far each smaller pin lies above the one before it.
    private const float FillLift = 0.0001f;
    private const float CoastLift = 0.0002f;
    private const float DotLift = 0.0004f;
    private const float DotStep = 0.00002f;

    private readonly List<Dot> _dots = new List<Dot>();
    private Vector2[] _cell;
    // The cell as triangles, three points each, a pin cut by the border is
    // cut to.
    private Vector2[] _cellTriangles;
    private readonly List<(string region, Vector2[] cell)> _regions = new List<(string, Vector2[])>();

    private MapAtlas _atlas;
    private Look _look;
    private Vector2 _centre;
    // The black edge drawn around each thing selected so far, made the first
    // time it is selected; only the one selected now is shown.
    private readonly Dictionary<string, MeshRenderer> _edges = new Dictionary<string, MeshRenderer>();
    private MeshRenderer _renderer;
    private bool _visible = true;
    private string _selected;

    public MapAtlas.Area Area { get; private set; }

    // The outline's size in metres, at this map's scale.
    public Vector2 Size { get; private set; }

    // 'regionColors' fills the national map's regions; a region's map colours
    // each pin by its metro.
    public static RegionMap Build(Transform parent, MapAtlas atlas, MapAtlas.Area area,
        IReadOnlyList<BreachMap.City> cities, IReadOnlyDictionary<string, Color> regionColors, Look look)
    {
        var go = new GameObject($"Map_{area.name}");
        go.transform.SetParent(parent, false);
        var map = go.AddComponent<RegionMap>();
        map.Area = area;
        map.Size = area.bounds.size * look.metresPerKm;
        map._atlas = atlas;
        map._look = look;

        var mesh = new Builder();
        Vector2 centre = area.bounds.center;
        map._centre = centre;
        map.AddLand(mesh, look, centre);
        if (area.IsNational) map.AddRegions(mesh, atlas, regionColors, look, centre);
        else map.AddDots(mesh, atlas, cities, look, centre);

        map._renderer = Draw(go, mesh, area.name, look.material);
        return map;
    }

    // What one of the map's units is in the Look's widths, for every map
    // drawn in this material: the panel's scale over the maps'.
    public static void SetGrow(Material material, float grow)
    {
        if (material != null) material.SetFloat(GrowId, grow);
    }

    private static readonly int GrowId = Shader.PropertyToID("_Grow");

    private static MeshRenderer Draw(GameObject go, Builder mesh, string name, Material material)
    {
        go.AddComponent<MeshFilter>().sharedMesh = mesh.Finish(name);
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        return renderer;
    }

    // Draws the map or stops drawing it, its selection edge with it. The map
    // stays active either way, so standing it up again costs nothing. Always
    // applied, since a panel that hides everything under it touches these too.
    public void SetVisible(bool on)
    {
        _visible = on;
        if (_renderer != null) _renderer.enabled = on;
        foreach (KeyValuePair<string, MeshRenderer> pair in _edges)
            if (pair.Value != null) pair.Value.enabled = on && pair.Key == _selected;
    }

    public bool Visible => _visible;

    // Edges what is selected in black: on the national map a region ('US
    // West'), on a region's map one of its metros by name. Null, or anything
    // the map does not hold, edges nothing.
    public void Select(string name)
    {
        if (_selected == name) return;
        if (_selected != null && _edges.TryGetValue(_selected, out MeshRenderer was) && was != null)
            was.enabled = false;
        _selected = name;
        if (name == null) return;

        if (!_edges.TryGetValue(name, out MeshRenderer edge) || edge == null)
            _edges[name] = edge = BuildEdge(name);
        if (edge != null) edge.enabled = _visible;
    }

    // Makes the edge of everything that can be selected on this map, put
    // away, so selecting one only shows it.
    public void PrebuildEdges()
    {
        var names = new List<string>();
        if (Area.IsNational)
        {
            foreach (MapAtlas.Area region in _atlas.Areas)
                if (!region.IsNational) names.Add(region.region);
        }
        else
        {
            foreach (Dot dot in _dots)
                if (dot.region == Area.region) names.Add(dot.name);
        }
        foreach (string name in names)
        {
            if (_edges.TryGetValue(name, out MeshRenderer made) && made != null) continue;
            MeshRenderer edge = BuildEdge(name);
            if (edge != null) edge.enabled = _visible && name == _selected;
            _edges[name] = edge;
        }
    }

    private MeshRenderer BuildEdge(string name)
    {
        var mesh = new Builder();
        if (Area.IsNational)
        {
            MapAtlas.Area region = _atlas.Find(name);
            if (region == null || region.IsNational) return null;
            AddOutline(mesh, region);
        }
        else
        {
            int at = _dots.FindIndex(d => d.name == name && d.region == Area.region);
            if (at < 0) return null;
            AddRing(mesh, _dots[at]);
        }

        var go = new GameObject($"Selected_{name}");
        go.transform.SetParent(transform, false);
        return Draw(go, mesh, $"{Area.name} {name} selected", _look.material);
    }

    // A region's own outline, which the national land was joined from, laid
    // over the national map in front of its coast.
    private void AddOutline(Builder mesh, MapAtlas.Area region)
    {
        AddRings(mesh, region.outlines, _centre, _look.metresPerKm, _look.borderWidth,
            FaceZ - CoastLift - FillLift, _look.border);
    }

    // A black disc a ring's width wider than the pin, just behind it and in
    // front of every larger pin, so it shows as a ring around it; cut along
    // the region's border as the pin is.
    // The ring's width is real, as a line's is; the pin inside it scales.
    private void AddRing(Builder mesh, Dot dot)
    {
        float z = dot.z + DotStep * 0.5f;
        if (dot.cut && _cellTriangles.Length > 0)
            CutDisc(mesh, dot.centre, dot.radius, _look.borderWidth, z, _look.border);
        else Round(mesh, dot.centre, dot.radius, _look.borderWidth, z, _look.border);
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

    // The region under a point on the national map, its land and the water
    // nearest it, or false off every region and on a region's map.
    public bool TryRegionAt(Vector2 local, out string region)
    {
        foreach ((string name, Vector2[] cell) in _regions)
            if (Inside(cell, local) && InsideBounds(local)) { region = name; return true; }
        region = null;
        return false;
    }

    private bool InsideBounds(Vector2 local) =>
        Mathf.Abs(local.x) <= Size.x * 0.5f && Mathf.Abs(local.y) <= Size.y * 0.5f;

    // The face as the outline's own triangles, the back as the same in black,
    // and a thin black line around the face where the coast is.
    private void AddLand(Builder mesh, Look look, Vector2 centre)
    {
        float s = look.metresPerKm;
        AddFill(mesh, Area.vertices, Area.triangles, centre, s, FaceZ, look.land);
        AddFill(mesh, Area.vertices, Area.triangles, centre, s, 0f, look.back);
        AddRings(mesh, Area.outlines, centre, s, look.coastWidth, FaceZ - CoastLift, look.coast);
    }

    // Each region's land laid over the national face in its colour: the
    // pieces the national land was joined from, so the colours cover it
    // exactly. Each region's share of the plane is kept, so a fingertip can be
    // told which region it is on.
    private void AddRegions(Builder mesh, MapAtlas atlas, IReadOnlyDictionary<string, Color> regionColors,
        Look look, Vector2 centre)
    {
        float s = look.metresPerKm, z = FaceZ - FillLift;
        foreach (MapAtlas.Fill fill in Area.fills)
        {
            Color color = regionColors != null && regionColors.TryGetValue(fill.region, out Color c) ? c : look.land;

            AddFill(mesh, fill.vertices, fill.triangles, centre, s, z, color);
        }

        foreach (MapAtlas.Area region in atlas.Areas)
        {
            if (region.IsNational || region.cell == null) continue;
            var cell = new Vector2[region.cell.Length / 2];
            for (int i = 0; i < cell.Length; i++) cell[i] = Local(region.cell, i, centre, s);
            _regions.Add((region.region, cell));
        }
    }

    // Flat triangles at depth z, in one colour.
    private static void AddFill(Builder mesh, float[] v, int[] t, Vector2 centre, float s, float z, Color color)
    {
        int at = mesh.Count;
        for (int i = 0; i < v.Length / 2; i++)
            mesh.Vertex(new Vector3((v[2 * i] - centre.x) * s, (v[2 * i + 1] - centre.y) * s, z), color);
        for (int i = 0; i < t.Length; i += 3) mesh.Triangle(at + t[i], at + t[i + 1], at + t[i + 2]);
    }

    // Closed rings drawn as lines of width w, with a round joint at each corner.
    private static void AddRings(Builder mesh, float[][] rings, Vector2 centre, float s, float w, float z, Color color)
    {
        foreach (float[] ring in rings)
        {
            int n = ring.Length / 2;
            for (int i = 0; i < n; i++)
            {
                Vector2 p = Local(ring, i, centre, s);
                Line(mesh, p, Local(ring, (i + 1) % n, centre, s), w, z, color);
                Joint(mesh, p, w * 0.5f, z, color);
            }
        }
    }

    private static Vector2 Local(float[] xy, int i, Vector2 centre, float s) =>
        new Vector2((xy[2 * i] - centre.x) * s, (xy[2 * i + 1] - centre.y) * s);

    // A round end where two lines meet, so a bend shows no notch between
    // their ends, as wide as the lines whatever the map's scale.
    private static void Joint(Builder mesh, Vector2 at, float radius, float z, Color color) =>
        Round(mesh, at, 0f, radius, z, color);

    // A disc of 'radius' in the map's units, which scales with it, and
    // 'grows' real metres more, which does not: a square the shader cuts to
    // a circle, its corners moving out as the disc grows.
    private static void Round(Builder mesh, Vector2 at, float radius, float grows, float z, Color color)
    {
        int first = mesh.Count;
        var radii = new Vector2(radius, grows);
        for (int k = 0; k < 4; k++)
        {
            Vector2 corner = Corners[k];
            mesh.Vertex(new Vector3(at.x + corner.x * radius, at.y + corner.y * radius, z), color,
                corner * radius, radii, corner * grows);
        }
        mesh.Triangle(first, first + 1, first + 2);
        mesh.Triangle(first, first + 2, first + 3);
    }

    private static readonly Vector2[] Corners =
        { new Vector2(-1f, -1f), new Vector2(-1f, 1f), new Vector2(1f, 1f), new Vector2(1f, -1f) };

    // A line 'width' real metres wide: its four corners stand on the line
    // itself and the shader moves each out along its side.
    private static void Line(Builder mesh, Vector2 p, Vector2 q, float width, float z, Color color)
    {
        if ((q - p).sqrMagnitude < 1e-12f) return;
        Vector2 side = new Vector2(p.y - q.y, q.x - p.x).normalized * (width * 0.5f);
        int at = mesh.Count;
        mesh.Vertex(new Vector3(p.x, p.y, z), color, Vector2.zero, Vector2.zero, -side);
        mesh.Vertex(new Vector3(p.x, p.y, z), color, Vector2.zero, Vector2.zero, side);
        mesh.Vertex(new Vector3(q.x, q.y, z), color, Vector2.zero, Vector2.zero, side);
        mesh.Vertex(new Vector3(q.x, q.y, z), color, Vector2.zero, Vector2.zero, -side);
        mesh.Triangle(at, at + 1, at + 2);
        mesh.Triangle(at, at + 2, at + 3);
    }

    // A flat disc per metro whose pin reaches into the region, lying on the
    // face and cut along the region's border.
    private void AddDots(Builder mesh, MapAtlas atlas, IReadOnlyList<BreachMap.City> cities, Look look, Vector2 centre)
    {
        float s = look.metresPerKm;
        if (Area.cell != null)
        {
            _cell = new Vector2[Area.cell.Length / 2];
            for (int i = 0; i < _cell.Length; i++) _cell[i] = Local(Area.cell, i, centre, s);
        }
        int[] ct = Area.cellTriangles;
        _cellTriangles = new Vector2[ct.Length];
        for (int i = 0; i < ct.Length; i++) _cellTriangles[i] = Local(Area.cellVertices, ct[i], centre, s);

        List<Pin> pins = Metros(atlas, cities, centre, s);
        // Measured against the region's largest uncoloured metro, not its
        // largest of all: the coloured ones are far bigger, and against them
        // every grey would come out white.
        var most = new Dictionary<string, int>();
        foreach (Pin pin in pins)
            if (CreateSheet.Uncoloured(pin.color))
                most[pin.region] = Mathf.Max(most.TryGetValue(pin.region, out int m) ? m : 0, pin.breaches);

        foreach (Pin pin in pins)
        {
            var dot = new Dot
            {
                centre = pin.at,
                radius = look.dotRadius * Mathf.Sqrt(Mathf.Max(pin.breaches, 1)),
                name = pin.name,
                region = pin.region,
                breaches = pin.breaches,
                color = pin.color
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
        Grey(most);

        float face = FaceZ - DotLift;
        for (int d = 0; d < _dots.Count; d++)
        {
            Dot dot = _dots[d];
            float z = face - d * DotStep;
            dot.z = z;
            _dots[d] = dot;
            if (dot.cut && _cellTriangles.Length > 0) CutDisc(mesh, dot.centre, dot.radius, 0f, z, dot.color);
            else Round(mesh, dot.centre, dot.radius, 0f, z, dot.color);
        }
    }

    // How light the lightest grey pin is and how dark the darkest, as a share
    // of the way from white to black: never white, which would vanish into
    // the land, nor black, which would read as its selection ring.
    private const float LightestGrey = 0.25f;
    private const float DarkestGrey = 0.8f;

    // A metro with no colour of its own, which the API sends black, turns a
    // grey as dark as its breaches are against its region's largest such
    // metro's, shaded the way a bar is. Breaches run over orders of
    // magnitude, so they are compared on a log scale, or all but the largest
    // few would share the lightest grey. Measured in its own region, so a pin
    // cut by a border is one grey on both maps.
    private void Grey(IReadOnlyDictionary<string, int> most)
    {
        for (int d = 0; d < _dots.Count; d++)
        {
            Dot dot = _dots[d];
            if (!CreateSheet.Uncoloured(dot.color)) continue;
            int largest = most.TryGetValue(dot.region, out int m) ? m : 0;
            float t = largest > 1
                ? Mathf.Clamp01(Mathf.Log(Mathf.Max(dot.breaches, 1)) / Mathf.Log(largest))
                : 1f;
            float shade = Mathf.Lerp(LightestGrey, DarkestGrey, CreateSheet.PerceptualFraction(t));
            dot.color = Color.Lerp(Color.white, Color.black, shade);
            _dots[d] = dot;
        }
    }

    // A disc the border cuts, a pin or its ring: the square around it
    // clipped to each of the cell's triangles it overlaps, both convex, so
    // the cut is exact; the shader then rounds what is left to the circle.
    // The square stands still and is wide enough for the most the disc's
    // real part grows, since its edge along the border cannot move out.
    private void CutDisc(Builder mesh, Vector2 centre, float radius, float grows, float z, Color color)
    {
        float r = radius + grows * Mathf.Max(_look.maxGrow, 1f);
        var radii = new Vector2(radius, grows);
        Vector2 lo = centre - new Vector2(r, r), hi = centre + new Vector2(r, r);
        var piece = new List<Vector2>(8);
        for (int t = 0; t + 2 < _cellTriangles.Length; t += 3)
        {
            Vector2 a = _cellTriangles[t], b = _cellTriangles[t + 1], c = _cellTriangles[t + 2];
            if (Mathf.Max(a.x, Mathf.Max(b.x, c.x)) < lo.x || Mathf.Min(a.x, Mathf.Min(b.x, c.x)) > hi.x ||
                Mathf.Max(a.y, Mathf.Max(b.y, c.y)) < lo.y || Mathf.Min(a.y, Mathf.Min(b.y, c.y)) > hi.y)
                continue;

            piece.Clear();
            piece.Add(a); piece.Add(b); piece.Add(c);
            ClipToBox(piece, lo, hi);
            if (piece.Count < 3) continue;

            int first = mesh.Count;
            foreach (Vector2 p in piece)
                mesh.Vertex(new Vector3(p.x, p.y, z), color, p - centre, radii, Vector2.zero);
            for (int k = 1; k + 1 < piece.Count; k++) mesh.Triangle(first, first + k, first + k + 1);
        }
    }

    // Sutherland-Hodgman against an upright box, in place.
    private static void ClipToBox(List<Vector2> poly, Vector2 lo, Vector2 hi)
    {
        ClipToEdge(poly, 0, lo.x, true);
        ClipToEdge(poly, 0, hi.x, false);
        ClipToEdge(poly, 1, lo.y, true);
        ClipToEdge(poly, 1, hi.y, false);
    }

    private static readonly List<Vector2> ClipScratch = new List<Vector2>(16);

    private static void ClipToEdge(List<Vector2> poly, int axis, float at, bool keepAbove)
    {
        if (poly.Count == 0) return;
        ClipScratch.Clear();
        for (int i = 0; i < poly.Count; i++)
        {
            Vector2 p = poly[i], q = poly[(i + 1) % poly.Count];
            bool pIn = keepAbove ? p[axis] >= at : p[axis] <= at;
            bool qIn = keepAbove ? q[axis] >= at : q[axis] <= at;
            if (pIn) ClipScratch.Add(p);
            if (pIn != qIn) ClipScratch.Add(Vector2.Lerp(p, q, (at - p[axis]) / (q[axis] - p[axis])));
        }
        poly.Clear();
        poly.AddRange(ClipScratch);
    }

    // One pin before it is drawn: where it stands on this map, in metres.
    private sealed class Pin
    {
        public string name;
        public string region;
        public Color color;
        public Vector2 at;
        public int breaches;
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
            // A city with no metro has no sheet (BreachMap.Answer.Metros skips
            // it too), so a pin for it could only say it has no figures.
            if (string.IsNullOrEmpty(city.metro))
            {
                Debug.LogWarning($"[RegionMap] {city.name} has no metro; it has no pin.");
                continue;
            }
            string metro = city.metro;
            if (!byMetro.TryGetValue(metro, out Pin pin))
            {
                pin = new Pin { name = metro, region = city.region, color = city.color };
                byMetro[metro] = pin;
                pins.Add(pin);
            }
            int n = Mathf.Max(city.breaches, 1);
            pin.at = (pin.at * pin.breaches + (km - centre) * s * n) / (pin.breaches + n);
            pin.breaches += n;
        }
        return pins;
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
        // Offset from a circle's centre before it grows, and its radius, the
        // part that scales and the part that grows, which the shader cuts
        // the circle by; a radius of zero for everything that is not round.
        private readonly List<Vector2> _circles = new List<Vector2>();
        private readonly List<Vector2> _radii = new List<Vector2>();
        // How far the vertex moves out for each real metre in a map unit:
        // a line's side, a round end's corner; zero for what only scales.
        private readonly List<Vector2> _grows = new List<Vector2>();
        private readonly List<int> _triangles = new List<int>();

        // More than the widest line, ring and depth grow to at the smallest
        // scale, in the map's units.
        private const float BoundsSlack = 0.05f;

        public int Count => _vertices.Count;

        public void Vertex(Vector3 at, Color color) =>
            Vertex(at, color, Vector2.zero, Vector2.zero, Vector2.zero);

        public void Vertex(Vector3 at, Color color, Vector2 circle, Vector2 radii, Vector2 grow)
        {
            _vertices.Add(at);
            _circles.Add(circle);
            _radii.Add(radii);
            _grows.Add(grow);
            // Handed over as the bars' colours are, so a map and the bars
            // beside it read as one palette.
            _colors.Add(color);
        }

        public void Triangle(int a, int b, int c)
        {
            _triangles.Add(a); _triangles.Add(b); _triangles.Add(c);
        }

        public Mesh Finish(string name)
        {
            var mesh = new Mesh { name = $"{name} map" };
            if (_vertices.Count > ushort.MaxValue) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(_vertices);
            mesh.SetColors(_colors);
            mesh.SetUVs(0, _circles);
            mesh.SetUVs(1, _radii);
            mesh.SetUVs(2, _grows);
            mesh.SetTriangles(_triangles, 0, true);
            // The lines and round ends stand on their middles and the shader
            // widens them, and the layers' depth with them, so the bounds
            // allow for the most they grow.
            Bounds bounds = mesh.bounds;
            bounds.Expand(BoundsSlack);
            mesh.bounds = bounds;
            // Handed to the GPU now, while it is made, rather than on the
            // frame it is first drawn.
            mesh.UploadMeshData(false);
            return mesh;
        }
    }

    private void OnDestroy()
    {
        MeshFilter filter = GetComponent<MeshFilter>();
        if (filter != null && filter.sharedMesh != null) Destroy(filter.sharedMesh);
        // The selection edges' meshes are the map's own too.
        foreach (MeshRenderer edge in _edges.Values)
        {
            if (edge == null) continue;
            MeshFilter edgeFilter = edge.GetComponent<MeshFilter>();
            if (edgeFilter != null && edgeFilter.sharedMesh != null) Destroy(edgeFilter.sharedMesh);
        }
    }
}
