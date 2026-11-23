using System.Text;
using KSAMultiplayer.Server;
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

/// <summary>
/// What a server does with what mods say once: who hears it, in what order, who is told when it did
/// not arrive, and that no game can make another's wait or speak in another's name. Real sockets
/// over loopback, as the rest are.
/// </summary>
public class EventRelayTests
{
    private const string Channel = "cars:crash/v1";

    // Seed has the server count Alice in the world without her game ever saying so, and a game
    // sends nothing a mod says until it has.
    private static Client First(Rig rig)
    {
        Client a = SaveTests.Seed(rig);
        a.SayLoaded(5000);
        return a;
    }

    private static GameServer.Player At(Rig rig, Client c) => rig.Server.Players.First(p => p.Id == c.Id);

    private static Blob Batch(params ModEvent[] events) =>
        new(BlobKind.ModEvent, "", 0, 0.0, ModEvents.Encode(events, ModEvents.BatchBytes, out _));

    private static bool Limited(Rig rig, string word) => rig.Server.Said.Any(s => s.Contains("over a limit") && s.Contains(word));

    // A game whose frame has stopped: on the roster, and taking nothing.
    private static void Stall(Rig rig, Client c) => rig.Clients.Remove(c);

    [Fact]
    public void AnEventReachesEveryOtherPlayerInTheWorldAndNotWhoSaidIt()
    {
        using Rig rig = new();
        Client alice = First(rig), bob = SaveTests.JoinAndLoad(rig, "Bob"), cas = SaveTests.JoinAndLoad(rig, "Cas");
        // In the room and holding the world, and not in it yet.
        Client dan = rig.Join("Dan");
        rig.Until(() => dan.Blobs.Any(b => b.Kind == BlobKind.World), seconds: 20, what: "Dan to get the world");

        byte[] said = [1, 2, 3];
        Assert.True(alice.SendEvent(1, Channel, said, "Buggy"));
        rig.Until(() => bob.Events.Count == 1 && cas.Events.Count == 1, what: "Bob and Cas to hear it");
        foreach (Client heard in (Client[])[bob, cas])
        {
            ModEvent e = heard.Events.Dequeue();
            // A craft nobody ever claimed: the server reads none of it.
            Assert.Equal((alice.Id, "Alice", (byte)0, 1u, Channel, "Buggy"), (e.From, e.FromName, e.To, e.Id, e.Channel, e.Craft));
            Assert.Equal(said, e.Data);
        }
        rig.For(0.5);
        Assert.Empty(alice.Events);
        Assert.Empty(dan.Events);
        Assert.Empty(alice.EventsLost);

        // And not kept for whoever comes into the world later: that is another thing to build.
        dan.SayLoaded(5000);
        rig.Until(() => At(rig, dan).Loaded, what: "Dan to load");
        rig.For(0.3);
        Assert.Empty(dan.Events);
    }

    [Fact]
    public void AnEventSaysWhoTheServerHeardItFromWhateverItSays()
    {
        using Rig rig = new();
        Client alice = First(rig), bob = SaveTests.JoinAndLoad(rig, "Bob"), cas = SaveTests.JoinAndLoad(rig, "Cas");
        alice.Upload(Batch(EventTests.Event(1, from: bob.Id, fromName: "Bob")));
        rig.Until(() => bob.Events.Count == 1 && cas.Events.Count == 1, what: "both to hear it");
        Assert.Equal((alice.Id, "Alice"), (cas.Events.Peek().From, cas.Events.Peek().FromName));
        Assert.Equal((alice.Id, "Alice"), (bob.Events.Peek().From, bob.Events.Peek().FromName));
    }

