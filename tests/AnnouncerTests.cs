using System.Net;
using System.Net.Sockets;
using System.Text;
using KSAMultiplayer.Server;
using Xunit;

namespace KSAMultiplayer.Tests;

/// <summary>
/// What a server asks of the internet, and when. The question these exist to settle is the one an
/// operator cannot check for themselves: that a server nobody opted in reaches for nothing at all.
///
/// <para>The directories served here are loopback addresses on a port that refuses, so a test that
/// lets the announcer act on what it learned sends its announcements nowhere off the machine.</para>
/// </summary>
public class AnnouncerTests
{
    private const string One = "https://127.0.0.1:9/v1/servers";
    private const string Two = "https://127.0.0.2:9/v1/servers";

    [Fact]
    public async Task AServerNobodyOptedInAsksNobodyWhereToAnnounce()
    {
        using Served list = new(One);
        using Pointed at = new(list.Url);

        double now = 0.0;
        using Announcer announcer = new(new ServerOptions { Advertise = false }, 50100, () => now);
        // A day of ticking, which is twenty-four of the hourly readings it would make if it made any.
        for (int i = 0; i < 24 * 60; i++)
        {
            now += 60.0;
            announcer.Tick();
        }
        await Task.Delay(200);

        Assert.Equal(0, list.Asked);
        Assert.Equal(Listings.BuiltIn, announcer.Where());
        Assert.Equal($"not listed publicly (advertise = false in {Settings.FileName})", announcer.Standing());
    }

    [Fact]
    public async Task AnOperatorWhoNamedTheirOwnDirectoriesIsNotAskedEither()
    {
        using Served list = new(One);
        using Pointed at = new(list.Url);

        double now = 0.0;
        ServerOptions options = new() { Advertise = true, Directories = Two };
        using Announcer announcer = new(options, 50100, () => now);
        for (int i = 0; i < 120; i++)
        {
            now += 60.0;
            announcer.Tick();
        }
        await Task.Delay(200);

        Assert.Equal(0, list.Asked);
        Assert.Equal([Two], announcer.Where());
    }

    [Fact]
    public async Task AnAdvertisingServerLearnsWhereToAnnounceAndSaysSo()
    {
        using Served list = new(One);
        using Pointed at = new(list.Url);

        double now = 0.0;
        using Announcer announcer = new(new ServerOptions { Advertise = true }, 50100, () => now);
        announcer.Tick();

        Assert.True(await Eventually(() => announcer.Where() is [One]));
        Assert.True(list.Asked >= 1);
        Assert.Contains($"the directory list changed: now {One}", Drain(announcer));
    }

    [Fact]
    public async Task ADirectoryDroppedFromTheListIsNoLongerAnnouncedTo()
    {
        using Served list = new(One);
        using Pointed at = new(list.Url);

        double now = 0.0;
        using Announcer announcer = new(new ServerOptions { Advertise = true }, 50100, () => now);
        announcer.Tick();
        Assert.True(await Eventually(() => announcer.Where() is [One]));

        // Announced to, so the directory is owed a withdrawal once it leaves the list.
        now += FirstAnnouncement;
        announcer.Tick();
        Assert.True(await Eventually(() => Drain(announcer).Any(s => s.Contains(One))));

        list.Serve(Two);
        now += 3600.0;
        announcer.Tick();

        Assert.True(await Eventually(() => announcer.Where() is [Two]));
        // Waited for like the rest: the list is taken up on one pass and the withdrawal said on
        // a later one, and asked for once in between it was not there yet -- which failed a
        // release whose every other job had passed.
        Assert.True(await Eventually(() => Drain(announcer).Any(s => s.Contains($"no longer listing itself with {One}"))));
    }

