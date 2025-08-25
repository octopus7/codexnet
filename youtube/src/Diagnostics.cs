namespace YoutubeCli;

internal static class Diag
{
    public static bool Enabled { get; set; }
    public static long TrafficBytes { get; private set; }

    public static void Print(string message)
    {
        if (Enabled)
            Console.WriteLine(message);
    }

    public static void AddTraffic(long bytes)
    {
        if (bytes > 0) TrafficBytes += bytes;
    }
}
