using System;
using System.Collections.Generic;
using System.Text.Json;
using UnityEngine;

// The United States and its six regions, as pipeline/maps.py drew them into
// Resources/Maps/us.json: flat, in the national map's kilometres. Every map
// shares one projection, so a city is at one place on all of them, and a
// region's map is the national map cut along its borders. The app ships the
// outlines rather than asking for them, since they never change with the
// data; the pins on them come from the API.
public sealed class MapAtlas
{
    public const string ResourcePath = "Maps/us";

    // One projection centre, and the box of longitudes and latitudes it covers,
    // moved into place by its scale and offset: the lower 48 and the Alaska,
    // Hawaii and Puerto Rico insets.
    public sealed class Frame
    {
        public double west, south, east, north;
        public double lon0, lat0;
        public double scale;
        public double toX, toY;
    }

    public sealed class Area
    {
        public string name;
        // The region it draws, or null for the national map.
        public string region;
        public Rect bounds;
        public float[] vertices;
        public int[] triangles;
        public float[][] outlines;
        // The national map's lines between regions; none on a region's map.
        public float[][] borders;
        // A region's share of the plane, its land and the water nearest it:
        // a pin reaching over the region's border is cut along this. Null on
        // the national map, where nothing is cut.
        public float[] cell;

        public bool IsNational => region == null;
    }

    private static double _radiusKm = 6371.0088;
    private Frame[] _frames = Array.Empty<Frame>();
    private readonly List<Area> _areas = new List<Area>();

    // The national map first, then the regions west to east.
    public IReadOnlyList<Area> Areas => _areas;

    public Area Find(string name)
    {
        foreach (Area area in _areas)
            if (string.Equals(area.name, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(area.region, name, StringComparison.OrdinalIgnoreCase))
                return area;
        return null;
    }

    // Where a longitude and latitude falls on every map, in kilometres, or
    // false for a place no frame holds.
    public bool TryPlace(double lon, double lat, out Vector2 km)
    {
        km = default;
        Frame frame = FrameOf(ref lon, lat);
        if (frame == null) return false;

        Vector2 flat = Project(lon, lat, frame.lon0, frame.lat0);
        km = new Vector2((float)(frame.toX + frame.scale * flat.x), (float)(frame.toY + frame.scale * flat.y));
        return true;
    }

    // The first frame whose box holds the point. Alaska's box reaches past
    // -180, for the Aleutians, and takes eastern longitudes as their western
    // equivalents.
    private Frame FrameOf(ref double lon, double lat)
    {
        foreach (Frame f in _frames)
        {
            double l = f.west < -180 && lon > 0 ? lon - 360 : lon;
            if (l >= f.west && l <= f.east && lat >= f.south && lat <= f.north)
            {
                lon = l;
                return f;
            }
        }
        return null;
    }

    // Lambert azimuthal equal-area on the sphere, in kilometres east and north
    // of (lon0, lat0): pipeline/maps.py's project(), which drew the outlines.
    public static Vector2 Project(double lon, double lat, double lon0, double lat0)
    {
        const double rad = Math.PI / 180.0;
        double p = lat * rad, l = (lon - lon0) * rad, p0 = lat0 * rad;
        double k = Math.Sqrt(2.0 / (1.0 + Math.Sin(p0) * Math.Sin(p) + Math.Cos(p0) * Math.Cos(p) * Math.Cos(l)));
        return new Vector2(
            (float)(_radiusKm * k * Math.Cos(p) * Math.Sin(l)),
            (float)(_radiusKm * k * (Math.Cos(p0) * Math.Sin(p) - Math.Sin(p0) * Math.Cos(p) * Math.Cos(l))));
    }

    // Parsing is done off the main thread; the bytes are read on it, as a
    // TextAsset's have to be. They are handed over as UTF-8, which the parser
    // reads directly, rather than as a string.
    public static byte[] ReadBytes()
    {
        TextAsset asset = Resources.Load<TextAsset>(ResourcePath);
        if (asset == null)
        {
            Debug.LogError($"[MapAtlas] No outlines at Resources/{ResourcePath}; run pipeline/maps.py.");
            return null;
        }
        byte[] bytes = asset.bytes;
        Resources.UnloadAsset(asset);
        return bytes;
    }

    public static MapAtlas Parse(byte[] json)
    {
        var atlas = new MapAtlas();
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        _radiusKm = root.GetProperty("radiusKm").GetDouble();
        atlas._frames = Frames(root.GetProperty("frames"));

        foreach (JsonElement m in root.GetProperty("maps").EnumerateArray())
        {
            JsonElement b = m.GetProperty("bounds");
            JsonElement region = m.GetProperty("region");
            var area = new Area
            {
                name = m.GetProperty("name").GetString(),
                region = region.ValueKind == JsonValueKind.String ? region.GetString() : null,
                bounds = Rect.MinMaxRect(b[0].GetSingle(), b[1].GetSingle(), b[2].GetSingle(), b[3].GetSingle()),
                vertices = Floats(m.GetProperty("vertices")),
                triangles = Ints(m.GetProperty("triangles")),
                outlines = Rings(m.GetProperty("outlines")),
                borders = m.TryGetProperty("borders", out JsonElement borders) ? Rings(borders) : Array.Empty<float[]>()
            };
            if (m.TryGetProperty("cell", out JsonElement cell) && cell.GetArrayLength() > 0)
                area.cell = Floats(cell[0]);
            atlas._areas.Add(area);
        }

        atlas.Check(root.GetProperty("checks"));
        return atlas;
    }

    // The outlines list a few places projected when they were drawn. Landing
    // anywhere else here would put every pin off its city.
    private void Check(JsonElement checks)
    {
        foreach (JsonElement c in checks.EnumerateArray())
        {
            double lon = c[0].GetDouble(), lat = c[1].GetDouble();
            var expected = new Vector2(c[2].GetSingle(), c[3].GetSingle());
            if (!TryPlace(lon, lat, out Vector2 km) || (km - expected).magnitude > 0.05f)
                Debug.LogWarning($"[MapAtlas] ({lon}, {lat}) lands at {km}, where the outlines put it at " +
                                 $"{expected}; pins will be off.");
        }
    }

    private static Frame[] Frames(JsonElement array)
    {
        var frames = new List<Frame>();
        foreach (JsonElement f in array.EnumerateArray())
        {
            JsonElement center = f.GetProperty("center"), to = f.GetProperty("to"), box = f.GetProperty("box");
            frames.Add(new Frame
            {
                west = box[0].GetDouble(), south = box[1].GetDouble(),
                east = box[2].GetDouble(), north = box[3].GetDouble(),
                lon0 = center[0].GetDouble(), lat0 = center[1].GetDouble(),
                scale = f.GetProperty("scale").GetDouble(),
                toX = to[0].GetDouble(), toY = to[1].GetDouble()
            });
        }
        return frames.ToArray();
    }

    private static float[] Floats(JsonElement array)
    {
        var values = new float[array.GetArrayLength()];
        int i = 0;
        foreach (JsonElement v in array.EnumerateArray()) values[i++] = v.GetSingle();
        return values;
    }

    private static int[] Ints(JsonElement array)
    {
        var values = new int[array.GetArrayLength()];
        int i = 0;
        foreach (JsonElement v in array.EnumerateArray()) values[i++] = v.GetInt32();
        return values;
    }

    private static float[][] Rings(JsonElement array)
    {
        var rings = new List<float[]>();
        foreach (JsonElement r in array.EnumerateArray()) rings.Add(Floats(r));
        return rings.ToArray();
    }
}
