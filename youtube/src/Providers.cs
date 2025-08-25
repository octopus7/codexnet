using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace YoutubeCli.Providers;

public record VideoInfo(string Id, string Title, long? ViewCount, long? LikeCount, long? CommentCount);

public interface IVideoProvider
{
    Task<List<VideoInfo>> GetRecentAsync(string handle, int maxResults);
}

public sealed class ApiVideoProvider(HttpClient http, string apiKey) : IVideoProvider
{
    private readonly HttpClient _http = http;
    private readonly string _apiKey = apiKey;

    public async Task<List<VideoInfo>> GetRecentAsync(string handle, int maxResults)
    {
        var channelId = await GetChannelIdByHandleAsync(handle);
        if (channelId is null) return new List<VideoInfo>();

        var ids = await GetRecentVideoIdsAsync(channelId, Math.Clamp(maxResults, 1, 50));
        if (ids.Count == 0) return new List<VideoInfo>();

        var details = await GetVideoDetailsAsync(ids);
        return details.Select(v => new VideoInfo(
            v.Id ?? string.Empty,
            v.Snippet?.Title ?? "(제목 없음)",
            v.Statistics?.ViewCount,
            v.Statistics?.LikeCount,
            v.Statistics?.CommentCount
        )).ToList();
    }

    private async Task<string?> GetChannelIdByHandleAsync(string handle)
    {
        var url = $"https://www.googleapis.com/youtube/v3/channels?part=id&forHandle={Uri.EscapeDataString(handle)}&key={Uri.EscapeDataString(_apiKey)}";
        using var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        using var stream = await resp.Content.ReadAsStreamAsync();
        var data = await JsonSerializer.DeserializeAsync<ChannelsListResponse>(stream);
        return data?.Items?.FirstOrDefault()?.Id;
    }

    private async Task<List<string>> GetRecentVideoIdsAsync(string channelId, int maxResults)
    {
        var url = $"https://www.googleapis.com/youtube/v3/search?part=id&order=date&channelId={Uri.EscapeDataString(channelId)}&type=video&maxResults={maxResults}&key={Uri.EscapeDataString(_apiKey)}";
        using var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        using var stream = await resp.Content.ReadAsStreamAsync();
        var data = await JsonSerializer.DeserializeAsync<SearchListResponse>(stream);
        return data?.Items?
            .Select(i => i.Id?.VideoId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToList() ?? new List<string>();
    }

    private async Task<List<Video>> GetVideoDetailsAsync(List<string> ids)
    {
        var idParam = string.Join(',', ids);
        var url = $"https://www.googleapis.com/youtube/v3/videos?part=snippet,statistics&id={Uri.EscapeDataString(idParam)}&key={Uri.EscapeDataString(_apiKey)}";
        using var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        using var stream = await resp.Content.ReadAsStreamAsync();
        var data = await JsonSerializer.DeserializeAsync<VideosListResponse>(stream);
        return data?.Items ?? new List<Video>();
    }

    // DTOs for API responses
    private sealed class ChannelsListResponse
    {
        [JsonPropertyName("items")] public List<ChannelItem>? Items { get; set; }
    }
    private sealed class ChannelItem
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
    }
    private sealed class SearchListResponse
    {
        [JsonPropertyName("items")] public List<SearchItem>? Items { get; set; }
    }
    private sealed class SearchItem
    {
        [JsonPropertyName("id")] public SearchItemId? Id { get; set; }
    }
    private sealed class SearchItemId
    {
        [JsonPropertyName("videoId")] public string? VideoId { get; set; }
    }
    private sealed class VideosListResponse
    {
        [JsonPropertyName("items")] public List<Video> Items { get; set; } = new();
    }
    private sealed class Video
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("snippet")] public VideoSnippet? Snippet { get; set; }
        [JsonPropertyName("statistics")] public VideoStatistics? Statistics { get; set; }
    }
    private sealed class VideoSnippet
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
    }
    private sealed class VideoStatistics
    {
        [JsonPropertyName("viewCount")] public long? ViewCount { get; set; }
        [JsonPropertyName("likeCount")] public long? LikeCount { get; set; }
        [JsonPropertyName("commentCount")] public long? CommentCount { get; set; }
    }
}