    [Fact]
    public void ANameMadeLongerToTellItFromAnotherStillNamesWhatItSaid()
    {
        // Two of one name are told apart by a number after the second, which makes it longer than a
        // name may be. Passed on as it stands, every game dropped the whole batch it came in.
        using Rig rig = new();
        string name = new('n', Wire.NameChars);
        Client first = rig.Join(name);
        rig.Until(() => first.Joined && first.SnapshotWanted is not null, what: "the server to ask for a world");
        first.SnapshotWanted = null;
        first.Upload(new Blob(BlobKind.World, "", first.Id, 5000, Rig.FakeWorld()));
        rig.Until(() => rig.Server.HasWorld && At(rig, first).Loaded, what: "the world to arrive");
        first.SayLoaded(5000);
        Client second = SaveTests.JoinAndLoad(rig, name), cas = SaveTests.JoinAndLoad(rig, "Cas");
        Assert.True(At(rig, second).Name.Length > Wire.NameChars);

        Assert.True(first.SendEvent(1, Channel, [1]));
        Assert.True(second.SendEvent(1, Channel, [2]));
        rig.Until(() => cas.Events.Count == 2, what: "Cas to hear both");
        ModEvent[] heard = [.. cas.Events];
        Assert.Equal(cas.Players.First(p => p.Id == second.Id).Name, heard.First(e => e.From == second.Id).FromName);
        Assert.Contains(heard, e => e.From == first.Id);
    }

    [Fact]
    public void AnEventForOnePlayerGoesToThatPlayerAlone()
    {
        using Rig rig = new();
        Client alice = First(rig), bob = SaveTests.JoinAndLoad(rig, "Bob"), cas = SaveTests.JoinAndLoad(rig, "Cas");
        Assert.True(alice.SendEvent(1, Channel, [9], to: cas.Id));
        rig.Until(() => cas.Events.Count == 1, what: "Cas to hear it");
        Assert.Equal(cas.Id, cas.Events.Peek().To);
        rig.For(0.5);
        Assert.Empty(bob.Events);
        Assert.Empty(alice.EventsLost);
    }

    [Theory]
    [InlineData("self")]
    [InlineData("nobody")]
    [InlineData("loading")]
    public void AnEventForNobodyInTheWorldIsSaidLostToWhoSaidIt(string who)
    {
        using Rig rig = new();
        Client alice = First(rig), bob = SaveTests.JoinAndLoad(rig, "Bob");
        Client dan = rig.Join("Dan");
        rig.Until(() => dan.Joined, what: "Dan to join");
        (byte to, string name) = who switch { "self" => (alice.Id, "Alice"), "loading" => (dan.Id, "Dan"), _ => ((byte)200, "") };

        Assert.True(alice.SendEvent(7, Channel, [1], to: to));
        rig.Until(() => alice.EventsLost.Count == 1, what: "Alice to be told");
        Wire.EventLost lost = alice.EventsLost.Dequeue();
        Assert.Equal((7u, to, name, Channel, Wire.EventLoss.Nobody), (lost.Id, lost.To, lost.ToName, lost.Channel, lost.Why));
        Assert.True(lost.Serial > 0);
        Assert.True(Limited(rig, "not in the world"));
        rig.For(0.3);
        Assert.Empty(bob.Events);
        Assert.Empty(dan.Events);
        Assert.Empty(alice.Events);
        // Told once, and said back: the server keeps nothing more to say.
        Assert.Empty(alice.EventsLost);
        Assert.Empty(At(rig, alice).Lost);
    }

    [Fact]
    public void EventsArriveInTheOrderTheyWereSaid()
    {
        using Rig rig = new();
        Client alice = First(rig), bob = SaveTests.JoinAndLoad(rig, "Bob"), cas = SaveTests.JoinAndLoad(rig, "Cas");
        int weight = 0;
        for (uint i = 1; i <= 300; i++)
        {
            Assert.True(alice.SendEvent(i, Channel, new byte[40], to: i % 3 == 0 ? cas.Id : (byte)0));
            weight += ModEvents.Size(EventTests.Event(i, bytes: 40));
        }
        Assert.True(weight > ModEvents.BatchBytes, "more than one batch, or this proves nothing about batches");
        for (uint i = 1; i <= 100; i++) Assert.True(bob.SendEvent(i, "radio:morse/v1", [(byte)i], to: cas.Id));

        rig.Until(() => bob.Events.Count == 200 && cas.Events.Count == 400, seconds: 30, what: "everything to arrive");
        Assert.Equal(Enumerable.Range(1, 300).Where(i => i % 3 != 0).Select(i => (uint)i), bob.Events.Select(e => e.Id));
        // Each sender's own in its own order; nothing is promised between the two.
        Assert.Equal(Enumerable.Range(1, 300).Select(i => (uint)i), cas.Events.Where(e => e.From == alice.Id).Select(e => e.Id));
        Assert.Equal(Enumerable.Range(1, 100).Select(i => (uint)i), cas.Events.Where(e => e.From == bob.Id).Select(e => e.Id));
        Assert.Empty(alice.EventsLost);
        Assert.False(Limited(rig, "events"));
    }

