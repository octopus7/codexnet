using YoutubeCli.Providers;

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

        Console.WriteLine($"Handle: {handle}");
        Console.WriteLine($"Mode: {(string.IsNullOrWhiteSpace(apiKey) ? "Web" : "API")}");

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("codex-cli-youtube/1.0");

        IVideoProvider provider = string.IsNullOrWhiteSpace(apiKey)
            ? new WebVideoProvider(http)
            : new ApiVideoProvider(http, apiKey);

        try
        {
            var videos = await provider.GetRecentAsync(handle, maxResults);
            if (videos.Count == 0)
            {
                Console.WriteLine("최근 영상이 없습니다.");
                return 0;
            }

            foreach (var v in videos)
            {
                string views = v.ViewCount?.ToString() ?? "-";
                string likes = v.LikeCount?.ToString() ?? "-";
                string comments = v.CommentCount?.ToString() ?? "-";
                Console.WriteLine($"id={v.Id} | views={views} | likes={likes} | comments={comments} | title={v.Title}");
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
}
