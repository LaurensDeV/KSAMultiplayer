using System.Collections.Concurrent;

namespace KSAMultiplayer;

/// <summary>
/// The mod's own log, in the Logs folder of the profile KSA was started with, the session before kept
/// beside it. Written from a thread of its own, since a write can wait on the disk and the game cannot.
/// </summary>
internal static class Log
{
    private static readonly ConcurrentQueue<string> Lines = new();
    private static readonly AutoResetEvent Wake = new(false);
    private static Thread? _writer;
    private static volatile bool _closing;

    public static string? Path { get; private set; }

    public static void Info(string what) => Write("INFO", what);
    public static void Warn(string what) => Write("WARN", what);
    public static void Error(string what, Exception e) => Write("ERROR", $"{what}: {e}");

    private static void Write(string level, string what)
    {
        Lines.Enqueue($"{DateTime.Now:HH:mm:ss.fff} {level} {what}");
        Start();
        Wake.Set();
    }

    private static void Start()
    {
        if (_writer is not null) return;
        lock (Lines)
        {
            if (_writer is not null) return;
            _writer = new Thread(Run) { IsBackground = true, Name = "KSAMultiplayer log" };
            _writer.Start();
        }
    }

    private static void Run()
    {
        StreamWriter? file = null;
        try
        {
            string folder = System.IO.Path.Combine(KSA.Constants.DocumentsFolderPath, "Logs");
            Directory.CreateDirectory(folder);
            Path = System.IO.Path.Combine(folder, "KSAMultiplayer.log");
            if (File.Exists(Path)) File.Move(Path, System.IO.Path.Combine(folder, "KSAMultiplayer.prev.log"), overwrite: true);
            file = new StreamWriter(Path) { AutoFlush = false };
        }
        catch (Exception)
        {
            // No log is better than no game.
        }
        while (true)
        {
            Wake.WaitOne(500);
            while (Lines.TryDequeue(out string? line)) file?.WriteLine(line);
            try { file?.Flush(); } catch (IOException) { }
            if (_closing && Lines.IsEmpty) break;
        }
        file?.Dispose();
    }

    public static void Shutdown()
    {
        _closing = true;
        Wake.Set();
        _writer?.Join(1000);
    }
}
