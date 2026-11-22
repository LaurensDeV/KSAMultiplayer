namespace KSAMultiplayer;

/// <summary>
/// The server's log, as an operator asked for it: written to a text file on this machine and opened
/// in whatever opens text files here. A file and not a window in the game, because it is read
/// alongside the game, searched, and pasted to somebody else.
/// </summary>
internal static class ServerLogs
{
    /// <summary>
    /// Where they are kept: KSA's documents folder, beside the mod's tokens, and not AppData. A game
    /// started from inside a packaged app writes AppData into that package's own copy of it, and the
    /// editor the shell opens outside the package then finds no file at the path it was given.
    /// </summary>
    public static string Folder => Path.Combine(KSA.Constants.DocumentsFolderPath, "KSAMultiplayer", "server-logs");

    /// <summary>Asks the server for the log of the last <paramref name="days"/> days.</summary>
    public static void Ask(int days = 1) => Sync.Ask(days <= 1 ? "logs" : $"logs {days}");

    public static void Open(string server, Blob blob)
    {
        if (LogText.Unpack(blob.Data) is not { } text)
        {
            Sync.Say("the server sent a log that couldn't be read");
            return;
        }
        string path = Path.Combine(Folder, $"{Safe(server)} {Safe(blob.Key)} at {DateTime.Now:yyyy-MM-dd HHmmss}.txt");
        try
        {
            Directory.CreateDirectory(Folder);
            // CRLF, because Notepad before Windows 10 1809 shows the server's LF lines as one.
            File.WriteAllText(path, text.ReplaceLineEndings("\r\n"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Sync.Say($"couldn't save the server's log to {Folder}: {e.Message}");
            return;
        }
        Log.Info($"saved the server's log of {blob.Key} to {path}");
        try
        {
            global::System.Diagnostics.Process.Start(new global::System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            Sync.Say($"the server's log of {blob.Key} is open; it is saved in {Folder}");
        }
        catch (Exception e) when (e is global::System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            Sync.Say($"the server's log of {blob.Key} is saved as {path}, but couldn't be opened: {e.Message}");
        }
    }

    // A server's name and a range of days are the server's to choose, and become part of a file name here.
    private static string Safe(string s)
    {
        char[] bad = Path.GetInvalidFileNameChars();
        string clean = new([.. s.Select(c => bad.Contains(c) || char.IsControl(c) ? '_' : c)]);
        clean = clean.Trim().Trim('.');
        return clean.Length == 0 ? "server" : clean.Length > 48 ? clean[..48] : clean;
    }
}
