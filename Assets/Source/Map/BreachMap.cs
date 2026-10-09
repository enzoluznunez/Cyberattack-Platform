using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

// Where the breached companies are headquartered, as /map answers: every
// country with a breach, most breaches first, each with one dot per city.
public static class BreachMap
{
    public sealed class City
    {
        public string name;
        public double lat, lon;
        public int breaches;
    }

    public sealed class Country
    {
        public string name;
        public string code;
        public int breaches;
        public List<City> cities = new List<City>();
    }

    public static async Task<List<Country>> Fetch()
    {
        string json = await CyberApi.Get("/map").ConfigureAwait(false);
        return Parse(json);
    }

    public static List<Country> Parse(string json)
    {
        var countries = new List<Country>();
        using JsonDocument doc = JsonDocument.Parse(json);
        foreach (JsonElement c in doc.RootElement.GetProperty("countries").EnumerateArray())
        {
            var country = new Country
            {
                name = c.GetProperty("country").GetString(),
                code = c.GetProperty("code").GetString(),
                breaches = c.GetProperty("breaches").GetInt32()
            };
            foreach (JsonElement city in c.GetProperty("cities").EnumerateArray())
                country.cities.Add(new City
                {
                    name = city.GetProperty("city").GetString(),
                    lat = city.GetProperty("lat").GetDouble(),
                    lon = city.GetProperty("lon").GetDouble(),
                    breaches = city.GetProperty("breaches").GetInt32()
                });
            countries.Add(country);
        }
        return countries;
    }
}