    [Fact]
    public void EventsArriveInOrderOverALineThatLosesAFifthOfEverything()
    {
        using Rig rig = new();
        Client alice = First(rig), bob = SaveTests.JoinAndLoad(rig, "Bob");
        rig.Server.Socket.Loss = alice.Socket.Loss = bob.Socket.Loss = 0.2;
        for (uint i = 1; i <= 100; i++) Assert.True(alice.SendEvent(i, Channel, [(byte)i]));
        // A whole transfer sent again and a finished one said to have come again: neither is a second copy.
        rig.Until(() => bob.Events.Count >= 100, seconds: 60, what: "all hundred to arrive");
        rig.For(1.0);
        Assert.Equal(Enumerable.Range(1, 100).Select(i => (uint)i), bob.Events.Select(e => e.Id));
        Assert.Empty(alice.EventsLost);
        Assert.False(Limited(rig, "events"));
    }

    [Fact]
    public void AGameNotInTheWorldSaysNothingAndIsSentNothing()
    {
        using Rig rig = new();
        Client alice = First(rig);
        Client bob = rig.Join("Bob");
        rig.Until(() => bob.Blobs.Any(b => b.Kind == BlobKind.World), seconds: 20, what: "Bob to get the world");
        Assert.False(bob.SendEvent(1, Channel, [1]));
        Assert.Equal(0, bob.EventsWaiting);

        // By hand, round its own gate: the server takes it from nobody it does not count in the world.
        bob.Upload(Batch(EventTests.Event(1)));
        Assert.True(alice.SendEvent(1, Channel, [1]));
        rig.For(0.5);
        Assert.Empty(alice.Events);
        Assert.Empty(bob.Events);
        Assert.False(Limited(rig, "events"));

        bob.Blobs.Clear();
        bob.SayLoaded(5000);
        Assert.True(bob.SendEvent(2, Channel, [2]));
        rig.Until(() => alice.Events.Count == 1, what: "Alice to hear Bob once he is in");
        Assert.Equal(2u, alice.Events.Peek().Id);
    }

    [Fact]
    public void AGameSaysNoMoreASecondThanTheServerPassesOn()
    {
        using Rig rig = new();
        Client alice = First(rig), bob = SaveTests.JoinAndLoad(rig, "Bob");
        rig.For(0.2);
        // Four times what a server takes at once. A transfer said to have come says nothing of
        // having been taken, so a game that did not hold itself to the server's rate would lose these.
        for (uint i = 1; i <= 16; i++) Assert.True(alice.SendEvent(i, Channel, new byte[ModEvents.EventBytes]));
        double began = rig.Now();
        rig.Until(() => bob.Events.Count == 16, seconds: 30, what: "all sixteen to arrive");
        Assert.True(rig.Now() - began > 3.0, $"64 kB at 15 kB a second took {rig.Now() - began:F1} s");
        Assert.Equal(Enumerable.Range(1, 16).Select(i => (uint)i), bob.Events.Select(e => e.Id));
        Assert.Empty(alice.EventsLost);
        Assert.False(Limited(rig, "events"));
    }

    // Seven of a kilobyte: a batch as full as one gets, and four of them what a server takes at once.
    private static Blob Kilobytes(uint from) => Batch([.. Enumerable.Range(0, 7).Select(i => EventTests.Event(from + (uint)i, bytes: 1024))]);

