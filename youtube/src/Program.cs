using YoutubeCli.Providers;
using System.Linq;

namespace YoutubeCli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("사용법: dotnet run --project src/youtube.csproj -- [--debug] @handle [최대개수]");
            Console.Error.WriteLine("예: dotnet run --project src/youtube.csproj -- --debug @GoogleDevelopers 5");
            return 1;
        }

        // Parse flags and positional args
        bool debug = false;
        var positionals = new List<string>();
        foreach (var a in args)
        {
            if (a == "--debug" || a == "--verbose" || a == "-v") { debug = true; continue; }
            positionals.Add(a);
        }
        if (positionals.Count == 0)
        {
            Console.Error.WriteLine("에러: 핸들이 필요합니다. 예: @GoogleDevelopers");
            return 1;
        }

        var handle = positionals[0].Trim();
        if (!handle.StartsWith("@")) handle = "@" + handle;

        int maxResults = 5;
        if (positionals.Count >= 2 && int.TryParse(positionals[1], out var n) && n > 0 && n <= 50)
            maxResults = n;

        var apiKey = Environment.GetEnvironmentVariable("YOUTUBE_API_KEY");

        // Set debug flag globally
        Diag.Enabled = debug;

        Console.WriteLine($"Handle: {handle}");
        Console.WriteLine($"Mode: {(string.IsNullOrWhiteSpace(apiKey) ? "Web" : "API")}");
        Console.WriteLine($"Count: {maxResults}");

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("codex-cli-youtube/1.0");

        IVideoProvider provider = string.IsNullOrWhiteSpace(apiKey)
            ? new WebVideoProvider(http)
            : new ApiVideoProvider(http, apiKey);

        try
        {
            var videos = await provider.GetRecentAsync(handle, maxResults);
            // Print result breakdown by type right after channel id logging
            var groups = videos
                .GroupBy(v => (v.Type ?? "video").ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.Count());
            int cVideo = groups.GetValueOrDefault("video", 0);
            int cStream = groups.GetValueOrDefault("stream", 0);
            int cShorts = groups.GetValueOrDefault("shorts", 0);
            Console.WriteLine($"[result] video : {cVideo}, stream : {cStream}, shorts : {cShorts}");
            if (videos.Count == 0)
            {
                Console.WriteLine("최근 영상이 없습니다.");
                if (provider is WebVideoProvider)
                {
                    var bytes = Diag.TrafficBytes;
                    Console.WriteLine($"Traffic: {bytes:N0} bytes (~{bytes / 1024.0 / 1024.0:F2} MB)");
                }
                return 0;
            }

            Console.WriteLine("id,views,likes,comments,title");
            foreach (var v in videos)
            {
                string views = v.ViewCount?.ToString() ?? "-";
                string likes = v.LikeCount?.ToString() ?? "-";
                string comments = v.CommentCount?.ToString() ?? "-";
                Console.WriteLine(ToCsv(v.Id, views, likes, comments, v.Title));
            }
            if (provider is WebVideoProvider)
            {
                var bytes = Diag.TrafficBytes;
                Console.WriteLine($"Traffic: {bytes:N0} bytes (~{bytes / 1024.0 / 1024.0:F2} MB)");
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

    private static string ToCsv(params string?[] fields)
    {
        static string Esc(string? s)
        {
            s ??= string.Empty;
            var needs = s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r');
            s = s.Replace("\"", "\"\"");
            return needs ? $"\"{s}\"" : s;
        }
        return string.Join(',', fields.Select(Esc));
    }
}
