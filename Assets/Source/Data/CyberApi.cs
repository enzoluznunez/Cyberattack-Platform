using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

public static class CyberApi {

    // Where the database is: API Gateway, in front of the private Cloud Run
    // service, the same for every build. StreamingAssets/cloud.url overrides it
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
        url.StartsWith(Url("/"), StringComparison.OrdinalIgnoreCase);

    // An endpoint of the API, 'path' starting with a slash.
    public static string Url(string path) => BaseUrl.TrimEnd('/') + path;

    // A file shipped in StreamingAssets, as a URL a web request can read: on
    // Android it already is one, elsewhere it is a path on disk.
    public static string StreamingAssetUrl(string fileName) {
        string path = System.IO.Path.Combine(Application.streamingAssetsPath, fileName);
        return path.Contains("://") ? path : "file://" + path;
    }

    // The one line a StreamingAssets file holds, trimmed: configuration such
    // as a key or an address. Null when the file cannot be read.
    public static Task<string> ReadStreamingLine(string fileName) {
        var done = new TaskCompletionSource<string>();
        var req = UnityWebRequest.Get(StreamingAssetUrl(fileName));
        req.SendWebRequest().completed += _ => {
            done.SetResult(req.result == UnityWebRequest.Result.Success ? req.downloadHandler.text.Trim() : null);
            req.Dispose();
        };
        return done.Task;
    }

    public static void Authorize(UnityWebRequest request) {
        if (!string.IsNullOrEmpty(ApiKey) && IsApi(request.url)) request.SetRequestHeader(KeyHeader, ApiKey);
    }

    public const string Unreachable = "The breach database could not be reached.";
    public const string Unreadable = "The breach database answered, but its reply was not in the expected shape.";

    private static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

    // A reply the server chose to send: its status and the text of its 'detail'.
    public sealed class ApiError : Exception {
        public readonly int Status;
        public ApiError(int status, string detail) : base(detail) { Status = status; }
    }

    public static async Task<string> Get(string path) {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url(path));
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

    // Runs one GET and hands the parsed body to read, so every caller of the
    // API shares the same error path: what read returns, or why there is none.
    public static async Task<(T value, string error)> Read<T>(string tag, string path, Func<JsonElement, T> read) {
        string json;
        try {
            json = await Get(path).ConfigureAwait(false);
        }
        catch (Exception e) {
            Debug.LogWarning($"[{tag}] {e.Message}");
            return (default, Explain(e));
        }

        // The server did answer: a reply that will not parse is a reply the
        // tool cannot read, not a database that cannot be reached.
        try {
            using JsonDocument doc = JsonDocument.Parse(json);
            return (read(doc.RootElement), null);
        }
        catch (Exception e) {
            Debug.LogWarning($"[{tag}] unreadable reply: {e}");
            return (default, Unreadable);
        }
    }

    // Read for a read-only tool: the parsed body fills its result, which gets
    // the same 'ok' marker every time.
    public static async Task<Dictionary<string, object>> Fetch(string tag, string path,
        Action<JsonElement, Dictionary<string, object>> fill) {
        var result = new Dictionary<string, object>();
        var (_, error) = await Read(tag, path, root => { fill(root, result); return true; }).ConfigureAwait(false);
        if (error != null) {
            result.Clear();
            result["error"] = error;
        }
        else result["ok"] = true;
        return result;
    }

    // What a tool returns when it stops before or instead of a request.
    public static Dictionary<string, object> Fail(string why) =>
        new Dictionary<string, object> { { "error", why } };

    public static List<object> Strings(JsonElement array) {
        var list = new List<object>();
        foreach (JsonElement item in array.EnumerateArray()) list.Add(item.GetString());
        return list;
    }

    // A property's text, or null when it is missing or not a string.
    public static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // A property's number, or null when it is missing or not a number.
    public static object Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? (object)value.GetDouble()
            : null;

    // FastAPI wraps its error text as {"detail": ...} and the gateway its own
    // refusals as {"code": ..., "message": ...}; anything else is passed on,
    // cut short.
    private static string Detail(string body) {
        try {
            using JsonDocument doc = JsonDocument.Parse(body);
            foreach (string name in new[] { "detail", "message" })
                if (Text(doc.RootElement, name) is string text) return text;
        }
        catch (JsonException) { }
        return string.IsNullOrEmpty(body) ? "" : body.Length <= 200 ? body : body.Substring(0, 200);
    }
}
