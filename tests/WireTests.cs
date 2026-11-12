using System.Buffers.Binary;
using Xunit;

namespace KSAMultiplayer.Tests;

public class WireTests
{
    [Fact]
    public void AnOwnersPageWithinItsBudgetFitsTheDatagram()
    {
        // A key is clipped to a number of characters, and a character is up to three bytes: forty
        // names of 85 Han characters came to 10 KB in an 8 KB buffer, and the write threw at the
        // thirty-second entry.
        string wide = new('\u6f22', 85);
        Assert.Equal(255, System.Text.Encoding.UTF8.GetByteCount(wide));
        Assert.True(Wire.OwnerBytes(wide) * Wire.OwnersPerPage > Wire.BufferBytes,
            "a full page of the widest keys should not fit, which is what the budget is for");

        // However they are filled, a page within the budget fits.
        byte[] buffer = new byte[Wire.BufferBytes];
        foreach (string key in new[] { wide, new string('a', Wire.KeyChars), "R" })
        {
            int room = Wire.OwnersBudgetBytes / Wire.OwnerBytes(key);
            int many = Math.Min(room, Wire.OwnersPerPage);
            Wire.Owner[] entries = [.. Enumerable.Range(0, many).Select(i => new Wire.Owner($"{key}{i}", 1))];
            int n = Wire.Write(new Wire.Owners(0, 1, entries), buffer);
            Assert.InRange(n, 1, Wire.BufferBytes);
        }
    }

    [Fact]
    public void ACraftsNameSaysWhatItCameOff()
    {
        // The chain a staged rocket leaves, which is how a game tells whose debris it is holding.
        Assert.Equal(["Rocket_2", "Rocket"], Wire.ShedFrom("Rocket_2_5"));
        Assert.Equal(["Rocket_2_3", "Rocket_2", "Rocket"], Wire.ShedFrom("Rocket_2_3_15"));
        Assert.Equal(["NewRocket_2_10_2_3", "NewRocket_2_10_2", "NewRocket_2_10", "NewRocket_2", "NewRocket"],
            Wire.ShedFrom("NewRocket_2_10_2_3_1"));

        // A piece whose name was taken gets a " 2" instead, which is the same step.
        Assert.Equal(["NewRocket_3_27", "NewRocket_3", "NewRocket"], Wire.ShedFrom("NewRocket_3_27 2"));

        // Names out of the shared world, where a player names a craft in words. The walk stops at
        // the piece's own separator: carrying on through the spaces would hand back "LS Test MK",
        // which is nobody's craft, and a piece whose ancestry answers nothing is settled by distance
        // instead -- which after a crash is a measurement against nothing left alive.
        Assert.Equal(["LS Test MK I"], Wire.ShedFrom("LS Test MK I_1"));
        Assert.Equal(["NewRocket_2", "NewRocket"], Wire.ShedFrom("NewRocket_2_1"));

        // A piece shed off a craft this world had to rename. KSA builds the name out of the parent's
        // local Id, and the walk stops at the bracket, so the chain is that local name and nothing
        // before it -- while both the game's own roster and the server's are kept under the names
        // the room shares. That is why Sync reads every ancestor back through the renames before it
        // looks one up: without that the piece belonged to nobody, and a breakup renames nearly
        // everything.
        Assert.Equal(["Rocket_1 (2)"], Wire.ShedFrom("Rocket_1 (2)_1"));
        Assert.Equal(["Gemini7 (2)"], Wire.ShedFrom("Gemini7 (2)_3"));

        // A name that was never shed from anything came off nothing.
        Assert.Empty(Wire.ShedFrom("Rocket"));
        Assert.Empty(Wire.ShedFrom("Octo Smoke Monster"));
        Assert.Empty(Wire.ShedFrom(""));

        // Nothing is read off a name that is all separator or all digits: there is no craft left to
        // have shed it, and walking further would hand back the empty name as an owner to look up.
        Assert.Empty(Wire.ShedFrom("_1"));
        Assert.Empty(Wire.ShedFrom("Rocket_"));
        Assert.Empty(Wire.ShedFrom("Rocket 2b"));
    }

    private static T RoundTrip<T>(T message) where T : Wire.Message
    {
        byte[] buffer = new byte[Wire.BufferBytes];
        int n = Wire.Write(message, buffer);
        Wire.Message? read = Wire.Read(buffer.AsSpan(0, n), out ushort version);
        Assert.Equal(Wire.Version, version);
        return Assert.IsType<T>(read);
    }

