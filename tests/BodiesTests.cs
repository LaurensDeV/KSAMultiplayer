using KSAMultiplayer.Server;
using Xunit;

namespace KSAMultiplayer.Tests;

public class BodiesTests
{
    private static readonly Quat Turned = new(0, 0, Math.Sin(0.2), Math.Cos(0.2));

    public static SystemDescription Sol(string system = "SolLite", string build = "B1", double earthRadius = 6_371_000) => new(system, build,
    [
        new("Sol", "", "star", 696_000_000, 1.327e20, 0, 0, Vec3.Zero, null, Quat.Identity, 0, 0),
        new("Earth", "Sol", "planet", earthRadius, 3.986e14, 100_000, 9.2e8, null,
            new BodyOrbit(1.471e11, 0.0167, -1.2e6, 3.156e7, 1.327e20, Turned), Turned, 7.292e-5, 1.1),
        new("Luna", "Earth", "moon", 1_737_000, 4.9e12, 0, 6.6e7, null,
            new BodyOrbit(3.63e8, 0.055, 2.0e5, 2.36e6, 4.035e14, Quat.Identity), Quat.Identity, 2.66e-6, 0.3),
    ]);

    [Fact]
    public void ADescriptionComesBackAsItWent()
    {
        SystemDescription sol = Sol();
        SystemDescription back = SystemDescription.Decode(sol.Encode(), out string why)!;
        Assert.Equal("", why);
        Assert.Equal(sol.Bodies.Length, back.Bodies.Length);
        Assert.Equal(sol.Bodies[1], back.Bodies[1]);
    }

    public static TheoryData<string, SystemDescription> Bad()
    {
        SystemDescription sol = Sol();
        BodyDescription earth = sol.Bodies[1], luna = sol.Bodies[2];
        SystemDescription With(params BodyDescription[] bodies) => sol with { Bodies = bodies };
        return new TheoryData<string, SystemDescription>
        {
            { "not described", With(sol.Bodies[0], luna) },
            { "own ancestor", With(sol.Bodies[0], earth with { Parent = "Luna" }, luna) },
            { "rotation", With(sol.Bodies[0], earth with { Cci2Cce = new Quat(0, 0, 0, 2) }, luna) },
            { "radius", With(sol.Bodies[0], earth with { Radius = 0 }, luna) },
            { "orbit that is not one", With(sol.Bodies[0], earth with { Orbit = earth.Orbit! with { Mu = -1 } }, luna) },
            { "no orbit", With(sol.Bodies[0], earth with { Orbit = null }, luna) },
            { "twice", With(sol.Bodies[0], earth, earth, luna) },
            { "bodies", With() },
            { "bodies", With([.. Enumerable.Range(0, SystemDescription.MostBodies + 1).Select(i => sol.Bodies[0] with { Id = $"S{i}" })]) },
        };
    }

    [Theory]
    [MemberData(nameof(Bad))]
    public void ADescriptionThatIsNotASystemIsRefused(string says, SystemDescription bad)
    {
        Assert.Null(SystemDescription.Decode(bad.Encode(), out string why));
        Assert.Contains(says, why);
    }

    [Fact]
    public void ANumberThatIsNotFiniteIsRefused()
    {
        byte[] json = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(Sol().Encode()).Replace("7.292E-05", "\"NaN\"").Replace("7.292e-05", "\"NaN\""));
        Assert.Null(SystemDescription.Decode(json, out _));
        Assert.Null(SystemDescription.Decode("{\"bodies\":"u8.ToArray(), out string why));
        Assert.StartsWith("not a description", why);
    }

    private static Client Seed(Rig rig, string name = "Alice")
    {
        Client a = rig.Join(name);
        rig.Until(() => a.Joined && a.SnapshotWanted is not null, what: "the server to ask for a world");
        a.SnapshotWanted = null;
        a.Upload(new Blob(BlobKind.World, "", a.Id, 5000, Rig.FakeWorld()));
        rig.Until(() => rig.Server.HasWorld && rig.Server.Players.First(p => p.Id == a.Id).Loaded, what: "the world to arrive");
        return a;
    }

    [Fact]
    public void TheFirstGoodDescriptionIsKeptAndOutlivesARestart()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ksamp-bodies-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (Rig rig = new(store: new WorldStore(folder)))
            {
                rig.Server.WantBodies = true;
                Client a = Seed(rig);
                rig.Until(() => a.DescribeWanted, what: "Alice to be asked for the system");
                a.Upload(new Blob(BlobKind.Bodies, "", a.Id, 0, Sol().Encode()));
                rig.Until(() => rig.Server.Bodies is not null, what: "the description to be kept");
                string tag = rig.Server.Bodies!.Value.Etag;

                // Sent unasked, and otherwise: not taken, and nothing changes.
                a.Upload(new Blob(BlobKind.Bodies, "", a.Id, 0, Sol(earthRadius: 1).Encode()));
                rig.For(0.5);
                Assert.Equal(tag, rig.Server.Bodies!.Value.Etag);
                Assert.Contains(rig.Server.Said, s => s.Contains("nobody asked for"));
            }
            using (Rig again = new(store: new WorldStore(folder)))
            {
                Assert.NotNull(again.Server.Bodies);
                Assert.Equal(6_371_000, SystemDescription.Decode(again.Server.Bodies!.Value.Json, out _)!.Bodies[1].Radius);
            }
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void ADescriptionOfAnotherSystemOrBuildIsRefused()
    {
        using Rig rig = new();
        rig.Server.WantBodies = true;
        Client a = Seed(rig);
        rig.Until(() => a.DescribeWanted, what: "Alice to be asked for the system");
        a.Upload(new Blob(BlobKind.Bodies, "", a.Id, 0, Sol(system: "Sol").Encode()));
        rig.Until(() => rig.Server.Said.Any(s => s.Contains("refused")), what: "the description to be refused");
        Assert.Null(rig.Server.Bodies);
    }

    [Fact]
    public void NobodyIsAskedWhileThereIsNoViewer()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        rig.For(0.5);
        Assert.False(a.DescribeWanted);
        rig.Server.AskForBodiesNow();
        rig.For(0.3);
        Assert.False(a.DescribeWanted);
    }
}
