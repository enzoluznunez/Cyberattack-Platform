using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Threading.Tasks;

// Lists the sheets at startup, from the regions and metros /map names: one for
// the whole United States, one per region, and one per metro, leaving out a
// metro none of whose breaches reports a figure, as it has no rows. Each
// becomes a listed dataset whose payload is the /sheet URL that draws it. A
// listed sheet is the database as it stood when the app started rather than
// an export of how it once stood. Nothing is opened here; the map opens the
// national sheet once it is drawn. The national sheet and each region's, the
// sheets a map opens on, are read behind it, so sliding to a map never waits
// on the network; every metro's is read after them, before the app is used.
public class ViewCatalog : MonoBehaviour
{
    public ManageDatasets manageDatasets;

    [Tooltip("One-line file inside StreamingAssets holding the API's base URL. " +
             "Absent or empty, the built-in default is used.")]
    public string apiUrlFile = "cloud.url";

    [Tooltip("One-line file inside StreamingAssets holding the Google Cloud API key the " +
             "gateway checks in X-Api-Key. Git ignores it, as it does gemini.key.")]
    public string apiKeyFile = "cloud.key";

    // Whether the address and key are read, so anything else asking the
    // database (the map) knows when it can.
    public static bool Configured { get; private set; }

    // Whether the sheets are listed and queued to be read, or never will be
    // (no map to list them from), so the loading phase knows what it waits on.
    public static bool Settled { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        Configured = false;
        Settled = false;
    }

    private void Start()
    {
        if (manageDatasets == null) manageDatasets = GetComponent<ManageDatasets>();
        if (manageDatasets == null) manageDatasets = Scene.Datasets;

        StartCoroutine(Bootstrap());
    }

    private IEnumerator Bootstrap()
    {
        // Both read at once; neither waits on the other.
        Task<string> url = ReadLine(apiUrlFile);
        Task<string> key = ReadLine(apiKeyFile);
        yield return new WaitUntil(() => url.IsCompleted && key.IsCompleted);

        if (!string.IsNullOrEmpty(url.Result)) CyberApi.BaseUrl = url.Result;
        Debug.Log($"[ViewCatalog] Breach database at {CyberApi.BaseUrl}.");

        if (!string.IsNullOrEmpty(key.Result)) CyberApi.ApiKey = key.Result;
        if (string.IsNullOrEmpty(CyberApi.ApiKey))
            Debug.LogWarning($"[ViewCatalog] No '{apiKeyFile}' in StreamingAssets; requests carry no " +
                             "API key, which only a server on this machine will accept.");

        // The map asks the database too, so the address and key are read even
        // with nowhere to list the sheets.
        Configured = true;

        if (manageDatasets == null)
        {
            Debug.LogError("[ViewCatalog] No ManageDatasets in the scene; no sheet will be listed.");
            Settled = true;
            yield break;
        }

        Task<BreachMap.Answer> map = BreachMap.Fetch();
        while (!map.IsCompleted) yield return null;
        if (map.Status != TaskStatus.RanToCompletion)
        {
            // The map says so in its own notice.
            Debug.LogWarning($"[ViewCatalog] No metros to list: {map.Exception?.GetBaseException().Message}");
            Settled = true;
            yield break;
        }
        ListMetros(map.Result);
        Settled = true;
    }

    // Where the database is and how to be let in are configuration, not data:
    // one line each in StreamingAssets, so a build is pointed and keyed without
    // editing a source file. A file that is absent or empty leaves the default.
    private static Task<string> ReadLine(string fileName) =>
        string.IsNullOrEmpty(fileName) ? Task.FromResult<string>(null) : CyberApi.ReadStreamingLine(fileName);

    private void ListMetros(BreachMap.Answer map)
    {
        const string Rows = "a row each: its revenue, net income, assets, book value, market cap and stock " +
                            "price, each bar as tall as its place among every US breach.";

        var entries = new List<(string, string, string)>();
        entries.Add((UsSheetUrl(), National,
            $"Every breach at a company headquartered in the United States, every region together, {Rows}"));
        foreach (BreachMap.Region region in map.regions)
            entries.Add((RegionSheetUrl(region.region), Title(region.region),
                $"Every breach at a company headquartered in the {Title(region.region)} region, metro by " +
                $"metro, {Rows}"));

        List<BreachMap.Metro> metros = map.Metros();
        foreach (BreachMap.Metro metro in metros)
        {
            if (metro.charted == 0) continue;
            entries.Add((SheetUrl(metro.name), metro.name,
                $"Every breach at a company headquartered in the {metro.name} metro ({Title(metro.region)}), {Rows}"));
        }
        manageDatasets.AddCatalogEntries(entries);
        Debug.Log($"[ViewCatalog] Listed {entries.Count} sheets; reading them all before the map opens.");

        // The US sheet, the regions', then every metro's, all while the app is
        // loading (LoadingPhase), so poking a pin never waits on the network
        // or a parse.
        foreach (var entry in entries)
            manageDatasets.Preload(manageDatasets.IndexOf(entry.Item1));
    }

    // What the national sheet and map are called.
    public const string National = "United States";

    // 'US West' -> 'West', as the map and the rail name it.
    public static string Title(string region) =>
        region != null && region.StartsWith("US ", StringComparison.Ordinal) ? region.Substring(3) : region;

    // The request that draws one metro's sheet.
    public static string SheetUrl(string metro) =>
        CyberApi.Url("/sheet?metro=" + Uri.EscapeDataString(metro));

    // The request that draws one region's sheet ('US West').
    public static string RegionSheetUrl(string region) =>
        CyberApi.Url("/sheet?region=" + Uri.EscapeDataString(region));

    // The request that draws the whole country's sheet.
    public static string UsSheetUrl() => CyberApi.Url("/sheet");
}
