namespace QuiVrRevival.Server;

/// <summary>Server log: console (when there is one) plus server.log in the data folder.</summary>
public static class Log
{
    static readonly object Gate = new();
    static string? file;
    public static bool Console { get; set; } = true;

    public static void Init(string dataDir)
    {
        file = Path.Combine(dataDir, "server.log");
        try
        {
            if (File.Exists(file) && new FileInfo(file).Length > 5_000_000)
                File.Move(file, file + ".old", overwrite: true);
        }
        catch (IOException)
        {
        }
    }

    public static void Info(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
        lock (Gate)
        {
            if (Console) System.Console.WriteLine(line);
            try
            {
                if (file != null) File.AppendAllText(file, line + Environment.NewLine);
            }
            catch (IOException)
            {
            }
        }
    }
}