    [Fact]
    public async Task TheListIsReadOnceAnHourAndNotOnceAMinute()
    {
        using Served list = new(One);
        using Pointed at = new(list.Url);

        double now = 0.0;
        using Announcer announcer = new(new ServerOptions { Advertise = true }, 50100, () => now);
        announcer.Tick();
        Assert.True(await Eventually(() => announcer.Where() is [One]));
        int afterFirst = list.Asked;

        // Fifty minutes of announcing, which is fifty heartbeats and no second reading.
        for (int i = 0; i < 50; i++)
        {
            now += 60.0;
            announcer.Tick();
        }
        await Task.Delay(200);
        Assert.Equal(afterFirst, list.Asked);

        now += 11 * 60.0;
        announcer.Tick();
        Assert.True(await Eventually(() => list.Asked > afterFirst));
    }

    [Fact]
    public async Task TheListReadOnceIsRememberedForAStartWithNoInternet()
    {
        string cache = Path.Combine(Path.GetTempPath(), $"ksamp-known-{Guid.NewGuid():N}", "directories-seen.txt");
        try
        {
            using (Served list = new(One))
            using (Pointed at = new(list.Url))
            {
                double now = 0.0;
                using Announcer announcer = new(new ServerOptions { Advertise = true }, 50100, () => now, cache);
                announcer.Tick();
                Assert.True(await Eventually(() => announcer.Where() is [One]));
            }

            // Nothing answering where the list lives, which is a machine starting up without the
            // internet it had yesterday.
            using (Pointed nowhere = new("http://127.0.0.1:9/directories.txt"))
            {
                double now = 0.0;
                using Announcer announcer = new(new ServerOptions { Advertise = true }, 50100, () => now, cache);
                announcer.Tick();
                Assert.True(await Eventually(() => announcer.Where() is [One]));
            }
        }
        finally
        {
            try
            {
                System.IO.Directory.Delete(Path.GetDirectoryName(cache)!, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Past the delay the first announcement waits, so one goes out on the next tick.</summary>
    private const double FirstAnnouncement = 5.0;

    private static string[] Drain(Announcer announcer)
    {
        List<string> said = [];
        while (announcer.Said.TryDequeue(out string? s)) said.Add(s);
        return [.. said];
    }

    private static async Task<bool> Eventually(Func<bool> done, double withinSeconds = 5.0)
    {
        for (int i = 0; i < (int)(withinSeconds * 50); i++)
        {
            if (done()) return true;
            await Task.Delay(20);
        }
        return done();
    }

    /// <summary>Points <see cref="Listings.KnownAt"/> somewhere for the length of one test, and puts it back.</summary>
    private sealed class Pointed : IDisposable
    {
        private readonly string _was;

        public Pointed(string url)
        {
            _was = Listings.KnownAt;
            Listings.KnownAt = url;
        }

        public void Dispose() => Listings.KnownAt = _was;
    }

    /// <summary>The shared list of directories, served over loopback, counting what is asked of it.</summary>
    private sealed class Served : IDisposable
    {
        private readonly HttpListener _listener = new();
        private volatile string _body;
        private int _asked;

        public Served(params string[] directories)
        {
            _body = string.Join("\n", directories);
            int port;
            using (TcpListener free = new(IPAddress.Loopback, 0))
            {
                free.Start();
                port = ((IPEndPoint)free.LocalEndpoint).Port;
            }
            Url = $"http://127.0.0.1:{port}/directories.txt";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = Loop();
        }

        public string Url { get; }

        public int Asked => Volatile.Read(ref _asked);

        public void Serve(params string[] directories) => _body = string.Join("\n", directories);

        private async Task Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return;
                }
                Interlocked.Increment(ref _asked);
                byte[] bytes = Encoding.UTF8.GetBytes(_body);
                try
                {
                    context.Response.ContentType = "text/plain";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                    context.Response.Close();
                }
                catch (Exception)
                {
                }
            }
        }

        public void Dispose()
        {
            try
            {
                _listener.Close();
            }
            catch (Exception)
            {
            }
        }
    }
}
