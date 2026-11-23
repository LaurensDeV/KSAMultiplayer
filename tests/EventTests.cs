using System.Text;
using Xunit;

namespace KSAMultiplayer.Tests;

/// <summary>
/// What a mod says once: the batch it travels in. Nothing here needs a server.
/// </summary>
public class EventTests
{
    internal static ModEvent Event(uint id, string channel = "cars:crash/v1", int bytes = 3, string craft = "", byte to = 0, byte from = 0, string fromName = "") =>
        new(from, fromName, to, id, channel, craft, [.. Enumerable.Range(0, bytes).Select(i => (byte)(i + id))]);

    private static void Same(ModEvent want, ModEvent got)
    {
        Assert.Equal((want.From, want.FromName, want.To, want.Id, want.Channel, want.Craft), (got.From, got.FromName, got.To, got.Id, got.Channel, got.Craft));
        Assert.Equal(want.Data, got.Data);
    }

    [Fact]
    public void ABatchOfEventsReadsBackAsItWasWritten()
    {
        ModEvent[] said =
        [
            Event(1),
            Event(2, "radio:morse/v1", 40, craft: "Kestrel"),
            Event(3, bytes: 0, from: 4, fromName: "Bob", to: 2),
            // Forty-eight characters of three bytes each: a length too long for one byte.
            Event(4, new string('信', Wire.ChannelChars), 7),
            Event(uint.MaxValue, bytes: ModEvents.EventBytes),
        ];
        byte[] batch = ModEvents.Encode(said, ModEvents.BatchBytes, out int taken);
        Assert.Equal(said.Length, taken);
        Assert.Equal(1 + said.Sum(e => ModEvents.Size(e)), batch.Length);

        ModEvent[] back = Assert.IsType<ModEvent[]>(ModEvents.Decode(batch));
        Assert.Equal(said.Length, back.Length);
        for (int i = 0; i < said.Length; i++) Same(said[i], back[i]);

        // And through the transfer it rides in, whose time is nothing: zero is a time a blob may say.
        Blob blob = Assert.IsType<Blob>(Blob.Decode(new Blob(BlobKind.ModEvent, "", 0, 0.0, batch).Encode()));
        Assert.Equal(BlobKind.ModEvent, blob.Kind);
        Assert.Equal(batch, blob.Data);
    }

    [Fact]
    public void ABatchCutShortIsNoBatchOrFewerEventsAndNeverThrows()
    {
        ModEvent[] said = [Event(1, bytes: 20), Event(2, "radio:morse/v1", 300, craft: "Kestrel"), Event(3, bytes: 0)];
        byte[] batch = ModEvents.Encode(said, ModEvents.BatchBytes, out _);
        for (int n = 0; n <= batch.Length; n++)
        {
            ModEvent[]? back = ModEvents.Decode(batch[..n]);
            if (back is null) continue;
            // A batch says no count, so one cut exactly between two events is a shorter batch: a
            // transfer is handed over whole or not at all, and this cannot happen on a line.
            Assert.InRange(back.Length, 1, said.Length);
            for (int i = 0; i < back.Length; i++) Same(said[i], back[i]);
        }

        Random noise = new(6);
        for (int i = 0; i < 500; i++)
        {
            byte[] bytes = new byte[noise.Next(1, 600)];
            noise.NextBytes(bytes);
            bytes[0] = 1;
            ModEvents.Decode(bytes);
        }
        Assert.Null(ModEvents.Decode([]));
        Assert.Null(ModEvents.Decode([1]));
        Assert.Null(ModEvents.Decode([2, .. batch[1..]]));
        Assert.Null(ModEvents.Decode(new byte[ModEvents.BatchBytes + 1]));
    }

    public static TheoryData<string, string, string, int> NotEvents() => new()
    {
        { "", "", "", 1 },
        { new string('c', Wire.ChannelChars + 1), "", "", 1 },
        { "cars:crash/v1", new string('k', Wire.KeyChars + 1), "", 1 },
        { "cars:crash/v1", "", new string('n', Wire.NameChars + 1), 1 },
        { "cars:crash/v1", "", "", ModEvents.EventBytes + 1 },
    };

    [Theory]
    [MemberData(nameof(NotEvents))]
    public void WhatIsTooBigOrOnNoChannelIsNotAnEvent(string channel, string craft, string fromName, int bytes)
    {
        ModEvent e = new(0, fromName, 0, 1, channel, craft, new byte[bytes]);
        Assert.False(ModEvents.Fits(e));

        // Written by hand, since Encode is not what stands between a server and a game that lies.
        using MemoryStream m = new();
        using (BinaryWriter w = new(m, Encoding.UTF8, leaveOpen: true))
        {
            w.Write((byte)1);
            w.Write(e.From);
            w.Write(e.FromName);
            w.Write(e.To);
            w.Write(e.Id);
            w.Write(e.Channel);
            w.Write(e.Craft);
            w.Write((ushort)e.Data.Length);
            w.Write(e.Data);
        }
        Assert.Null(ModEvents.Decode(m.ToArray()));
    }

    [Fact]
    public void ABatchIsFilledFromTheFrontAndStopsAtTheFirstThatDoesNotFit()
    {
        ModEvent small = Event(1, bytes: 10), big = Event(2, bytes: ModEvents.EventBytes), last = Event(3, bytes: 10);
        int room = 1 + ModEvents.Size(small) + ModEvents.Size(last);

        // The big one does not fit, and the small one after it is not taken in its place.
        byte[] batch = ModEvents.Encode([small, big, last], room, out int taken);
        Assert.Equal(1, taken);
        Same(small, Assert.Single(ModEvents.Decode(batch)!));

        ModEvents.Encode([big, small], 100, out taken);
        Assert.Equal(0, taken);

        // The largest thing that is an event at all still goes in a batch, alone, and in what a
        // link sends at once.
        ModEvent largest = new(255, new string('名', Wire.NameChars), 255, 9, new string('信', Wire.ChannelChars), new string('船', Wire.KeyChars), new byte[ModEvents.EventBytes]);
        Assert.True(ModEvents.Fits(largest));
        batch = ModEvents.Encode([largest, largest], ModEvents.BatchBytes, out taken);
        Assert.Equal(1, taken);
        Assert.InRange(new Blob(BlobKind.ModEvent, "", 0, 0.0, new byte[ModEvents.BatchBytes]).Encode().Length, 1, 8 * Link.ChunkBytes);
        Assert.True(ModEvents.Cost(largest) + 1 <= ModEvents.BatchBytes, "a game's allowance fills to a batch, so no event may cost more");
    }

    [Fact]
    public void AnEventCostsWhatItWeighsOnceAServerHasNamedItsSender()
    {
        // What a game sends has no name in it, and what a server passes on has: counted as it came,
        // a few bytes from a long name would be several times that in everybody's queue.
        ModEvent said = Event(1, "c", 0);
        ModEvent passedOn = said with { From = 3, FromName = new string('名', Wire.NameChars) };
        Assert.Equal(ModEvents.Size(passedOn), ModEvents.Cost(said));
        Assert.Equal(ModEvents.Cost(said), ModEvents.Cost(passedOn));
    }
}