public sealed class WebVideoProvider(HttpClient http) : IVideoProvider
{
    private readonly HttpClient _http = http;

    public async Task<List<VideoInfo>> GetRecentAsync(string handle, int maxResults)
    {
        // Use the public channel videos page and parse ytInitialData JSON.
        var url = $"https://www.youtube.com/{Uri.EscapeDataString(handle)}/videos?hl=en";
        using var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();

        var json = ExtractJsonBlock(html, "ytInitialData");
        if (json is null) return new List<VideoInfo>();

        using var doc = JsonDocument.Parse(json);
        var videos = new List<VideoInfo>();

        foreach (var renderer in FindVideoRenderers(doc.RootElement))
        {
            var id = TryGetString(renderer, ["videoId"]) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id)) continue;

            var title = TryGetText(renderer, ["title"]) ?? "(제목 없음)";
            var viewsText = TryGetText(renderer, ["viewCountText"]) ?? string.Empty;
            long? views = ParseFirstNumber(viewsText);

            videos.Add(new VideoInfo(id, title, views, null, null));
            if (videos.Count >= maxResults) break;
        }

        // Note: Like/Comment counts are not reliably present on the channel videos page HTML
        // without client-side calls. Leaving them null here.
        return videos;
    }

    private static IEnumerable<JsonElement> FindVideoRenderers(JsonElement root)
    {
        var stack = new Stack<JsonElement>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur.ValueKind == JsonValueKind.Object)
            {
                if (cur.TryGetProperty("gridVideoRenderer", out var gvr)) yield return gvr;
                if (cur.TryGetProperty("videoRenderer", out var vr)) yield return vr;
                foreach (var prop in cur.EnumerateObject())
                    stack.Push(prop.Value);
            }
            else if (cur.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in cur.EnumerateArray()) stack.Push(item);
            }
        }
    }

    private static string? ExtractJsonBlock(string html, string marker)
    {
        var idx = html.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;

        // Find first '{' after marker
        var start = html.IndexOf('{', idx);
        if (start < 0) return null;

        int depth = 0;
        for (int i = start; i < html.Length; i++)
        {
            char c = html[i];
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return html[start..(i + 1)];
                }
            }
        }
        return null;
    }

    private static string? TryGetText(JsonElement obj, string[] propertyPath)
    {
        var e = TryGet(obj, propertyPath);
        if (e is null) return null;
        // Prefer simpleText
        if (e.Value.ValueKind == JsonValueKind.Object)
        {
            if (e.Value.TryGetProperty("simpleText", out var st) && st.ValueKind == JsonValueKind.String)
                return st.GetString();
            if (e.Value.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
            {
                var parts = runs.EnumerateArray()
                    .Select(r => r.ValueKind == JsonValueKind.Object && r.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null)
                    .Where(s => s is not null);
                return string.Concat(parts);
            }
        }
        else if (e.Value.ValueKind == JsonValueKind.String)
        {
            return e.Value.GetString();
        }
        return null;
    }

    private static string? TryGetString(JsonElement obj, string[] propertyPath)
    {
        var e = TryGet(obj, propertyPath);
        if (e is null) return null;
        if (e.Value.ValueKind == JsonValueKind.String) return e.Value.GetString();
        return null;
    }

    private static JsonElement? TryGet(JsonElement obj, string[] propertyPath)
    {
        var cur = obj;
        foreach (var p in propertyPath)
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(p, out var next)) return null;
            cur = next;
        }
        return cur;
    }

    private static long? ParseFirstNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // Extract digits, handling commas and spaces
        var m = Regex.Match(text, @"([0-9][0-9,\. ]*)");
        if (!m.Success) return null;
        var digits = new string(m.Groups[1].Value.Where(ch => char.IsDigit(ch)).ToArray());
        return long.TryParse(digits, out var v) ? v : null;
    }
}