    [Fact]
    public void AFloodOfEventsPastTheRateIsToldBackToWhoSaidIt()
    {
        using Rig rig = new();
        Client alice = First(rig), bob = SaveTests.JoinAndLoad(rig, "Bob");
        // Round its own pacing, as a game that means to would.
        for (uint b = 0; b < 4; b++) alice.Upload(Kilobytes(1 + (b * 7)));
        rig.Until(() => bob.Events.Count == 28, what: "what is inside the rate to arrive");
        alice.Upload(Kilobytes(29));
        alice.Upload(Kilobytes(36));
        rig.Until(() => alice.EventsLost.Count == 14, what: "Alice to be told of the rest");
        Assert.True(Limited(rig, "a second"));
        // Two transfers sent together come in either order, so which batch is told first is not fixed.
        Assert.Equal(Enumerable.Range(29, 14).Select(i => (uint)i), alice.EventsLost.Select(l => l.Id).Order());
        Assert.All(alice.EventsLost, l => Assert.Equal((Wire.EventLoss.Rate, (byte)0, ""), (l.Why, l.To, l.ToName)));
        Assert.Equal(28, bob.Events.Count);

        // And it fills again: this is a rate and not a count.
        rig.Skip(2.0);
        alice.Upload(Kilobytes(50));
        rig.Until(() => bob.Events.Count == 35, what: "the next batch to pass");
    }

    [Fact]
    public void ABatchThatIsNotOneIsDroppedWhole()
    {
        using Rig rig = new();
        Client alice = First(rig), bob = SaveTests.JoinAndLoad(rig, "Bob");
        alice.Upload(new Blob(BlobKind.ModEvent, "", 0, 0.0, new byte[ModEvents.BatchBytes + 1]));
        byte[] noise = new byte[900];
        new Random(4).NextBytes(noise);
        noise[0] = 1;
        alice.Upload(new Blob(BlobKind.ModEvent, "", 0, 0.0, noise));
        rig.Until(() => Limited(rig, "not one"), what: "the server to say so");
        rig.For(0.3);
        Assert.Empty(bob.Events);
        // There is no number in it to believe, so nobody is told of one.
        Assert.Empty(alice.EventsLost);

        // And it was not counted against her: four full batches still pass straight after.
        for (uint b = 0; b < 4; b++) alice.Upload(Kilobytes(1 + (b * 7)));
        rig.Until(() => bob.Events.Count == 28, what: "good batches to arrive");
        Assert.False(Limited(rig, "a second"));
    }

    [Fact]
    public void AGameWithinItsRateCannotFillEverybodysQueueWithLittleEvents()
    {
        // A dozen bytes as it is sent is several times that once the server has put a long name on
        // it. Counted as it came, a game inside its allowance put seven times it into every queue.
        using Rig rig = new();
        Client alice = First(rig), bob = SaveTests.JoinAndLoad(rig, "Bob");
        ModEvent[] tiny = [.. Enumerable.Range(1, 600).Select(i => new ModEvent(0, "", 0, (uint)i, "c", "", []))];
        Assert.True(1 + tiny.Sum(e => ModEvents.Size(e)) <= ModEvents.BatchBytes, "one batch as a game sends it");
        Assert.True(1 + tiny.Sum(e => ModEvents.Cost(e)) > ModEvents.BurstBytes, "and more than a server takes at once as it is passed on");
        alice.Upload(Batch(tiny));
        rig.Until(() => alice.EventsLost.Count > 0, what: "Alice to be told");
        Assert.True(Limited(rig, "a second"));
        rig.For(0.3);
        Assert.Empty(bob.Events);
    }

