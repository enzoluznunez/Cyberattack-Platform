using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using UnityEngine;
using UnityEngine.Networking;

// Lists the sheets the breach database draws, at startup, without reading any
// of them. The app ships no sheets: /views states which exist, and each becomes
// a listed dataset whose payload is the /sheet URL that draws it, fetched the
// first time it is opened. So a listed sheet is always the database as it
// stands now rather than an export of how it once stood.
public class ViewCatalog : MonoBehaviour
{
    public ManageDatasets manageDatasets;

    [Tooltip("One-line file inside StreamingAssets holding the API's base URL. " +
             "Absent or empty, the built-in default is used.")]
    public string apiUrlFile = "api.url";

    [Tooltip("One-line file inside StreamingAssets holding the Google Cloud API key the " +
             "gateway checks in X-Api-Key. Git ignores it, as it does gemini.key.")]
    public string apiKeyFile = "cloud.key";

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

        yield return ListViews();
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

    private IEnumerator ListViews()
    {
        string url = CyberApi.BaseUrl.TrimEnd('/') + "/views";

        using (UnityWebRequest www = UnityWebRequest.Get(url))
        {
            CyberApi.Authorize(www);
            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[ViewCatalog] The breach database at {url} could not be reached " +
                               $"({www.error}); the app starts with nothing listed.");
                Notices.Show(this, "No Data", CyberApi.Unreachable);
                yield break;
            }

            Register(www.downloadHandler.text);
        }
    }

    private void Register(string json)
    {
        Catalog catalog;
        try
        {
            catalog = Read(json);
        }
        catch (Exception e)
        {
            Debug.LogError($"[ViewCatalog] /views answered with something unreadable: {e.Message}");
            Notices.Show(this, "No Data", CyberApi.Unreachable);
            return;
        }

        if (catalog.views.Count == 0)
        {
            Debug.LogWarning("[ViewCatalog] The database draws no sheets; nothing is listed.");
            Notices.Show(this, "No Data", "The breach database draws no sheets.");
            return;
        }

        // The rail draws the newest entry at the top, so the reply is walked
        // backwards to arrive in the server's order, top to bottom. The whole
        // catalogue goes in as one batch: the rail rebuilds itself on every
        // change.
        var entries = new List<(string, string, string)>(catalog.views.Count);
        for (int i = catalog.views.Count - 1; i >= 0; i--)
        {
            View view = catalog.views[i];
            entries.Add((SheetUrl(view.id), view.title, view.description));
        }
        manageDatasets.AddCatalogEntries(entries);

        Debug.Log($"[ViewCatalog] Listed {catalog.views.Count} sheets; opening '{catalog.defaultView}', " +
                  "the rest are fetched when asked for.");

        OpenAtStartup(SheetUrl(catalog.defaultView));
    }

    // The app opens on the server's default sheet, so there is something to
    // look at before anyone asks for anything. Switching to an unread entry
    // fetches it and switches once it is read.
    private void OpenAtStartup(string url)
    {
        if (manageDatasets.ActiveIndex >= 0) return;

        var datasets = manageDatasets.Datasets;
        for (int i = 0; i < datasets.Count; i++)
        {
            if (datasets[i].payload != url) continue;
            manageDatasets.SwitchDataset(i);
            return;
        }
    }

    // The request that draws one sheet, unfiltered: every breach, every year.
    public static string SheetUrl(string view) =>
        CyberApi.BaseUrl.TrimEnd('/') + "/sheet?view=" + Uri.EscapeDataString(view);

    private struct View
    {
        public string id;
        public string title;
        public string description;
    }

    private struct Catalog
    {
        public string defaultView;
        public List<View> views;
    }

    private static Catalog Read(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        var views = new List<View>();
        foreach (JsonElement view in root.GetProperty("views").EnumerateArray())
        {
            string id = view.GetProperty("view").GetString();
            if (string.IsNullOrEmpty(id)) continue;

            views.Add(new View
            {
                id = id,
                title = view.GetProperty("title").GetString(),
                description = view.GetProperty("description").GetString()
            });
        }
        return new Catalog { defaultView = root.GetProperty("default").GetString(), views = views };
    }
}
