using System.Diagnostics;
using System.Text;
using Xunit;

namespace KSAMultiplayer.Tests;

/// <summary>
/// What `ssh ops@ksamp.gg logs` shows (infra/server/ops/ksamp-ops-root) against what an operator's
/// game is sent: the same lines with the same addresses and user folders taken out. The two are a
/// Python and a .NET regex, and a line one hides and the other does not is a player's address in
/// an operator's terminal.
/// </summary>
public class OpsTests
{
    private static string Root()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            string script = Path.Combine(d.FullName, "infra", "server", "ops", "ksamp-ops-root");
            if (File.Exists(script)) return script;
        }
        throw new FileNotFoundException("infra/server/ops/ksamp-ops-root is not above the tests");
    }

    // Imported, not run: run, it would want root, Docker and the journal.
    private static string[] Scrubbed(string[] lines)
    {
        const string program = """
            import importlib.machinery, importlib.util, sys
            loader = importlib.machinery.SourceFileLoader("ops", sys.argv[1])
            spec = importlib.util.spec_from_loader("ops", loader)
            ops = importlib.util.module_from_spec(spec)
            loader.exec_module(ops)
            sys.stdout.reconfigure(encoding="utf-8")
            for line in sys.stdin.buffer.read().decode("utf-8").split("\n"):
                print(ops.scrubbed(line))
            """;
        ProcessStartInfo start = new("python3") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { "-I", "-B", "-c", program, Root() }) start.ArgumentList.Add(a);
        using Process p = Process.Start(start)!;
        using (Stream input = p.StandardInput.BaseStream)
            input.Write(Encoding.UTF8.GetBytes(string.Join("\n", lines)));
        string said = p.StandardOutput.ReadToEnd();
        string errors = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, errors);
        return said.TrimEnd('\n').Split('\n');
    }

    private static readonly string[] Lines =
    [
        "Pat has joined from 198.51.100.7:50100",
        "refused Bob from 10.0.0.2",
        "Pat is joining again from [::ffff:198.51.100.7]:4000",
        "[::1]:5 and [fe80::1%eth0]",
        "for players on KSA 2026.11.16.5901",
        "ksamp-server 0.27.6, speaking protocol 31",
        "universe time 1234.5 s at 2026-11-21 10:00:00",
        "looking up 1.2.3.4...",
        "1.2.3.4.5 is not an address, nor is v1.2.3.4",
        "999.1.1.1:99999 is shaped like one",
        @"C:\Users\devoo\AppData and /home/laurens/x and /mnt/c/Users/A B/y and /USERS/z",
        "no address here, nor a folder: /var/lib/docker",
    ];

    [Fact]
    public void TheShellHidesWhatTheGameHides()
    {
        string[] shell = Scrubbed(Lines);
        Assert.Equal(Lines.Select(LogText.Scrubbed), shell);
    }

    [Fact]
    public void NoControlCharacterReachesAnOperatorsTerminal()
    {
        string[] shell = Scrubbed(["\u001b[2J\u001b]0;owned\u0007cleared\tand tabbed\u009b31m"]);
        Assert.Equal("[2J]0;ownedcleared\tand tabbed31m", Assert.Single(shell));
    }
}
