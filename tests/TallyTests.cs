using Xunit;

namespace KSAMultiplayer.Tests;

public class TallyTests
{
    /// <summary>The first is said at once, the rest of the minute as one line once it is over, and then the first again.</summary>
    [Fact]
    public void TheFirstIsSaidAndTheRestOfTheMinuteAreCountedIntoOneLine()
    {
        Tally t = new(60.0);
        Assert.True(t.Add(0.0, 0.6, "a"));
        Assert.Null(t.Due(1.0));

        Assert.False(t.Add(5.0, 0.7, "b"));
        Assert.False(t.Add(10.0, 2.5, "c"));
        Assert.False(t.Add(20.0, 0.9, "d"));
        Assert.Null(t.Due(59.0));
        Assert.Equal((3, 5.0, 2.5, "c"), t.Due(60.0));
        Assert.Null(t.Due(61.0));

        // A quiet minute since the last line: the next is said at once again.
        Assert.False(t.Add(70.0, 1.0));
        Assert.Equal((1, 70.0, 1.0, ""), t.Due(120.0));
        Assert.True(t.Add(185.0, 1.0));
    }

    /// <summary>However many there are, a window holds two lines at most.</summary>
    [Fact]
    public void AThousandAMinuteAreStillTwoLines()
    {
        Tally t = new(60.0);
        int lines = 0;
        for (int i = 0; i < 1000; i++)
        {
            double now = i * 0.06;
            if (t.Add(now, 0.5)) lines++;
            if (t.Due(now) is not null) lines++;
        }
        if (t.Due(60.0) is not null) lines++;
        Assert.Equal(2, lines);
    }
}