    [Fact]
    public void WhatAGameIsNotTakingIsHeldSoFarAndTheRestSaidLost()
    {
        using Rig rig = new();
        Client alice = First(rig), bob = SaveTests.JoinAndLoad(rig, "Bob"), cas = SaveTests.JoinAndLoad(rig, "Cas");
        List<Client> more = [.. Enumerable.Range(1, 10).Select(i => SaveTests.JoinAndLoad(rig, $"P{i}"))];
        rig.For(0.3);
        Stall(rig, bob);

        // Eleven games each saying what a server takes of one at once: more than is held for one game.
        // One after another, as a healthy game takes them: all at once would be more than is held
        // for Cas as well, in the one tick before any of it had gone.
        Client[] saying = [alice, .. more];
        for (int n = 0; n < saying.Length; n++)
        {
            for (uint b = 0; b < 4; b++) saying[n].Upload(Kilobytes(1 + (b * 7)));
            // One game that has stopped holds nobody else up.
            rig.Until(() => cas.Events.Count == (n + 1) * 28, seconds: 30, what: $"Cas to have what {n + 1} have said");
        }
        rig.Until(() => saying.Sum(c => c.EventsLost.Count) > 0, seconds: 10, what: "somebody to be told of what Bob's game had no room for");
        rig.For(0.5);

        GameServer.Player stalled = At(rig, bob);
        Assert.InRange(stalled.EventBytes, 1, ModEvents.WaitingBytes);
        Assert.True(Limited(rig, "not taking them"));
        Wire.EventLost[] told = [.. saying.SelectMany(c => c.EventsLost)];
        Assert.All(told, l => Assert.Equal((Wire.EventLoss.Full, bob.Id, "Bob"), (l.Why, l.To, l.ToName)));
        // Everything said for Bob is on its way to him, waiting for him, or told lost: none unaccounted.
        Assert.Equal(saying.Length * 28, stalled.EventsGoing.Count + stalled.Events.Count + told.Length);

        // When his game comes back it has what was held, each sender's in order with gaps and nothing out of place.
        rig.Clients.Add(bob);
        rig.Until(() => At(rig, bob).Events.Count == 0 && At(rig, bob).EventsGoing.Count == 0, seconds: 30, what: "Bob to be sent what was held");
        rig.For(0.3);
        foreach (Client c in saying)
        {
            uint[] got = [.. bob.Events.Where(e => e.From == c.Id).Select(e => e.Id)];
            Assert.Equal(got.Order(), got);
            Assert.Equal(28, got.Length + c.EventsLost.Count);
        }
    }

    [Fact]
    public void OneBatchAtATimeGoesToAGameHoweverManyAreSaying()
    {
        using Rig rig = new();
        Client alice = First(rig), bob = SaveTests.JoinAndLoad(rig, "Bob");
        Client[] saying = [alice, .. Enumerable.Range(1, 4).Select(i => SaveTests.JoinAndLoad(rig, $"P{i}"))];
        rig.For(1.0);
        foreach (Client c in saying)
        {
            for (uint i = 1; i <= 20; i++) Assert.True(c.SendEvent(i, Channel, new byte[200]));
        }
        // Bob sends nothing and everything before this has settled, so what is going on the
        // server's link to him is its batches for him and nothing else.
        double until = rig.Now() + 10.0;
        while (bob.Events.Count < 100 && rig.Now() < until)
        {
            rig.Tick();
            Assert.InRange(At(rig, bob).Link.Busy, 0, 1);
            Thread.Sleep(1);
        }
        Assert.Equal(100, bob.Events.Count);
        foreach (Client c in saying) Assert.Equal(Enumerable.Range(1, 20).Select(i => (uint)i), bob.Events.Where(e => e.From == c.Id).Select(e => e.Id));
    }

    [Fact]
    public void EventsDoNotWaitForAWorldOnItsWayUp()
    {
        using Rig rig = new();
        Client alice = First(rig), bob = SaveTests.JoinAndLoad(rig, "Bob");
        rig.For(0.3);
        // Two megabytes at 400 kB a second is five seconds of the link being busy.
        rig.Server.SnapshotNow();
        rig.Until(() => alice.SnapshotWanted is not null, what: "Alice to be asked for a save");
        alice.SnapshotWanted = null;
        alice.Upload(new Blob(BlobKind.World, "", alice.Id, 5000, Rig.FakeWorld(2000)));
        for (uint i = 1; i <= 10; i++) Assert.True(alice.SendEvent(i, Channel, [(byte)i]));
        rig.Until(() => bob.Events.Count == 10, seconds: 2.5, what: "the events to arrive while the world is still going up");
        Assert.True(alice.Sending, "the world should still be on its way");
        Assert.Equal(Enumerable.Range(1, 10).Select(i => (uint)i), bob.Events.Select(e => e.Id));
    }

