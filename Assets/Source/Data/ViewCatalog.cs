using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

// Lists the national sheet and one sheet per US region at startup: each
// becomes a listed dataset whose payload is the /sheet URL that draws it. The
// national sheet is opened and the rest are read behind it, so sliding to a
// region finds its sheet already read. So a listed sheet is the database as it
// stood when the app started rather than an export of how it once stood. The
// regions are the generated contract's, west to east, the order the map slides
// through them.
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
        ListRegions();
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

    private void ListRegions()
    {
        var entries = new List<(string, string, string)>(CyberContract.Regions.Length + 1);
        entries.Add((SheetUrl(null), NationalTitle,
            "Every breach at a company headquartered in the United States, a row each, region by region " +
            "west to east: its revenue, net income, assets, book value, market cap and stock price, each " +
            "bar as tall as its place among every US breach, and coloured by its region."));
        foreach (string region in CyberContract.Regions)
        {
            string title = Title(region);
            entries.Add((SheetUrl(region), title,
                $"Every breach at a company headquartered in the {title}, a row each: its revenue, net " +
                "income, assets, book value, market cap and stock price, each bar as tall as its place " +
                "among every US breach."));
        }
        manageDatasets.AddCatalogEntries(entries);
        Debug.Log($"[ViewCatalog] Listed {entries.Count} sheets; opening the national one and reading " +
                  "the rest behind it.");

        // The app opens on the national map with the national sheet.
        if (manageDatasets.ActiveIndex < 0 && entries.Count > 0)
            manageDatasets.SwitchDataset(manageDatasets.IndexOf(entries[0].Item1));

        // The others are read now rather than on the slide that opens them, so
        // a slide never waits on the network.
        for (int i = 1; i < entries.Count; i++)
            manageDatasets.Preload(manageDatasets.IndexOf(entries[i].Item1));
    }

    // 'US West' -> 'West', as the map and the rail name it.
    public static string Title(string region) =>
        region != null && region.StartsWith("US ", StringComparison.Ordinal) ? region.Substring(3) : region;

    public const string NationalTitle = "United States";

    // The request that draws one region's sheet, or with no region the
    // national one.
    public static string SheetUrl(string region) =>
        CyberApi.BaseUrl.TrimEnd('/') + "/sheet" +
        (string.IsNullOrEmpty(region) ? "" : "?region=" + Uri.EscapeDataString(region));
}
