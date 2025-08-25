using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using YoutubeCli;
using System.Globalization;
using System.Text;

namespace YoutubeCli.Providers;

public record VideoInfo(string Id, string Title, long? ViewCount, long? LikeCount, long? CommentCount, string Type);

public interface IVideoProvider
{
    Task<List<VideoInfo>> GetRecentAsync(string handle, int maxResults, int daysWindow);
}

public sealed class ApiVideoProvider(HttpClient http, string apiKey) : IVideoProvider
{
    private readonly HttpClient _http = http;
    private readonly string _apiKey = apiKey;

    public async Task<List<VideoInfo>> GetRecentAsync(string handle, int maxResults, int daysWindow)
    {
        Console.WriteLine($"[API] Resolving channelId for {handle} ...");
        var channelId = await GetChannelIdByHandleAsync(handle);
        if (channelId is null)
        {
            Console.WriteLine("[API] channelId not found.");
            return new List<VideoInfo>();
        }
        Console.WriteLine($"[API] channelId: {channelId}");
        var subsText = await GetSubscriberTextAsync(channelId);
        Console.WriteLine($"[API] subscribers: {subsText}");

        var ids = await GetRecentVideoIdsAsync(channelId, Math.Clamp(maxResults, 1, 50), daysWindow);
        if (ids.Count == 0) return new List<VideoInfo>();

        var details = await GetVideoDetailsAsync(ids);
        return details.Select(v =>
        {
            var type = (v.Snippet?.LiveBroadcastContent?.ToLowerInvariant()) switch
            {
                "live" or "upcoming" => "stream",
                _ => (TryParseIsoDurationSeconds(v.ContentDetails?.Duration) <= 60) ? "shorts" : "video"
            };
            return new VideoInfo(
                v.Id ?? string.Empty,
                v.Snippet?.Title ?? "(제목 없음)",
                v.Statistics?.ViewCount,
                v.Statistics?.LikeCount,
                v.Statistics?.CommentCount,
                type
            );
        }).ToList();
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

    private async Task<string> GetSubscriberTextAsync(string channelId)
    {
        try
        {
            var url = $"https://www.googleapis.com/youtube/v3/channels?part=statistics&id={Uri.EscapeDataString(channelId)}&key={Uri.EscapeDataString(_apiKey)}";
            using var resp = await _http.GetAsync(url);
            resp.EnsureSuccessStatusCode();
            using var stream = await resp.Content.ReadAsStreamAsync();
            var doc = await JsonSerializer.DeserializeAsync<ChannelsStatsResponse>(stream);
            var st = doc?.Items?.FirstOrDefault()?.Statistics;
            if (st is null) return "unknown";
            if (st.HiddenSubscriberCount == true) return "hidden";
            if (long.TryParse(st.SubscriberCount, out var n)) return n.ToString("N0", CultureInfo.InvariantCulture);
            return st.SubscriberCount ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private async Task<List<string>> GetRecentVideoIdsAsync(string channelId, int maxResults, int daysWindow)
    {
        var publishedAfter = DateTime.UtcNow.AddDays(-Math.Max(1, daysWindow)).ToString("o");
        var url = $"https://www.googleapis.com/youtube/v3/search?part=id&order=date&channelId={Uri.EscapeDataString(channelId)}&type=video&maxResults={maxResults}&publishedAfter={Uri.EscapeDataString(publishedAfter)}&key={Uri.EscapeDataString(_apiKey)}";
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
        var url = $"https://www.googleapis.com/youtube/v3/videos?part=snippet,statistics,contentDetails&id={Uri.EscapeDataString(idParam)}&key={Uri.EscapeDataString(_apiKey)}";
        using var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        using var stream = await resp.Content.ReadAsStreamAsync();
        var data = await JsonSerializer.DeserializeAsync<VideosListResponse>(stream);
        return data?.Items ?? new List<Video>();
    }

    private static int TryParseIsoDurationSeconds(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return int.MaxValue;
        try
        {
            // Simple parser for PT#M#S
            var m = Regex.Match(iso, @"PT(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?");
            if (!m.Success) return int.MaxValue;
            int h = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0;
            int mn = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
            int s = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
            return h * 3600 + mn * 60 + s;
        }
        catch { return int.MaxValue; }
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
    private sealed class ChannelsStatsResponse
    {
        [JsonPropertyName("items")] public List<ChannelStatsItem>? Items { get; set; }
    }
    private sealed class ChannelStatsItem
    {
        [JsonPropertyName("statistics")] public ChannelStatistics? Statistics { get; set; }
    }
    private sealed class ChannelStatistics
    {
        [JsonPropertyName("subscriberCount")] public string? SubscriberCount { get; set; }
        [JsonPropertyName("hiddenSubscriberCount")] public bool? HiddenSubscriberCount { get; set; }
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
        [JsonPropertyName("contentDetails")] public VideoContentDetails? ContentDetails { get; set; }
    }
    private sealed class VideoSnippet
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("liveBroadcastContent")] public string? LiveBroadcastContent { get; set; }
    }
    private sealed class VideoContentDetails
    {
        [JsonPropertyName("duration")] public string? Duration { get; set; }
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

    public async Task<List<VideoInfo>> GetRecentAsync(string handle, int maxResults, int daysWindow)
    {
        var results = new List<VideoInfo>();

        // Videos tab: also logs channelId once
        await AppendFromTabAsync(handle, "videos", Math.Max(1, maxResults - results.Count), results, daysWindow, logChannelId: true);

        // Streams tab
        if (results.Count < maxResults)
            await AppendFromTabAsync(handle, "streams", maxResults - results.Count, results, daysWindow);

        // Shorts tab
        if (results.Count < maxResults)
            await AppendFromTabAsync(handle, "shorts", maxResults - results.Count, results, daysWindow);

        return results;
    }

    private async Task AppendFromTabAsync(string handle, string tab, int remaining, List<VideoInfo> sink, int daysWindow, bool logChannelId = false)
    {
        if (remaining <= 0) return;
        Diag.Print($"[WEB] Fetching /{tab} ...");
        var url = $"https://www.youtube.com/{Uri.EscapeDataString(handle)}/{tab}?hl=ko";
        using var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        var html = await ReadAndCountAsync(resp);

        var json = ExtractJsonBlock(html, "ytInitialData");
        if (json is null) return;

        using var doc = JsonDocument.Parse(json);
        if (logChannelId)
        {
            Console.WriteLine($"[WEB] Resolving channelId for {handle} ...");
            var channelId = FindChannelId(doc.RootElement);
            Console.WriteLine(channelId is null ? "[WEB] channelId not found (parsing)." : $"[WEB] channelId: {channelId}");
            var subText = FindSubscriberText(doc.RootElement);
            string subsOut = "unknown";
            if (string.IsNullOrWhiteSpace(subText))
            {
                // Fallback: fetch channel root page to read header subscriber text
                var fetched = await FetchSubscriberTextForHandle(handle);
                subText = fetched;
            }
            if (!string.IsNullOrWhiteSpace(subText))
            {
                var parsed = TryParseSubscribers(subText!);
                subsOut = parsed.HasValue ? parsed.Value.ToString("N0", CultureInfo.InvariantCulture) : subText!;
            }
            Console.WriteLine($"[WEB] subscribers: {subsOut}");
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
            Diag.Print($"[WEB][LIST {tab}] id={id} | title={title} | viewsText={viewsText} | publishedText={(publishedText ?? "(없음)")}");
            bool alwaysDbg = AlwaysDebug.EnabledFor(id);
            if (alwaysDbg)
                Console.WriteLine($"[DBG][LIST {tab}] id={id} | publishedText={(publishedText ?? "(없음)")} | withinWindow(list)={IsWithinWindow(publishedText, daysWindow)}");

            bool isShortsTab = string.Equals(tab, "shorts", StringComparison.OrdinalIgnoreCase);

            string? effectivePublished = publishedText;
            long? effectiveViews = views;
            long? likeCount = null;
            long? commentCount = null;

            if (isShortsTab)
            {
                // Shorts: always fetch detail for first 2 to get time; others are skipped
                if (shortsDetailUsed < 2)
                {
                    var det = await GetWatchDetailsAsync(id);
                    effectivePublished = det.publishedText ?? publishedText;
                    effectiveViews = det.viewCount ?? views;
                    likeCount = det.likeCount ?? likeCount;
                    commentCount = det.commentCount ?? commentCount;
                    shortsDetailUsed++;
                    if (!IsWithinWindow(effectivePublished, daysWindow)) continue;
                }
                else
                {
                    // Skip remaining shorts because list-time is unreliable and detail budget is limited
                    continue;
                }
            }
            else
            {
                // Videos/Streams: trust list-time; if it's within 48h, do NOT override with detail page
                if (!IsWithinWindow(publishedText, daysWindow))
                {
                    if (alwaysDbg)
                        Console.WriteLine($"[DBG][SKIP {tab}] id={id} | reason=list-withinWindow=false");
                    continue;
                }
                if (alwaysDbg)
                    Console.WriteLine($"[DBG][TRUST {tab}] id={id} | reason=list-withinWindow=true (skip detail)");
                // keep effectivePublished/effectiveViews as from list
                // For streams, still fetch likes/comments from detail without trusting its published time
                if (string.Equals(tab, "streams", StringComparison.OrdinalIgnoreCase))
                {
                    var det = await GetWatchDetailsAsync(id);
                    likeCount = det.likeCount ?? likeCount;
                    commentCount = det.commentCount ?? commentCount;
                    effectiveViews = det.viewCount ?? effectiveViews;
                }
            }

            Diag.Print($"[WEB][ADD {tab}] id={id} | published={(effectivePublished ?? "(없음)")} | views={(effectiveViews?.ToString() ?? "-")}");
            var typeStr = isShortsTab ? "shorts" : (string.Equals(tab, "streams", StringComparison.OrdinalIgnoreCase) ? "stream" : "video");
            sink.Add(new VideoInfo(id, title, effectiveViews, likeCount, commentCount, typeStr));
            added++;
            if (added >= remaining) break;
        }
    }

    private async Task<(string? publishedText, long? viewCount, long? likeCount, long? commentCount)> GetWatchDetailsAsync(string videoId)
    {
        try
        {
            var url = $"https://www.youtube.com/watch?v={Uri.EscapeDataString(videoId)}&hl=ko";
            using var resp = await _http.GetAsync(url);
            resp.EnsureSuccessStatusCode();
            var html = await ReadAndCountAsync(resp);

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
            long? likeCount = null;
            long? commentCount = null;
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

                // Try to find like count text anywhere in initial data
                var likeText = FindFirstStringContaining(idDoc.RootElement, new[] { "좋아요", "likes" });
                if (!string.IsNullOrWhiteSpace(likeText))
                {
                    likeCount = TryParseSubscribers(likeText!); // reuse numeric + unit parser
                }

                // Try to find comments count (댓글 n개 / n comments)
                var commentText = FindFirstStringContaining(idDoc.RootElement, new[] { "댓글", "comments" });
                if (!string.IsNullOrWhiteSpace(commentText))
                {
                    commentCount = TryParseSubscribers(commentText!);
                }
            }

            // Fallback: search raw HTML for likes/comments text
            if (likeCount is null)
            {
                var m1 = Regex.Match(html, @"(좋아요|likes)\s*([0-9][0-9,\. ]*)", RegexOptions.IgnoreCase);
                if (m1.Success) likeCount = TryParseSubscribers(m1.Groups[2].Value);
            }
            if (commentCount is null)
            {
                var m2 = Regex.Match(html, @"(댓글|comments?)\s*([0-9][0-9,\. ]*)", RegexOptions.IgnoreCase);
                if (m2.Success) commentCount = TryParseSubscribers(m2.Groups[2].Value);
            }

            return (published, viewCount, likeCount, commentCount);
        }
        catch (Exception ex)
        {
            Diag.Print($"[WEB][DETAIL] {videoId} detail fetch error: {ex.Message}");
            return (null, null, null, null);
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

    private static string? FindFirstStringContaining(JsonElement root, string[] needles)
    {
        var stack = new Stack<JsonElement>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            switch (cur.ValueKind)
            {
                case JsonValueKind.String:
                    var s = cur.GetString();
                    if (!string.IsNullOrEmpty(s))
                    {
                        var low = s.ToLowerInvariant();
                        if (needles.Any(n => low.Contains(n))) return s;
                    }
                    break;
                case JsonValueKind.Object:
                    foreach (var p in cur.EnumerateObject()) stack.Push(p.Value);
                    break;
                case JsonValueKind.Array:
                    foreach (var e in cur.EnumerateArray()) stack.Push(e);
                    break;
            }
        }
        return null;
    }

    private static string? FindSubscriberText(JsonElement root)
    {
        // Try common paths for subscriberCountText in header
        // It often appears as { subscriberCountText: { simpleText: "구독자 N명" } }
        var stack = new Stack<JsonElement>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur.ValueKind == JsonValueKind.Object)
            {
                if (cur.TryGetProperty("subscriberCountText", out var sct))
                {
                    var txt = TryGetText(sct, Array.Empty<string>());
                    if (!string.IsNullOrWhiteSpace(txt)) return txt;
                }
                foreach (var prop in cur.EnumerateObject()) stack.Push(prop.Value);
            }
            else if (cur.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in cur.EnumerateArray()) stack.Push(item);
            }
        }
        return null;
    }