    [Fact]
    public void APlayerWhoGoesTakesWhatWasWaitingForThemAndTheSenderIsTold()
    {
        using Rig rig = new();
        Client alice = First(rig), bob = SaveTests.JoinAndLoad(rig, "Bob"), cas = SaveTests.JoinAndLoad(rig, "Cas");
        rig.For(0.3);
        byte was = bob.Id;
        Stall(rig, bob);
        Assert.True(alice.SendEvent(1, Channel, [1]));
        rig.Until(() => At(rig, bob).EventGoing != 0, what: "the first to be on its way to Bob");
        Assert.True(alice.SendEvent(2, Channel, [2], to: was));
        Assert.True(alice.SendEvent(3, Channel, [3], to: was));
        rig.Until(() => At(rig, bob).Events.Count == 2, what: "two more to be waiting for Bob");

        bob.Dispose();
        rig.Until(() => alice.EventsLost.Count == 3, what: "Alice to be told of all three");
        Assert.Equal([1u, 2u, 3u], alice.EventsLost.Select(l => l.Id));
        Assert.All(alice.EventsLost, l => Assert.Equal((Wire.EventLoss.Left, was, "Bob"), (l.Why, l.To, l.ToName)));
        // The one for everybody still reached everybody else.
        Assert.Equal([1u], cas.Events.Select(e => e.Id));

        // Whoever is handed his number is not handed what was his.
        Client dan = SaveTests.JoinAndLoad(rig, "Dan");
        Assert.Equal(was, dan.Id);
        rig.For(0.5);
        Assert.Empty(dan.Events);
    }

    [Fact]
    public void WhatCameJustBeforeAPlayerWentIsNotSaidLost()
    {
        using Rig rig = new();
        Client alice = First(rig), bob = SaveTests.JoinAndLoad(rig, "Bob");
        Assert.True(alice.SendEvent(1, Channel, [1]));
        rig.Until(() => bob.Events.Count == 1, what: "Bob to hear it");
        rig.For(0.2);
        bob.Dispose();
        rig.Clients.Remove(bob);
        rig.For(0.5);
        Assert.Empty(alice.EventsLost);
    }

    [Fact]
    public void WhatAPlayerSaidAndHasNotGoneOutGoesWithThem()
    {
        using Rig rig = new();
        Client alice = First(rig), bob = SaveTests.JoinAndLoad(rig, "Bob");
        rig.For(0.3);
        byte was = alice.Id;
        Stall(rig, bob);
        Assert.True(alice.SendEvent(1, Channel, [1]));
        rig.Until(() => At(rig, bob).EventGoing != 0, what: "the first to be on its way to Bob");
        Assert.True(alice.SendEvent(2, Channel, [2]));
        rig.Until(() => At(rig, bob).Events.Count == 1, what: "the second to be waiting for Bob");

        alice.Dispose();
        rig.Clients.Remove(alice);
        rig.Until(() => rig.Server.Players.All(p => p.Name != "Alice"), what: "Alice to be gone");
        Assert.Empty(At(rig, bob).Events);
        // Ann is handed Alice's number, and nothing of Alice's is hers: neither said by her nor told lost to her.
        Client ann = SaveTests.JoinAndLoad(rig, "Ann");
        Assert.Equal(was, ann.Id);

        rig.Clients.Add(bob);
        rig.For(1.0);
        // What was already on the link still comes, under the name of whoever said it.
        Assert.All(bob.Events, e => Assert.Equal((1u, "Alice"), (e.Id, e.FromName)));
        Assert.InRange(bob.Events.Count, 0, 1);
        bob.Dispose();
        rig.Clients.Remove(bob);
        rig.For(0.5);
        Assert.Empty(ann.EventsLost);
    }

