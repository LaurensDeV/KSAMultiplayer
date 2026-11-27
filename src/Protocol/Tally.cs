namespace KSAMultiplayer;

/// <summary>
/// Something that can happen many times a minute and is worth a line, kept to one line in every
/// <see cref="Every"/> seconds. The first time is said at once, since it is the one that matters
/// to anybody reading a log back; any more within the window are counted, and said as one line,
/// with how many and the worst of them, once the window is over. A game hitching on a big station
/// would otherwise write a line a frame.
/// </summary>
public sealed class Tally(double every = 60.0)
{
    private int _count;
    private double _worst, _since, _saidAt = double.NegativeInfinity;
    private string _worstOf = "";

    /// <summary>How many seconds at least between two lines.</summary>
    public double Every { get; } = every;

    /// <summary>
    /// One more of it, as bad as <paramref name="amount"/>, about <paramref name="of"/>. True when it
    /// is to be said now, which is when nothing has been said for a window: the caller says it.
    /// </summary>
    public bool Add(double now, double amount, string of = "")
    {
        if (now - _saidAt >= Every && _count == 0)
        {
            _saidAt = now;
            return true;
        }
        if (_count++ == 0) _since = now;
        if (amount > _worst || _count == 1) (_worst, _worstOf) = (amount, of);
        return false;
    }

    /// <summary>
    /// What was counted and not said, once its window is over: how many, since when, and the worst
    /// and what it was about. Null while there is nothing, or while the window is still open.
    /// </summary>
    public (int Count, double Since, double Worst, string Of)? Due(double now)
    {
        if (_count == 0 || now - _saidAt < Every) return null;
        (int Count, double Since, double Worst, string Of) said = (_count, _since, _worst, _worstOf);
        (_count, _worst, _worstOf, _saidAt) = (0, 0.0, "", now);
        return said;
    }
}
