using System.Text;
using Xunit;

namespace KSAMultiplayer.Tests;

/// <summary>A game's log sent a piece at a time: each line once, whole lines only, and a record of how far each file has gone.</summary>
public class LogPieceTests
{
    // One that can be written on, as a log still being written is.
    private static MemoryStream File(string text)
    {
        MemoryStream m = new();
        m.Write(Encoding.UTF8.GetBytes(text));
        return m;
    }

    [Fact]
    public void ALogStillBeingWrittenGoesToItsLastWholeLine()
    {
        using MemoryStream f = File("one\ntwo\nconnecting to 203.0");
        LogPieces.Piece piece = LogPieces.Next(f, 0, 0, over: false, most: 1 << 20)!.Value;
        Assert.Equal("one\ntwo\n", piece.Text);
        Assert.Equal((0, 8L, 2), (piece.First, piece.Bytes, piece.Lines));
        // Half an address is held back until its line is done, and then goes from where the last piece ended.
        f.Seek(0, SeekOrigin.End);
        f.Write(".113.7:50100\n"u8);
        LogPieces.Piece rest = LogPieces.Next(f, piece.Bytes, piece.Lines, over: false, most: 1 << 20)!.Value;
        Assert.Equal("connecting to 203.0.113.7:50100\n", rest.Text);
        Assert.Equal(2, rest.First);
        Assert.Equal(3, rest.Lines);
        Assert.Null(LogPieces.Next(f, rest.Bytes, rest.Lines, over: false, most: 1 << 20));
    }

    [Fact]
    public void ASessionOverGoesToItsEndThoughItsLastLineHasNoNewline()
    {
        using MemoryStream f = File("one\ncrashed here");
        LogPieces.Piece piece = LogPieces.Next(f, 0, 0, over: true, most: 1 << 20)!.Value;
        Assert.Equal("one\ncrashed here", piece.Text);
        Assert.Equal(2, piece.Lines);
        Assert.Null(LogPieces.Next(File("no newline yet"), 0, 0, over: false, most: 1 << 20));
    }

    [Fact]
    public void TooMuchOfALogSendsItsEndFromALineAndCountsTheLinesPassedOver()
    {
        string text = string.Concat(Enumerable.Range(0, 100).Select(i => $"line {i:D3}\n"));
        LogPieces.Piece piece = LogPieces.Next(File(text), 0, 0, over: false, most: 50)!.Value;
        Assert.StartsWith("line ", piece.Text);
        Assert.EndsWith("line 099\n", piece.Text);
        Assert.True(Encoding.UTF8.GetByteCount(piece.Text) <= 50 + 9);
        // The lines before it are counted, so its first says where it is in the file.
        Assert.Equal($"line {piece.First:D3}\n", piece.Text.Split('\n')[0] + "\n");
        Assert.Equal(100, piece.Lines);
    }

    [Fact]
    public void AFileShorterThanWhatWasSentOfItStartsOverAndOneSentWholeSendsNothing()
    {
        LogPieces.Piece piece = LogPieces.Next(File("new\n"), 400, 30, over: false, most: 1 << 20)!.Value;
        Assert.Equal(("new\n", 0, 1), (piece.Text, piece.First, piece.Lines));
        Assert.Null(LogPieces.Next(File("old\n"), LogPieces.Whole, 0, over: true, most: 1 << 20));
    }

    [Fact]
    public void AByteOrderMarkIsNotSent()
    {
        using MemoryStream f = new([0xEF, 0xBB, 0xBF, (byte)'a', (byte)'\n']);
        Assert.Equal("a\n", LogPieces.Next(f, 0, 0, over: false, most: 1 << 20)!.Value.Text);
    }

    [Fact]
    public void TheLedgerReadsWhatOlderGamesWroteAndKeepsTheFurtherOfTwoRows()
    {
        Dictionary<string, (long Bytes, int Lines)> sent = LogPieces.Ledger([
            "KSAMultiplayer.261123-152935",
            "KSAMultiplayer.261125-100000\t200\t4",
            "KSAMultiplayer.261125-100000\t900\t20",
            "KSAMultiplayer.261125-100000\t500\t9",
            "",
        ]);
        Assert.Equal((LogPieces.Whole, 0), sent["KSAMultiplayer.261123-152935"]);
        Assert.Equal((900L, 20), sent["KSAMultiplayer.261125-100000"]);
        Assert.Equal(sent, LogPieces.Ledger(LogPieces.Rows(sent)));
    }

    [Fact]
    public void APiecesKeySaysItsLogAndItsFirstLine()
    {
        string key = LogPieces.Key("KittenSpaceAgency.261124-190035.40600.20261124_1", 120);
        Assert.True(key.Length <= Wire.KeyChars);
        Assert.Equal(("KittenSpaceAgency.261124-190035.40600.20261124_1", 120), LogPieces.Parse(key));
        // As games of 0.37.0 and before name a log sent whole.
        Assert.Null(LogPieces.Parse("KSAMultiplayer.261123-152935"));
        Assert.Null(LogPieces.Parse("KSAMultiplayer.now-180000"));
        Assert.Null(LogPieces.Parse(":3"));
        Assert.Null(LogPieces.Parse("x:-3"));
    }

    [Fact]
    public void CleaningALogNeverMakesOrTakesAwayALine()
    {
        string text = "at 203.0.113.7:50100\r\nC:\\Users\\devoo\\x\n[::1]:5\n/home/laurens/y\nlast";
        Assert.Equal(LogPieces.LineCount(text), LogPieces.LineCount(LogText.Scrubbed(text)));
    }
}