    [Fact]
    public void WhatWaitedInAGameForAPlayerWhoWentIsNotSentToWhoeverHasTheirNumber()
    {
        using Rig rig = new();
        Client alice = First(rig), bob = SaveTests.JoinAndLoad(rig, "Bob");
        rig.For(0.3);
        byte was = bob.Id;
        // Far more than goes in a second, so most of it is still in Alice's game when Bob goes.
        for (uint i = 1; i <= 40; i++) Assert.True(alice.SendEvent(i, Channel, new byte[ModEvents.EventBytes], to: was));
        rig.For(0.2);
        bob.Dispose();
        rig.Clients.Remove(bob);
        Client dan = SaveTests.JoinAndLoad(rig, "Dan");
        Assert.Equal(was, dan.Id);

        rig.Until(() => alice.EventsWaiting == 0, seconds: 10, what: "Alice's game to let go of what was for Bob");
        rig.For(1.0);
        Assert.Empty(dan.Events);
        // Every one is accounted for: Bob had it, or Alice is told he did not.
        Assert.All(alice.EventsLost, l => Assert.Equal("Bob", l.ToName));
        Assert.Contains(alice.EventsLost, l => l.Serial == 0 && l.Why == Wire.EventLoss.Left);
        Assert.Equal(40, alice.EventsLost.Select(l => l.Id).Distinct().Count() + bob.Events.Count);
    }

    [Fact]
    public void AWorldSentAgainDropsWhatWasSaidInTheOldOne()
    {
        using Rig rig = new();
        Client alice = First(rig), bob = SaveTests.JoinAndLoad(rig, "Bob");
        rig.For(0.3);
        Stall(rig, bob);
        Assert.True(alice.SendEvent(1, Channel, [1]));
        rig.Until(() => At(rig, bob).EventGoing != 0, what: "one to be on its way to Bob");
        for (uint i = 2; i <= 6; i++) Assert.True(alice.SendEvent(i, Channel, [(byte)i]));
        rig.Until(() => At(rig, bob).Events.Count == 5, what: "five to be waiting for Bob");
        Stall(rig, alice);
        for (uint i = 7; i <= 11; i++) Assert.True(alice.SendEvent(i, Channel, [(byte)i]));
        Assert.True(alice.EventsWaiting > 0);

        Assert.Equal(2, rig.Server.ReloadAll("test"));
        Assert.Empty(At(rig, bob).Events);
        rig.Clients.Add(alice);
        rig.Clients.Add(bob);
        rig.Until(() => alice.Blobs.Any(b => b.Kind == BlobKind.World) && bob.Blobs.Any(b => b.Kind == BlobKind.World), seconds: 20, what: "the world to come again");
        // Nothing said in the old world waits to be said in the new one, and nothing is until this game is in it.
        Assert.Equal(0, alice.EventsWaiting);
        Assert.False(alice.SendEvent(12, Channel, [12]));
        alice.Blobs.Clear();
        bob.Blobs.Clear();
        alice.SayLoaded(5000);
        bob.SayLoaded(5000);
        rig.Until(() => At(rig, alice).Loaded && At(rig, bob).Loaded, what: "both to load");
        rig.For(0.5);
        Assert.Empty(bob.Events);
        // Not told lost either: every game was loading, and none could act on it.
        Assert.Empty(alice.EventsLost);

        Assert.True(alice.SendEvent(13, Channel, [13]));
        rig.Until(() => bob.Events.Count == 1, what: "what is said in the new world to arrive");
        Assert.Equal(13u, bob.Events.Peek().Id);
    }

    [Fact]
    public void AnEventLostIsToldOnceHoweverOftenItIsSaid()
    {
        using Rig rig = new();
        Client alice = First(rig);
        SaveTests.JoinAndLoad(rig, "Bob");
        rig.For(0.3);
        rig.Server.Socket.Loss = alice.Socket.Loss = 0.3;
        // More than are said in one round, so the round has to move on as they are said back.
        for (uint i = 1; i <= 100; i++) Assert.True(alice.SendEvent(i, Channel, [(byte)i], to: 200));
        rig.Until(() => alice.EventsLost.Count >= 100 && At(rig, alice).Lost.Count == 0, seconds: 60, what: "all hundred to be told and said back");
        rig.For(1.0);
        Assert.Equal(Enumerable.Range(1, 100).Select(i => (uint)i), alice.EventsLost.Select(l => l.Id));
        Assert.False(Limited(rig, "lost events"));
    }