    [Fact]
    public void EveryMessageComesBackAsItWent()
    {
        Assert.Equal(new Wire.Hello("Jeb", "2026.11.10.5801", "Core,KSAMultiplayer", "SolLite", "pw", "t0ken"),
                     RoundTrip(new Wire.Hello("Jeb", "2026.11.10.5801", "Core,KSAMultiplayer", "SolLite", "pw", "t0ken")));
        Assert.Equal(new Wire.Command("clear Rocket_*"), RoundTrip(new Wire.Command("clear Rocket_*")));
        Assert.Equal(new Wire.Op(true, "t0ken"), RoundTrip(new Wire.Op(true, "t0ken")));
        Assert.Equal(new Wire.Op(false, ""), RoundTrip(new Wire.Op(false, "")));
        Assert.Equal(new Wire.Welcome(3, "", "Moon base", true), RoundTrip(new Wire.Welcome(3, "", "Moon base", true)));
        Assert.Equal(new Wire.Welcome(0, "", "Moon base", true, "c00kie", "SolLite"), RoundTrip(new Wire.Welcome(0, "", "Moon base", true, "c00kie", "SolLite")));
        Assert.Equal(new Wire.Ask(), RoundTrip(new Wire.Ask()));
        Assert.Equal(new Wire.Facts("Moon base", "SolLite", true, "Core,KSAMultiplayer", 3, 16, Wire.WelcomeFlags.Password, "2026.11.10.5801"),
                     RoundTrip(new Wire.Facts("Moon base", "SolLite", true, "Core,KSAMultiplayer", 3, 16, Wire.WelcomeFlags.Password, "2026.11.10.5801")));
        Assert.Equal(new Wire.Bye("kicked"), RoundTrip(new Wire.Bye("kicked")));
        Assert.Equal(new Wire.Ping(12.5, 0.035f, 0f, 1f, true, ""), RoundTrip(new Wire.Ping(12.5, 0.035f, 0f, 1f, true, "")));
        // A game that has not measured a round trip yet says so rather than claiming none.
        Assert.Equal(new Wire.Ping(12.5, -1f, 0f, 1f, true, ""), RoundTrip(new Wire.Ping(12.5, -1f, 0f, 1f, true, "")));
        Assert.Equal(new Wire.Pong(1, 2), RoundTrip(new Wire.Pong(1, 2)));
        Assert.Equal(new Wire.Clock(1e6, 3.25, 1f), RoundTrip(new Wire.Clock(1e6, 3.25, 1f)));
        Assert.Equal(new Wire.Loaded(42), RoundTrip(new Wire.Loaded(42)));
        Assert.Equal(new Wire.Claim("Rocket", true, false), RoundTrip(new Wire.Claim("Rocket", true, false)));
        Assert.Equal(new Wire.Claim("Kitten", false, true), RoundTrip(new Wire.Claim("Kitten", false, true)));
        Assert.Equal(new Wire.Rename("Kitten", "Kitten 2"), RoundTrip(new Wire.Rename("Kitten", "Kitten 2")));
        Assert.Equal(new Wire.Gone("Rocket Debris"), RoundTrip(new Wire.Gone("Rocket Debris")));
        Assert.Equal(new Wire.Chat(2, "hello 🐱"), RoundTrip(new Wire.Chat(2, "hello 🐱")));
        Assert.Equal(new Wire.Snapshot("why"), RoundTrip(new Wire.Snapshot("why")));
        Assert.Equal(new Wire.Got(9), RoundTrip(new Wire.Got(9)));

        Wire.Roster roster = RoundTrip(new Wire.Roster([new Wire.Player(1, "A", true, "Rocket", 0.012f, 1f), new Wire.Player(2, "B", false, "", -1f, 1f)]));
        Assert.Equal([new Wire.Player(1, "A", true, "Rocket", 0.012f, 1f), new Wire.Player(2, "B", false, "", -1f, 1f)], roster.Players);

        Wire.Owners owners = RoundTrip(new Wire.Owners(1, 3, [new Wire.Owner("X", 1), new Wire.Owner("Y", 0)]));
        Assert.Equal((ushort)1, owners.Page);
        Assert.Equal([new Wire.Owner("X", 1), new Wire.Owner("Y", 0)], owners.Entries);

        Wire.Need need = RoundTrip(new Wire.Need(7, [1, 5, 9]));
        Assert.Equal([1, 5, 9], need.Missing);

        Wire.Chunk chunk = RoundTrip(new Wire.Chunk(4, 2, 10, [1, 2, 3]));
        Assert.Equal([1, 2, 3], chunk.Data);
    }

    [Fact]
    public void AStateKeepsItsPositionToTheMillimetreFarFromItsBody()
    {
        Wire.State s = new(1, "Rocket", 7, 1234.5, "Earth", new Vec3(6_371_000.123, -2.5, 1e7 + 0.001), new Vec3(1, 2, 3),
                           new Vec3(4.2e8, 1, 2), new Vec3(1000, 0, 7), new Quat(0, 0, 0.7071068, 0.7071068), new Vec3(0.1, 0.2, 0.3), 0.5f,
                           Wire.StateFlags.Landed | Wire.StateFlags.Controlled);
        Wire.State back = RoundTrip(s);
        Assert.Equal(s.Key, back.Key);
        Assert.Equal(s.Body, back.Body);
        Assert.Equal(s.Universe, back.Universe);
        Assert.Equal(s.Flags, back.Flags);
        Assert.True((back.PositionCcf - s.PositionCcf).Length < 1e-3);
        Assert.True((back.PositionCci - s.PositionCci).Length < 1e-3);
        Assert.True((back.VelocityCci - s.VelocityCci).Length < 1e-3);
        Assert.Equal(1.0, back.Body2Ccf.LengthSquared, 6);
    }

