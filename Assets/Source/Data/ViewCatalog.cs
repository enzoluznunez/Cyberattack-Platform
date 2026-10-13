using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

// Lists one sheet per metro at startup, from the metros /map names: each
// becomes a listed dataset whose payload is the /sheet URL that draws it. A
// listed sheet is the database as it stood when the app started rather than
// an export of how it once stood. Nothing is opened, since the app starts on
// the national map, which shows no sheet; each region's largest metro, the
// sheet a region's map opens on, is read behind it, so sliding to a region
// never waits on the network. Another metro is read when its pin is poked.
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

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => Configured = false;

    private void Start()
    {
        if (manageDatasets == null) manageDatasets = GetComponent<ManageDatasets>();
        if (manageDatasets == null) manageDatasets = FindAnyObjectByType<ManageDatasets>();

        if (manageDatasets == null)
        {
            Debug.LogError("[ViewCatalog] No ManageDatasets in the scene; no sheet will be listed.");
            return;
        }

        StartCoroutine(Bootstrap());
    }

    private IEnumerator Bootstrap()
    {
        yield return ReadLine(apiUrlFile, configured => CyberApi.BaseUrl = configured);
        Debug.Log($"[ViewCatalog] Breach database at {CyberApi.BaseUrl}.");

        yield return ReadLine(apiKeyFile, key => CyberApi.ApiKey = key);
        if (string.IsNullOrEmpty(CyberApi.ApiKey))
            Debug.LogWarning($"[ViewCatalog] No '{apiKeyFile}' in StreamingAssets; requests carry no " +
                             "API key, which only a server on this machine will accept.");

        Configured = true;

        System.Threading.Tasks.Task<BreachMap.Answer> map = BreachMap.Fetch();
        while (!map.IsCompleted) yield return null;
        if (map.Status != System.Threading.Tasks.TaskStatus.RanToCompletion)
        {
            // The map says so in its own notice.
            Debug.LogWarning($"[ViewCatalog] No metros to list: {map.Exception?.GetBaseException().Message}");
            yield break;
        }
        ListMetros(map.Result);
    }

    // Where the database is and how to be let in are configuration, not data:
    // one line each in StreamingAssets, so a build is pointed and keyed without
    // editing a source file. A file that is absent or empty leaves the default.
    private IEnumerator ReadLine(string fileName, Action<string> apply)
    {
        if (string.IsNullOrEmpty(fileName)) yield break;

        string path = Path.Combine(Application.streamingAssetsPath, fileName);
        string url = path.Contains("://") ? path : "file://" + path;

        using (UnityWebRequest www = UnityWebRequest.Get(url))
        {
            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success) yield break;

            string line = www.downloadHandler.text.Trim();
            if (line.Length > 0) apply(line);
        }
    }

    private void ListMetros(BreachMap.Answer map)
    {
        List<BreachMap.Metro> metros = map.Metros();
        var entries = new List<(string, string, string)>(metros.Count);
        foreach (BreachMap.Metro metro in metros)
        {
            entries.Add((SheetUrl(metro.name), metro.name,
                $"Every breach at a company headquartered in the {metro.name} metro ({Title(metro.region)}), " +
                "a row each: its revenue, net income, assets, book value, market cap and stock price, each " +
                "bar as tall as its place among every US breach."));
        }
        manageDatasets.AddCatalogEntries(entries);
        Debug.Log($"[ViewCatalog] Listed {entries.Count} metros; reading each region's largest behind it.");

        var read = new HashSet<string>();
        foreach (BreachMap.Metro metro in metros)
            if (read.Add(metro.region)) manageDatasets.Preload(manageDatasets.IndexOf(SheetUrl(metro.name)));
    }

    // The largest metro of a region, whose sheet its map opens on, or null.
    public static string LargestMetro(BreachMap.Answer map, string region)
    {
        foreach (BreachMap.Metro metro in map.Metros())
            if (metro.region == region) return metro.name;
        return null;
    }

    // 'US West' -> 'West', as the map and the rail name it.
    public static string Title(string region) =>
        region != null && region.StartsWith("US ", StringComparison.Ordinal) ? region.Substring(3) : region;

    // The request that draws one metro's sheet.
    public static string SheetUrl(string metro) =>
        CyberApi.BaseUrl.TrimEnd('/') + "/sheet?metro=" + Uri.EscapeDataString(metro);
}