    [Fact]
    public void AnAckWithoutTheSessionsWordStopsNothing()
    {
        using Rig rig = new();
        Client alice = First(rig);
        rig.For(0.3);
        Assert.True(alice.SendEvent(1, Channel, [1], to: 200));
        rig.Until(() => At(rig, alice).Lost.Count == 1 || alice.EventsLost.Count == 1, what: "a loss to be waiting for Alice");
        if (alice.EventsLost.Count == 1) return;

        // From her own address, which anybody can write: without the word it says nothing back.
        Stall(rig, alice);
        alice.Send(new Wire.EventLost(uint.MaxValue, 0, 0, "", "", 0, "not the word"));
        for (int i = 0; i < 20; i++)
        {
            rig.Server.Tick();
            Thread.Sleep(5);
        }
        Assert.Single(At(rig, alice).Lost);
        rig.Clients.Add(alice);
        rig.Until(() => alice.EventsLost.Count == 1, what: "Alice to be told");
    }

    [Fact]
    public void AnEventNoGameEverTakesIsGivenUpOnAndItsSenderTold()
    {
        using Rig rig = new();
        Client alice = First(rig);
        // A game that goes on saying it is there and never says anything came.
        using System.Net.Sockets.UdpClient deaf = new(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        System.Net.IPEndPoint server = new(System.Net.IPAddress.Loopback, rig.Server.Port);
        void Say(Wire.Message m)
        {
            byte[] d = HardeningTests.Datagram(m);
            deaf.Send(d, d.Length, server);
        }
        Say(new Wire.Hello("Deaf", "B1", "Core", "SolLite", "", ""));
        Wire.Welcome cookie = HardeningTests.Heard(rig, deaf, 0.3).Select(d => Wire.Read(d, out _)).OfType<Wire.Welcome>().First();
        Say(new Wire.Hello("Deaf", "B1", "Core", "SolLite", "", "", cookie.Cookie));
        Wire.Welcome welcome = HardeningTests.Heard(rig, deaf, 0.3).Select(d => Wire.Read(d, out _)).OfType<Wire.Welcome>().First(w => w.Id != 0);
        Say(new Wire.Loaded(5000));
        rig.Until(() => rig.Server.Players.Any(p => p.Id == welcome.Id && p.Loaded), what: "the deaf game to be counted in");

        Assert.True(alice.SendEvent(5, Channel, [5]));
        rig.Until(() => rig.Server.Players.First(p => p.Id == welcome.Id).EventGoing != 0, what: "the event to be on its way to it");
        // Two minutes and more, five seconds at a time, the deaf game saying it is alive each time.
        for (int i = 0; i < 30 && alice.EventsLost.Count == 0; i++)
        {
            Say(new Wire.Alive());
            rig.Skip(5.0, real: 0.05);
            rig.Tick();
        }
        rig.Until(() => alice.EventsLost.Count == 1, what: "Alice to be told it was given up on");
        Wire.EventLost lost = alice.EventsLost.Peek();
        Assert.Equal((5u, welcome.Id, "Deaf", Wire.EventLoss.GaveUp), (lost.Id, lost.To, lost.ToName, lost.Why));
        Assert.Contains(rig.Server.Said, s => s.Contains("took nothing for two minutes"));
    }

    [Fact]
    public void AStrangerCannotSayAnEvent()
    {
        using Rig rig = new();
        Client alice = First(rig), bob = SaveTests.JoinAndLoad(rig, "Bob");
        using System.Net.Sockets.UdpClient stranger = new(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        byte[] blob = Batch(EventTests.Event(1)).Encode();
        byte[] d = HardeningTests.Datagram(new Wire.Chunk(1, 0, 1, blob));
        stranger.Send(d, d.Length, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, rig.Server.Port));
        rig.For(0.5);
        Assert.Empty(alice.Events);
        Assert.Empty(bob.Events);
    }
}
