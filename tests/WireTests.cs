using Xunit;

namespace KSAMultiplayer.Tests;

public class WireTests
{
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
        Assert.Equal(new Wire.Hello("Jeb", "2026.10.25.5707", "Core,KSAMultiplayer", "SolLite", "pw"), RoundTrip(new Wire.Hello("Jeb", "2026.10.25.5707", "Core,KSAMultiplayer", "SolLite", "pw")));
        Assert.Equal(new Wire.Welcome(3, "", "Moon base", true), RoundTrip(new Wire.Welcome(3, "", "Moon base", true)));
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
        n = Wire.Write(new Wire.Hello("Jeb", "b", "m", "s", "p"), buffer);
        for (int k = 7; k < n; k++) Assert.Null(Wire.Read(buffer.AsSpan(0, k), out _));
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
