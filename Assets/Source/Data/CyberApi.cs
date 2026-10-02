using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

public static class CyberApi {

    // Where the database is: API Gateway, in front of the private Cloud Run
    // service, the same for every build. StreamingAssets/api.url overrides it
    // when present, which is how the Editor is pointed at a server running on
    // this machine instead.
    public static string BaseUrl = "https://cyber-gateway-6z6s1gve.ue.gateway.dev";

    // The Google Cloud API key the gateway checks in X-Api-Key, restricted to
    // this API alone. ViewCatalog reads it from StreamingAssets/cloud.key at
    // startup; left empty, requests go out without it, which a server on this
    // machine does not ask for and the gateway refuses.
    public static string ApiKey = "";
    public const string KeyHeader = "X-Api-Key";

    // Only requests to the API carry the key: a sheet can also be a file in
    // StreamingAssets or another site's URL, and neither should ever see it.
    public static bool IsApi(string url) =>
        !string.IsNullOrEmpty(url) &&
        url.StartsWith(BaseUrl.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);

    public static void Authorize(UnityWebRequest request) {
        if (!string.IsNullOrEmpty(ApiKey) && IsApi(request.url)) request.SetRequestHeader(KeyHeader, ApiKey);
    }

    public const string Unreachable = "The breach database could not be reached.";

    private static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

    // A reply the server chose to send: its status and the text of its 'detail'.
    public sealed class ApiError : Exception {
        public readonly int Status;
        public ApiError(int status, string detail) : base(detail) { Status = status; }
    }

    public static async Task<string> Get(string path) {
        using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl.TrimEnd('/') + path);
        if (!string.IsNullOrEmpty(ApiKey)) request.Headers.Add(KeyHeader, ApiKey);
        using HttpResponseMessage response = await http.SendAsync(request).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new ApiError((int)response.StatusCode, Detail(body));
        return body;
    }

    // What the model should be told about a failed call: the server's own
    // words when it rejected the request, one generic line for anything else.
    public static string Explain(Exception e) =>
        e is ApiError api && api.Status < 500 ? api.Message : Unreachable;

    // Runs one GET and hands the parsed body to fill, so every read-only tool
    // shares the same error path and the same 'ok' marker.
    public static async Task<Dictionary<string, object>> Fetch(string tag, string path,
        Action<JsonElement, Dictionary<string, object>> fill) {
        var result = new Dictionary<string, object>();
        try {
            string json = await Get(path).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(json);
            fill(doc.RootElement, result);
            result["ok"] = true;
        }
        catch (Exception e) {
            Debug.LogWarning($"[{tag}] {e.Message}");
            result["error"] = Explain(e);
        }
        return result;
    }

    public static List<object> Strings(JsonElement array) {
        var list = new List<object>();
        foreach (JsonElement item in array.EnumerateArray()) list.Add(item.GetString());
        return list;
    }

    // FastAPI wraps its error text as {"detail": ...} and the gateway its own
    // refusals as {"code": ..., "message": ...}; anything else is passed on,
    // cut short.
    private static string Detail(string body) {
        try {
            using JsonDocument doc = JsonDocument.Parse(body);
            foreach (string name in new[] { "detail", "message" })
                if (doc.RootElement.TryGetProperty(name, out JsonElement text) &&
                    text.ValueKind == JsonValueKind.String)
                    return text.GetString();
        }
        catch (JsonException) { }
        return string.IsNullOrEmpty(body) ? "" : body.Length <= 200 ? body : body.Substring(0, 200);
    }
}
