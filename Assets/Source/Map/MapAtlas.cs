using System;
using System.Collections.Generic;
using System.Text.Json;
using UnityEngine;

// Every country's outline, as pipeline/maps.py drew it into
// Resources/Maps/countries.json: flat, in kilometres, one shared scale. The
// app ships the outlines rather than asking for them, since they never change
// with the data; the dots on them come from the API.
public sealed class MapAtlas
{
    public const string ResourcePath = "Maps/countries";

    // One projection centre, and the box of longitudes and latitudes it covers
    // when it is an inset, moved into place by its scale and offset.
    public sealed class Frame
    {
        public bool hasBox;
        public double west, south, east, north;
        public double lon0, lat0;
        public double scale;
        public double toX, toY;
    }

    public sealed class Country
    {
        public string name;
        public string code;
        public float areaKm2;
        public Rect bounds;
        public Frame[] frames;
        public float[] vertices;
        public int[] triangles;
        public float[][] outlines;

        // Where a longitude and latitude falls on this map, in kilometres, or
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

        // The first frame whose box holds the point, or one with no box, which
        // holds everything. A box reaching past -180 (Alaska's, for the
        // Aleutians) takes eastern longitudes as their western equivalents.
        private Frame FrameOf(ref double lon, double lat)
        {
            foreach (Frame f in frames)
            {
                if (!f.hasBox) return f;
                double l = f.west < -180 && lon > 0 ? lon - 360 : lon;
                if (l >= f.west && l <= f.east && lat >= f.south && lat <= f.north)
                {
                    lon = l;
                    return f;
                }
            }
            return null;
        }
    }

    private static double _radiusKm = 6371.0088;

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

    private readonly Dictionary<string, Country> _byName = new Dictionary<string, Country>();

    public IEnumerable<Country> Countries => _byName.Values;

    public Country Find(string name) =>
        name != null && _byName.TryGetValue(name, out Country c) ? c : null;

    // The widest map's width in kilometres: what the shared scale is set from.
    public float WidestKm { get; private set; }
    public float TallestKm { get; private set; }

    // Parsing is a few megabytes of JSON, so it is done off the main thread;
    // the text is read on it, as a TextAsset has to be.
    public static string ReadText()
    {
        TextAsset asset = Resources.Load<TextAsset>(ResourcePath);
        if (asset == null)
        {
            Debug.LogError($"[MapAtlas] No outlines at Resources/{ResourcePath}; run pipeline/maps.py.");
            return null;
        }
        string text = asset.text;
        Resources.UnloadAsset(asset);
        return text;
    }

    public static MapAtlas Parse(string json)
    {
        var atlas = new MapAtlas();
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        _radiusKm = root.GetProperty("radiusKm").GetDouble();

        foreach (JsonElement m in root.GetProperty("maps").EnumerateArray())
        {
            JsonElement b = m.GetProperty("bounds");
            var country = new Country
            {
                name = m.GetProperty("country").GetString(),
                code = m.GetProperty("code").GetString(),
                areaKm2 = m.GetProperty("areaKm2").GetSingle(),
                bounds = Rect.MinMaxRect(b[0].GetSingle(), b[1].GetSingle(), b[2].GetSingle(), b[3].GetSingle()),
                frames = Frames(m.GetProperty("frames")),
                vertices = Floats(m.GetProperty("vertices")),
                triangles = Ints(m.GetProperty("triangles")),
                outlines = Rings(m.GetProperty("outlines"))
            };
            atlas._byName[country.name] = country;
            atlas.WidestKm = Mathf.Max(atlas.WidestKm, country.bounds.width);
            atlas.TallestKm = Mathf.Max(atlas.TallestKm, country.bounds.height);
            Check(country, m.GetProperty("checks"));
        }
        return atlas;
    }

    // The outlines list a few places projected when they were drawn. Landing
    // anywhere else here would put every dot off its city.
    private static void Check(Country country, JsonElement checks)
    {
        foreach (JsonElement c in checks.EnumerateArray())
        {
            double lon = c[0].GetDouble(), lat = c[1].GetDouble();
            var expected = new Vector2(c[2].GetSingle(), c[3].GetSingle());
            if (!country.TryPlace(lon, lat, out Vector2 km) || (km - expected).magnitude > 0.05f)
                Debug.LogWarning($"[MapAtlas] {country.name}: ({lon}, {lat}) lands at {km}, " +
                                 $"where the outlines put it at {expected}; dots will be off.");
        }
    }

    private static Frame[] Frames(JsonElement array)
    {
        var frames = new List<Frame>();
        foreach (JsonElement f in array.EnumerateArray())
        {
            JsonElement center = f.GetProperty("center"), to = f.GetProperty("to");
            var frame = new Frame
            {
                lon0 = center[0].GetDouble(), lat0 = center[1].GetDouble(),
                scale = f.GetProperty("scale").GetDouble(),
                toX = to[0].GetDouble(), toY = to[1].GetDouble()
            };
            if (f.TryGetProperty("box", out JsonElement box))
            {
                frame.hasBox = true;
                frame.west = box[0].GetDouble();
                frame.south = box[1].GetDouble();
                frame.east = box[2].GetDouble();
                frame.north = box[3].GetDouble();
            }
            frames.Add(frame);
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