    private async Task<string?> FetchSubscriberTextForHandle(string handle)
    {
        try
        {
            foreach (var tab in new[] { "", "about" })
            {
                var url = $"https://www.youtube.com/{Uri.EscapeDataString(handle)}/{tab}?hl=ko".TrimEnd('/');
                using var resp = await _http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) continue;
                var html = await ReadAndCountAsync(resp);
                var json = ExtractJsonBlock(html, "ytInitialData");
                if (json is null) continue;
                using var doc = JsonDocument.Parse(json);
                var txt = FindSubscriberText(doc.RootElement);
                if (!string.IsNullOrWhiteSpace(txt)) return txt;
            }
        }
        catch { }
        return null;
    }

    private static async Task<string> ReadAndCountAsync(HttpResponseMessage resp)
    {
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        Diag.AddTraffic(bytes.LongLength);
        return Encoding.UTF8.GetString(bytes);
    }

    private static long? TryParseSubscribers(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim().ToLowerInvariant();
        // remove common words
        s = s.Replace("구독자", string.Empty)
             .Replace("구독", string.Empty)
             .Replace("명", string.Empty)
             .Replace("subscriber", string.Empty)
             .Replace("subscribers", string.Empty)
             .Trim();

        // capture number and optional unit
        var m = Regex.Match(s, @"([0-9]+(?:[\.,][0-9]+)?)\s*(천|만|억|k|m|b)?");
        if (!m.Success) return null;
        var numPart = m.Groups[1].Value;
        var unit = m.Groups[2].Success ? m.Groups[2].Value : string.Empty;

        // normalize decimal separator: keep '.' as decimal, remove thousand commas
        if (numPart.Contains('.') && numPart.Contains(','))
            numPart = numPart.Replace(",", string.Empty);
        else if (!numPart.Contains('.'))
            numPart = numPart.Replace(",", string.Empty);

        if (!double.TryParse(numPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            // Try parsing with current culture as fallback
            if (!double.TryParse(numPart, out value)) return null;
        }

        double scale = unit switch
        {
            "천" => 1_000d,
            "만" => 10_000d,
            "억" => 100_000_000d,
            "k" => 1_000d,
            "m" => 1_000_000d,
            "b" => 1_000_000_000d,
            _ => 1d
        };

        var result = Math.Round(value * scale);
        if (result < 0 || result > long.MaxValue) return null;
        return (long)result;
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

    private static bool IsWithinWindow(string? publishedText, int daysWindow)
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
                "hour" => val < daysWindow * 24,
                "day" => val < daysWindow,
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
                "시간" => val < daysWindow * 24,
                "일" => val < daysWindow,
                _ => false
            };
        }

        return false;
    }
}
