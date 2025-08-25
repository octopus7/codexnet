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
        Console.WriteLine($"[API] Resolving channelId for {handle} ...");
        var channelId = await GetChannelIdByHandleAsync(handle);
        if (channelId is null)
        {
            Console.WriteLine("[API] channelId not found.");
            return new List<VideoInfo>();
        }
        Console.WriteLine($"[API] channelId: {channelId}");

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
        var results = new List<VideoInfo>();

        // Videos tab: also logs channelId once
        await AppendFromTabAsync(handle, "videos", Math.Max(1, maxResults - results.Count), results, logChannelId: true);

        // Streams tab
        if (results.Count < maxResults)
            await AppendFromTabAsync(handle, "streams", maxResults - results.Count, results);

        // Shorts tab
        if (results.Count < maxResults)
            await AppendFromTabAsync(handle, "shorts", maxResults - results.Count, results);

        return results;
    }

    private async Task AppendFromTabAsync(string handle, string tab, int remaining, List<VideoInfo> sink, bool logChannelId = false)
    {
        if (remaining <= 0) return;
        Console.WriteLine($"[WEB] Fetching /{tab} ...");
        var url = $"https://www.youtube.com/{Uri.EscapeDataString(handle)}/{tab}?hl=ko";
        using var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();

        var json = ExtractJsonBlock(html, "ytInitialData");
        if (json is null) return;

        using var doc = JsonDocument.Parse(json);
        if (logChannelId)
        {
            Console.WriteLine($"[WEB] Resolving channelId for {handle} ...");
            var channelId = FindChannelId(doc.RootElement);
            Console.WriteLine(channelId is null ? "[WEB] channelId not found (parsing)." : $"[WEB] channelId: {channelId}");
        }

        int added = 0;
        int shortsDetailUsed = 0;
        foreach (var renderer in FindVideoRenderers(doc.RootElement))
        {
            var id = TryGetString(renderer, ["videoId"]) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (sink.Any(v => v.Id == id)) continue; // de-dup across tabs

            // title: use title -> headline (shorts) fallback
            var title = TryGetText(renderer, ["title"]) ??
                        TryGetText(renderer, ["headline"]) ??
                        "(제목 없음)";

            // views: prefer explicit field, fallback to accessibility label
            var viewsText = TryGetText(renderer, ["viewCountText"]) ??
                            TryGetText(renderer, ["accessibility", "accessibilityData", "label"]) ??
                            string.Empty;
            long? views = ParseFirstNumber(viewsText);

            // Published text raw
            var publishedText = TryGetText(renderer, ["publishedTimeText"]) ??
                                TryGetText(renderer, ["accessibility", "accessibilityData", "label"]);

            // Log candidate prior to filtering, with tab context
            Console.WriteLine($"[WEB][LIST {tab}] id={id} | title={title} | viewsText={viewsText} | publishedText={(publishedText ?? "(없음)")}");

            bool isShortsTab = string.Equals(tab, "shorts", StringComparison.OrdinalIgnoreCase);

            string? effectivePublished = publishedText;
            long? effectiveViews = views;

            if (isShortsTab)
            {
                // Shorts: always fetch detail for first 2 to get time; others are skipped
                if (shortsDetailUsed < 2)
                {
                    var det = await GetWatchDetailsAsync(id);
                    effectivePublished = det.publishedText ?? publishedText;
                    effectiveViews = det.viewCount ?? views;
                    shortsDetailUsed++;
                    if (!IsWithin48Hours(effectivePublished)) continue;
                }
                else
                {
                    // Skip remaining shorts because list-time is unreliable and detail budget is limited
                    continue;
                }
            }
            else
            {
                // Videos/Streams: only fetch detail if list-time indicates within 48 hours
                if (!IsWithin48Hours(publishedText)) continue;
                var det = await GetWatchDetailsAsync(id);
                effectivePublished = det.publishedText ?? publishedText;
                effectiveViews = det.viewCount ?? views;
                if (!IsWithin48Hours(effectivePublished)) continue;
            }

            Console.WriteLine($"[WEB][ADD {tab}] id={id} | published={(effectivePublished ?? "(없음)")} | views={(effectiveViews?.ToString() ?? "-")}");
            sink.Add(new VideoInfo(id, title, effectiveViews, null, null));
            added++;
            if (added >= remaining) break;
        }
    }

    private async Task<(string? publishedText, long? viewCount)> GetWatchDetailsAsync(string videoId)
    {
        try
        {
            var url = $"https://www.youtube.com/watch?v={Uri.EscapeDataString(videoId)}&hl=ko";
            using var resp = await _http.GetAsync(url);
            resp.EnsureSuccessStatusCode();
            var html = await resp.Content.ReadAsStringAsync();

            // Extract player response for viewCount
            long? viewCount = null;
            var pr = ExtractJsonBlock(html, "ytInitialPlayerResponse");
            if (pr is not null)
            {
                using var prDoc = JsonDocument.Parse(pr);
                var vd = TryGet(prDoc.RootElement, ["videoDetails", "viewCount"]);
                if (vd is not null && vd.Value.ValueKind == JsonValueKind.String)
                {
                    if (long.TryParse(vd.Value.GetString(), out var vc)) viewCount = vc;
                }
            }

            // Extract initial data for published text/date
            string? published = null;
            var idata = ExtractJsonBlock(html, "ytInitialData");
            if (idata is not null)
            {
                using var idDoc = JsonDocument.Parse(idata);
                // Try publishedTimeText or dateText
                published = FindFirstTextByKeys(idDoc.RootElement, new[] { "publishedTimeText", "dateText" });
                if (string.IsNullOrWhiteSpace(published))
                {
                    // Try microformat publishDate (ISO yyyy-MM-dd)
                    var pf = TryGet(idDoc.RootElement, ["microformat", "microformatDataRenderer", "publishDate"]);
                    if (pf is not null && pf.Value.ValueKind == JsonValueKind.String)
                    {
                        var d = pf.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(d)) published = d;
                    }
                }
            }

            return (published, viewCount);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WEB][DETAIL] {videoId} detail fetch error: {ex.Message}");
            return (null, null);
        }
    }

    private static string? FindFirstTextByKeys(JsonElement root, string[] keys)
    {
        var stack = new Stack<JsonElement>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in cur.EnumerateObject())
                {
                    if (keys.Any(k => prop.NameEquals(k)))
                    {
                        var t = TryGetText(prop.Value, Array.Empty<string>());
                        if (!string.IsNullOrWhiteSpace(t)) return t;
                    }
                    stack.Push(prop.Value);
                }
            }
            else if (cur.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in cur.EnumerateArray()) stack.Push(item);
            }
        }
        return null;
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
                if (cur.TryGetProperty("reelItemRenderer", out var rr)) yield return rr; // Shorts
                foreach (var prop in cur.EnumerateObject())
                    stack.Push(prop.Value);
            }
            else if (cur.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in cur.EnumerateArray()) stack.Push(item);
            }
        }
    }

    private static string? FindChannelId(JsonElement root)
    {
        var stack = new Stack<JsonElement>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in cur.EnumerateObject())
                {
                    if ((prop.NameEquals("channelId") || prop.NameEquals("externalId") || prop.NameEquals("browseId"))
                        && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var id = prop.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(id) && id.StartsWith("UC"))
                            return id;
                    }
                    stack.Push(prop.Value);
                }
            }
            else if (cur.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in cur.EnumerateArray()) stack.Push(item);
            }
        }
        return null;
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

    private static bool IsWithin48Hours(string? publishedText)
    {
        if (string.IsNullOrWhiteSpace(publishedText)) return false;
        var s = publishedText.Trim().ToLowerInvariant();

        // Treat live indicators as within 48h
        if (s.Contains("live now") || s.Contains("실시간") || s.Contains("라이브") || s.Contains("스트리밍 중"))
            return true;

        // English pattern: "X unit ago"
        var mEn = Regex.Match(s, @"(\d+)\s+(second|minute|hour|day)s?\s+ago");
        if (mEn.Success)
        {
            int val = int.Parse(mEn.Groups[1].Value);
            string unit = mEn.Groups[2].Value;
            return unit switch
            {
                "second" => true,
                "minute" => true,
                "hour" => val < 48,
                "day" => val < 2,
                _ => false
            };
        }

        // Korean pattern: "X단위 전" (초/분/시간/일)
        var mKo = Regex.Match(s, @"(\d+)\s*(초|분|시간|일)\s*전");
        if (mKo.Success)
        {
            int val = int.Parse(mKo.Groups[1].Value);
            string unit = mKo.Groups[2].Value;
            return unit switch
            {
                "초" => true,
                "분" => true,
                "시간" => val < 48,
                "일" => val < 2,
                _ => false
            };
        }

        return false;
    }
}
