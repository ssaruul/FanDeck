namespace FanDeck;

/// <summary>Appends errors to %APPDATA%\FanDeck\log.txt.</summary>
static class Log
{
    static readonly object Gate = new();

    public static string FilePath => Path.Combine(AppConfig.Folder, "log.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppConfig.Folder);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > 1_000_000)
                    file.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    public static void Write(Exception ex) => Write(ex.ToString());
}
