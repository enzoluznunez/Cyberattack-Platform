using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using UnityEngine;

// Where the breached companies are headquartered, as /map answers: the six
// regions west to east, each with the colour the national map draws it in,
// and every city with a breach, with its metro and the colour its pin and its
// bars share. Read once and shared: the map draws from it, and the sheets are
// listed from its metros.
public static class BreachMap
{
    public sealed class Region
    {
        public string region;
        public string title;
        public Color color;
        public int breaches;
    }

    public sealed class City
    {
        public string name;
        public string region;
        public string metro;
        public Color color;
        public double lat, lon;
        public int breaches;
    }

    public sealed class Metro
    {
        public string name;
        public string region;
        public int breaches;
    }

    public sealed class Answer
    {
        public readonly List<Region> regions = new List<Region>();
        public readonly List<City> cities = new List<City>();

        // Every metro, its region and its breaches, largest first within
        // each region, and the regions west to east.
        public List<Metro> Metros()
        {
            var byName = new Dictionary<string, Metro>();
            foreach (City city in cities)
            {
                if (string.IsNullOrEmpty(city.metro)) continue;
                if (!byName.TryGetValue(city.metro, out Metro metro))
                    byName[city.metro] = metro = new Metro { name = city.metro, region = city.region };
                metro.breaches += city.breaches;
            }
            var order = regions.ConvertAll(r => r.region);
            var metros = new List<Metro>(byName.Values);
            metros.Sort((a, b) =>
            {
                int byRegion = order.IndexOf(a.region).CompareTo(order.IndexOf(b.region));
                if (byRegion != 0) return byRegion;
                return a.breaches != b.breaches
                    ? b.breaches.CompareTo(a.breaches)
                    : string.CompareOrdinal(a.name, b.name);
            });
            return metros;
        }
    }

    private static Task<Answer> _shared;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => _shared = null;

    // The one read of /map; asked again after a failure, so a later caller
    // tries again.
    public static Task<Answer> Fetch()
    {
        if (_shared == null || _shared.IsFaulted || _shared.IsCanceled) _shared = Read();
        return _shared;
    }

    private static async Task<Answer> Read()
    {
        string json = await CyberApi.Get("/map").ConfigureAwait(false);
        return Parse(json);
    }

    public static Answer Parse(string json)
    {
        var answer = new Answer();
        using JsonDocument doc = JsonDocument.Parse(json);
        foreach (JsonElement r in doc.RootElement.GetProperty("regions").EnumerateArray())
            answer.regions.Add(new Region
            {
                region = r.GetProperty("region").GetString(),
                title = r.GetProperty("title").GetString(),
                color = Hex(r.GetProperty("color").GetString()),
                breaches = r.GetProperty("breaches").GetInt32()
            });
        foreach (JsonElement c in doc.RootElement.GetProperty("cities").EnumerateArray())
            answer.cities.Add(new City
            {
                name = c.GetProperty("city").GetString(),
                region = c.GetProperty("region").GetString(),
                metro = c.GetProperty("metro").GetString(),
                color = Hex(c.GetProperty("color").GetString()),
                lat = c.GetProperty("lat").GetDouble(),
                lon = c.GetProperty("lon").GetDouble(),
                breaches = c.GetProperty("breaches").GetInt32()
            });
        return answer;
    }

    // Parsed off the main thread: ColorUtility is Unity's and may not be.
    private static Color Hex(string hex)
    {
        if (hex == null || hex.Length != 7 || hex[0] != '#') return Color.grey;
        int value = System.Convert.ToInt32(hex.Substring(1), 16);
        return new Color(((value >> 16) & 0xFF) / 255f, ((value >> 8) & 0xFF) / 255f, (value & 0xFF) / 255f);
    }
}
