using System.Text;

namespace NexScreen.Companion.Util;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>Console + optional file logger. Never pass image data or secrets here.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static StreamWriter? _file;

    public static LogLevel MinLevel { get; set; } = LogLevel.Info;
    public static string? FilePath { get; private set; }

    public static void EnableFile(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            FilePath = Path.Combine(directory, $"companion-{DateTime.Now:yyyyMMdd}.log");
            _file = new StreamWriter(new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
        }
        catch (Exception ex)
        {
            FilePath = null;
            Warn($"File logging disabled: {ex.Message}");
        }
    }

    public static void Close()
    {
        lock (Gate)
        {
            _file?.Dispose();
            _file = null;
        }
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);
    public static void Error(string message) => Write(LogLevel.Error, message);

    private static void Write(LogLevel level, string message)
    {
        if (level < MinLevel)
        {
            return;
        }

        var tag = level switch
        {
            LogLevel.Debug => "DBG",
            LogLevel.Info => "INF",
            LogLevel.Warn => "WRN",
            _ => "ERR",
        };
        var line = $"{DateTime.Now:HH:mm:ss.fff} {tag} {message}";
        lock (Gate)
        {
            if (level >= LogLevel.Warn)
            {
                var old = Console.ForegroundColor;
                Console.ForegroundColor = level == LogLevel.Warn ? ConsoleColor.Yellow : ConsoleColor.Red;
                Console.WriteLine(line);
                Console.ForegroundColor = old;
            }
            else
            {
                Console.WriteLine(line);
            }

            try
            {
                _file?.WriteLine(line);
            }
            catch (IOException)
            {
                // Ignore file errors; console output is enough.
            }
        }
    }
}
