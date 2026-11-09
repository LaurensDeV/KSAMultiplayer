using System.Diagnostics;

namespace KSAMultiplayer.Directories;

/// <summary>
/// The one clock everything here reads.
///
/// <para>A stopwatch rather than the wall clock, because every decision in <see cref="Roster"/> is
/// about an interval and none is about a date: a machine whose clock steps backwards over NTP must
/// not list a server for an hour or drop one that answered a second ago. The wall clock is read in
/// one place, for the <c>seen</c> a listing carries, which is a date because it is read by somebody
/// else.</para>
/// </summary>
public sealed class Ticker
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();

    public double Now => _watch.Elapsed.TotalSeconds;

    public DateTimeOffset Wall => DateTimeOffset.UtcNow;
}
