using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YoutubeCli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("사용법: dotnet run --project src/youtube.csproj -- @handle [최대개수]");
            Console.Error.WriteLine("예: dotnet run --project src/youtube.csproj -- @GoogleDevelopers 5");
            return 1;
        }

        var handle = args[0].Trim();
        if (!handle.StartsWith("@")) handle = "@" + handle;

        int maxResults = 5;
        if (args.Length >= 2 && int.TryParse(args[1], out var n) && n > 0 && n <= 50)
            maxResults = n;

        var apiKey = Environment.GetEnvironmentVariable("YOUTUBE_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.Error.WriteLine("환경변수 YOUTUBE_API_KEY 가 설정되어 있지 않습니다.");
            Console.Error.WriteLine("Google Cloud Console에서 API 키를 발급받아 export YOUTUBE_API_KEY=\"<YOUR_KEY>\" 로 설정하세요.");
            return 2;
        }

        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("codex-cli-youtube/1.0");

            var channelId = await GetChannelIdByHandleAsync(http, apiKey, handle);
            if (channelId is null)
            {
                Console.Error.WriteLine($"핸들을 채널로 해석할 수 없습니다: {handle}");
                return 3;
            }

            var videoIds = await GetRecentVideoIdsAsync(http, apiKey, channelId, maxResults);
            if (videoIds.Count == 0)
            {
                Console.WriteLine("최근 영상이 없습니다.");
                return 0;
            }

            var videos = await GetVideoDetailsAsync(http, apiKey, videoIds);

            foreach (var v in videos)
            {
                var views = v.Statistics?.ViewCount?.ToString() ?? "-";
                var likes = v.Statistics?.LikeCount?.ToString() ?? "-"; // 비공개/비활성일 수 있음
                var comments = v.Statistics?.CommentCount?.ToString() ?? "-"; // 비활성일 수 있음
                var title = v.Snippet?.Title ?? "(제목 없음)";
                Console.WriteLine($"id={v.Id} | views={views} | likes={likes} | comments={comments} | title={title}");
            }

            return 0;
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"HTTP 오류: {ex.Message}");
            return 10;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"예상치 못한 오류: {ex}");
            return 11;
        }
    }

    private static async Task<string?> GetChannelIdByHandleAsync(HttpClient http, string apiKey, string handle)
    {
        var url = $"https://www.googleapis.com/youtube/v3/channels?part=id&forHandle={Uri.EscapeDataString(handle)}&key={Uri.EscapeDataString(apiKey)}";
        using var resp = await http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        using var stream = await resp.Content.ReadAsStreamAsync();
        var data = await JsonSerializer.DeserializeAsync<ChannelsListResponse>(stream);
        return data?.Items?.FirstOrDefault()?.Id;
    }

    private static async Task<List<string>> GetRecentVideoIdsAsync(HttpClient http, string apiKey, string channelId, int maxResults)
    {
        // search.list 로 최신 업로드 영상 ID 가져오기
        var url = $"https://www.googleapis.com/youtube/v3/search?part=id&order=date&channelId={Uri.EscapeDataString(channelId)}&type=video&maxResults={maxResults}&key={Uri.EscapeDataString(apiKey)}";
        using var resp = await http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        using var stream = await resp.Content.ReadAsStreamAsync();
        var data = await JsonSerializer.DeserializeAsync<SearchListResponse>(stream);
        var list = data?.Items?
            .Select(i => i.Id?.VideoId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .ToList();
        return list ?? new List<string>();
    }

    private static async Task<List<Video>> GetVideoDetailsAsync(HttpClient http, string apiKey, List<string> ids)
    {
        var idParam = string.Join(',', ids);
        var url = $"https://www.googleapis.com/youtube/v3/videos?part=snippet,statistics&id={Uri.EscapeDataString(idParam)}&key={Uri.EscapeDataString(apiKey)}";
        using var resp = await http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        using var stream = await resp.Content.ReadAsStreamAsync();
        var data = await JsonSerializer.DeserializeAsync<VideosListResponse>(stream);
        return data?.Items ?? new List<Video>();
    }
}

// Response models
public sealed class ChannelsListResponse
{
    [JsonPropertyName("items")] public List<ChannelItem>? Items { get; set; }
}
public sealed class ChannelItem
{
    [JsonPropertyName("id")] public string? Id { get; set; }
}

public sealed class SearchListResponse
{
    [JsonPropertyName("items")] public List<SearchItem>? Items { get; set; }
}
public sealed class SearchItem
{
    [JsonPropertyName("id")] public SearchItemId? Id { get; set; }
}
public sealed class SearchItemId
{
    [JsonPropertyName("videoId")] public string? VideoId { get; set; }
}

public sealed class VideosListResponse
{
    [JsonPropertyName("items")] public List<Video> Items { get; set; } = new();
}
public sealed class Video
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("snippet")] public VideoSnippet? Snippet { get; set; }
    [JsonPropertyName("statistics")] public VideoStatistics? Statistics { get; set; }
}
public sealed class VideoSnippet
{
    [JsonPropertyName("title")] public string? Title { get; set; }
}
public sealed class VideoStatistics
{
    [JsonPropertyName("viewCount")] public long? ViewCount { get; set; }
    [JsonPropertyName("likeCount")] public long? LikeCount { get; set; }
    [JsonPropertyName("commentCount")] public long? CommentCount { get; set; }
}