    [Fact]
    public void AnythingElseIsDropped()
    {
        Assert.Null(Wire.Read("hello world"u8, out _));
        Assert.Null(Wire.Read("FPR!\u0002\u0000\u0004"u8, out _));

        byte[] buffer = new byte[Wire.BufferBytes];
        int n = Wire.Write(new Wire.Ping(1, -1f, 0f, 1f, true, ""), buffer);
        buffer[4] = 99;
        Assert.Null(Wire.Read(buffer.AsSpan(0, n), out ushort version));
        Assert.Equal(99, version);

        // Cut short anywhere, a datagram is dropped rather than read wrong.
        n = Wire.Write(new Wire.Hello("Jeb", "b", "m", "s", "p", "t"), buffer);
        for (int k = 7; k < n; k++) Assert.Null(Wire.Read(buffer.AsSpan(0, k), out _));
    }

    [Fact]
    public void WhatAListOfServersAsksAndIsToldIsReadInAnyVersion()
    {
        // Every other datagram of another version is dropped, which is what emptied every directory
        // at every release: a list was read through a hello and a welcome, and neither had changed.
        byte[] buffer = new byte[Wire.BufferBytes];
        int n = Wire.Write(new Wire.Facts("Moon base", "SolLite", true, "Core", 3, 16, Wire.WelcomeFlags.None, "B1"), buffer);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), 99);
        Wire.Facts told = Assert.IsType<Wire.Facts>(Wire.Read(buffer.AsSpan(0, n), out ushort version));
        Assert.Equal(99, version);
        // Which version said it is the stamp, and is the one thing a reader of another needs.
        Assert.Equal(new Wire.Facts("Moon base", "SolLite", true, "Core", 3, 16, Wire.WelcomeFlags.None, "B1", 99), told);

        // A field added after the ones a reader knows is not a new form of the answer.
        buffer[n] = 7;
        Assert.Equal(told, Wire.Read(buffer.AsSpan(0, n + 1), out _));

        // A form of the answer this cannot read is a server that answered, and no more than that.
        buffer[7] = (byte)(Wire.ProbeVersion + 1);
        Assert.Null(Wire.Read(buffer.AsSpan(0, n), out version));
        Assert.Equal(99, version);

        n = Wire.Write(new Wire.Ask(), buffer);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), 7);
        Assert.Equal(new Wire.Ask(), Wire.Read(buffer.AsSpan(0, n), out version));
        Assert.Equal(7, version);
    }

    [Fact]
    public void AnAskIsAsLongAsTheMostItIsAnsweredWithAndCarriesNothingOfTheLastDatagram()
    {
        byte[] buffer = new byte[Wire.BufferBytes];
        Array.Fill(buffer, (byte)0xAB);
        int n = Wire.Write(new Wire.Ask(), buffer);
        Assert.Equal(Wire.AskBytes, n);
        // The buffer is the sender's own, written over for every datagram it sends.
        Assert.All(buffer[8..n], b => Assert.Equal(0, b));

        // The longest answer a mod list in ASCII makes, with room to spare.
        string wide = new('m', Wire.ProbeModsChars);
        Assert.InRange(Wire.Write(new Wire.Facts(new string('n', 64), new string('s', 24), true, wide, 255, 255, Wire.WelcomeFlags.Password, new string('b', 64)), buffer),
                       1, Wire.AskBytes);
    }

    [Fact]
    public void AStateWithANanInItIsDropped()
    {
        Wire.State s = new(1, "R", 1, 1, "Earth", new Vec3(double.NaN, 0, 0), Vec3.Zero, Vec3.Zero, Vec3.Zero, Quat.Identity, Vec3.Zero, 0, 0);
        byte[] buffer = new byte[Wire.BufferBytes];
        int n = Wire.Write(s, buffer);
        Assert.Null(Wire.Read(buffer.AsSpan(0, n), out _));
    }

    [Fact]
    public void ABlobSaysWhatItIs()
    {
        Blob b = new(BlobKind.Vehicle, "Rocket", 3, 99.5, [1, 2, 3, 4]);
        Blob back = Assert.IsType<Blob>(Blob.Decode(b.Encode()));
        Assert.Equal(b.Kind, back.Kind);
        Assert.Equal(b.Key, back.Key);
        Assert.Equal(b.Owner, back.Owner);
        Assert.Equal(b.Universe, back.Universe);
        Assert.Equal(b.Data, back.Data);
        Assert.Null(Blob.Decode([0, 1, 2]));
    }

    [Fact]
    public void AZipThatClimbsOutOfItsFolderIsRefused()
    {
        Assert.False(Blob.SafeName("../evil"));
        Assert.False(Blob.SafeName("/etc/passwd"));
        Assert.False(Blob.SafeName("a\\b"));
        Assert.False(Blob.SafeName("C:x"));
        Assert.True(Blob.SafeName("vehicles/Rocket/vehicle.xml"));
        byte[] zip = Blob.Zip([("../x", [1])]);
        Assert.Throws<InvalidDataException>(() => Blob.Unzip(zip));
    }
}
