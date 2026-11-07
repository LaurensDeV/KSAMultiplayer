using Xunit;

using System.Buffers.Binary;
using System.Text;


namespace KSAMultiplayer.Tests;

/// <summary>
/// The half of RFC 1035 the client has to read, driven off packets built here: a resolver is not
/// something a test can rely on, and what breaks a hand-written reader is the shape of the bytes.
/// </summary>
public sealed class SrvTests
{
    private const ushort Id = 0xBEEF;

    [Fact]
    public void AsksForOneSrvRecord()
    {
        byte[] q = Srv.Ask("_ksamp._udp.mpcars.ksarmory.com", Id);
        Assert.Equal(Id, BinaryPrimitives.ReadUInt16BigEndian(q));
        Assert.Equal(0x0100, BinaryPrimitives.ReadUInt16BigEndian(q.AsSpan(2)));   // recursion asked for
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(q.AsSpan(4)));        // one question
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(q.AsSpan(6)));
        Assert.Equal(33, BinaryPrimitives.ReadUInt16BigEndian(q.AsSpan(q.Length - 4)));  // SRV
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(q.AsSpan(q.Length - 2)));   // IN
        // The name as labels: a length, then that many bytes, down to a zero.
        Assert.Equal((byte)"_ksamp".Length, q[12]);
        Assert.Equal("_ksamp", Encoding.ASCII.GetString(q.AsSpan(13, 6)));
    }

    [Fact]
    public void ReadsTheRecordTheZoneHolds()
    {
        byte[] answer = Answer(Id, [(0, 0, 50101, "mpcars.ksarmory.com")]);
        Assert.True(Srv.Read(answer, Id, out Srv.Record[] found));
        Srv.Record one = Assert.Single(found);
        Assert.Equal(50101, one.Port);
        Assert.Equal("mpcars.ksarmory.com", one.Target);
    }

    [Fact]
    public void FollowsAPointerBackIntoTheQuestion()
    {
        // The target written as a pointer at the name already in the packet, which is what a real
        // resolver sends and what a reader that only walks forwards gets wrong.
        byte[] answer = Answer(Id, [(0, 0, 50101, "_ksamp._udp.mpcars.ksarmory.com")], compressTarget: true);
        Assert.True(Srv.Read(answer, Id, out Srv.Record[] found));
        Assert.Equal("_ksamp._udp.mpcars.ksarmory.com", Assert.Single(found).Target);
    }

    [Fact]
    public void SomebodyElsesAnswerIsNotAnAnswer()
    {
        byte[] answer = Answer(Id, [(0, 0, 50101, "mpcars.ksarmory.com")]);
        Assert.False(Srv.Read(answer, 0x1234, out _));
    }

    [Fact]
    public void NoSuchNameIsAnAnswerWithNothingInIt()
    {
        byte[] answer = Answer(Id, [], rcode: 3);
        Assert.True(Srv.Read(answer, Id, out Srv.Record[] found));
        Assert.Empty(found);
    }

    [Fact]
    public void CutShortIsNotTrusted()
    {
        byte[] answer = Answer(Id, [(0, 0, 50101, "mpcars.ksarmory.com")], truncated: true);
        Assert.False(Srv.Read(answer, Id, out _));
    }

    [Theory]
    [InlineData(4)]      // a record that stops inside its own rdata
    [InlineData(20)]     // and one that stops in the middle of a name
    public void APacketThatStopsPartWayIsRefused(int cut)
    {
        byte[] answer = Answer(Id, [(0, 0, 50101, "mpcars.ksarmory.com")]);
        Assert.False(Srv.Read(answer.AsSpan(0, answer.Length - cut), Id, out _));
    }

    [Fact]
    public void APointerThatPointsAtItselfIsRefused()
    {
        byte[] answer = Answer(Id, [(0, 0, 50101, "mpcars.ksarmory.com")]);
        // The answer's own name turned into a pointer at the record that holds it.
        int name = 12 + Labelled("_ksamp._udp.mpcars.ksarmory.com").Length + 4;
        answer[name] = 0xC0;
        answer[name + 1] = (byte)name;
        Assert.False(Srv.Read(answer, Id, out _));
    }

    [Fact]
    public void ADotMeansTheServiceIsDeliberatelyNotHere()
    {
        byte[] answer = Answer(Id, [(0, 0, 50101, "")]);
        Assert.True(Srv.Read(answer, Id, out Srv.Record[] found));
        Assert.Equal("", Assert.Single(found).Target);
    }

    [Fact]
    public void TakesTheLowestPriorityOffered()
    {
        Srv.Record[] found =
        [
            new(20, 0, 50102, "second.example.com"),
            new(10, 0, 50101, "first.example.com"),
        ];
        for (int seed = 0; seed < 32; seed++)
        {
            Assert.Equal("first.example.com", Srv.Pick(found, new Random(seed))!.Value.Target);
        }
    }

    [Fact]
    public void SharesOutByWeightWithinAPriority()
    {
        Srv.Record[] found = [new(10, 1, 1, "rare.example.com"), new(10, 99, 2, "common.example.com")];
        int rare = 0;
        for (int seed = 0; seed < 2000; seed++)
        {
            if (Srv.Pick(found, new Random(seed))!.Value.Port == 1) rare++;
        }
        // About 1 in 100. Wide enough that a fair draw never trips it and a broken one always does.
        Assert.InRange(rare, 1, 120);
    }

    [Fact]
    public void NothingToPickFrom() => Assert.Null(Srv.Pick([], Random.Shared));

    [Fact]
    public async Task AnAddressTypedAsDigitsIsNobodysName() =>
        Assert.Null(await Srv.LookupAsync("127.0.0.1", TimeSpan.FromMilliseconds(50)));

    [Fact]
    public async Task NothingTypedAtAll() =>
        Assert.Null(await Srv.LookupAsync("   ", TimeSpan.FromMilliseconds(50)));

    private static byte[] Labelled(string name)
    {
        List<byte> m = [];
        foreach (string label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            m.Add((byte)label.Length);
            m.AddRange(Encoding.ASCII.GetBytes(label));
        }
        m.Add(0);
        return [.. m];
    }

    /// <summary>A resolver's reply, written the way one writes it: the question back, then the records.</summary>
    private static byte[] Answer(ushort id, (ushort Priority, ushort Weight, ushort Port, string Target)[] records,
                                 int rcode = 0, bool truncated = false, bool compressTarget = false)
    {
        const string Asked = "_ksamp._udp.mpcars.ksarmory.com";
        List<byte> m =
        [
            (byte)(id >> 8), (byte)id,
            (byte)(0x81 | (truncated ? 0x02 : 0x00)), (byte)(0x80 | rcode),
            0x00, 0x01,
            (byte)(records.Length >> 8), (byte)records.Length,
            0x00, 0x00,
            0x00, 0x00,
        ];
        m.AddRange(Labelled(Asked));
        m.AddRange([0x00, 33, 0x00, 1]);
        foreach ((ushort priority, ushort weight, ushort port, string target) in records)
        {
            m.AddRange([0xC0, 12]);                               // the name, as a pointer at the question
            m.AddRange([0x00, 33, 0x00, 1, 0x00, 0x00, 0x01, 0x2C]);
            byte[] written = compressTarget ? [0xC0, 12] : Labelled(target);
            int length = 6 + written.Length;
            m.AddRange([(byte)(length >> 8), (byte)length]);
            m.AddRange([(byte)(priority >> 8), (byte)priority, (byte)(weight >> 8), (byte)weight,
                        (byte)(port >> 8), (byte)port]);
            m.AddRange(written);
        }
        return [.. m];
    }
}
