using KSAMultiplayer.Server;
using Xunit;

namespace KSAMultiplayer.Tests;

public class SettingsTests
{
    private static readonly Dictionary<string, string> None = [];

    private static string? NoEnvironment(string _) => null;

    [Fact]
    public void TheFileAServerWritesReadsBackAsEveryDefault()
    {
        Dictionary<string, string> read = Settings.Read(Settings.Template());
        foreach (Settings.Setting s in Settings.All) Assert.Equal(s.Default, read[s.Key]);
        (ServerOptions o, var bind, string data) = Settings.Options(Settings.Resolve(None, NoEnvironment, read));
        Assert.Equal(new ServerOptions(), o);
        Assert.Null(bind);
        Assert.Equal("data", data);
    }

    [Fact]
    public void AFlagWinsOverTheEnvironmentWhichWinsOverTheFile()
    {
        Dictionary<string, string> file = new() { ["port"] = "1", ["name"] = "file", ["password"] = "file" };
        Dictionary<string, string> environment = new() { ["KSAMP_PORT"] = "2", ["KSAMP_NAME"] = "env" };
        Dictionary<string, string> flags = new() { ["port"] = "3" };

        var resolved = Settings.Resolve(flags, k => environment.GetValueOrDefault(k), file);

        Assert.Equal(("3", "--port"), resolved["port"]);
        Assert.Equal(("env", "KSAMP_NAME"), resolved["name"]);
        Assert.Equal(("file", "the settings file"), resolved["password"]);
        Assert.Equal(("16", "its default"), resolved["max-players"]);
    }

    [Fact]
    public void SettingALineChangesItInPlaceAndLeavesTheRest()
    {
        string text = "# mine\r\nname = \"old\" # shown\r\n\r\n# the port\r\nport = 50100\r\n";
        string changed = Settings.Write(text, Settings.Find("port")!, "50200");
        Assert.Equal("# mine\r\nname = \"old\" # shown\r\n\r\n# the port\r\nport = 50200\r\n", changed);

        // A setting with no line yet is added, and a commented-out one is not mistaken for it.
        string added = Settings.Write("# speed = 9\n", Settings.Find("speed")!, "2");
        Assert.Equal("2", Settings.Read(added)["speed"]);
        Assert.StartsWith("# speed = 9", added);
    }

    [Fact]
    public void AnyStringComesBackAsItWent()
    {
        Settings.Setting name = Settings.Find("name")!;
        foreach (string s in (string[])["", "Bob's \"place\"", "back\\slash", "two\nlines", "tab\there", "ünïcødé 🚀", "# not a comment"])
        {
            Assert.Equal(s, Settings.Read(Settings.Write("", name, s))["name"]);
        }
    }

    [Fact]
    public void TomlAsAnOperatorMightWriteItIsRead()
    {
        Dictionary<string, string> read = Settings.Read("""
            name = 'literal \no escapes'   # a comment
            port = 50_100
            keep-time = true
            speed = 2.5
            """);
        Assert.Equal(@"literal \no escapes", read["name"]);
        Assert.Equal("50100", read["port"]);
        (ServerOptions o, _, _) = Settings.Options(Settings.Resolve(None, NoEnvironment, read));
        Assert.Equal(50100, o.Port);
        Assert.True(o.KeepTime);
        Assert.Equal(2.5, o.Speed);
    }

    [Theory]
    [InlineData("[server]\nport = 1", "tables")]
    [InlineData("colour = \"red\"", "no setting called 'colour'")]
    [InlineData("name = \"unended", "does not end")]
    [InlineData("name = \"a\" b", "after the value")]
    [InlineData("port", "not 'key = value'")]
    public void AFileThatIsNotSettingsSaysWhichLine(string text, string says)
    {
        FormatException e = Assert.Throws<FormatException>(() => Settings.Read(text));
        Assert.Contains("line 1", e.Message);
        Assert.Contains(says, e.Message);
    }

    [Fact]
    public void AnIdIsOneALinkCanCarry()
    {
        (ServerOptions unsaid, _, _) = Settings.Options(Settings.Resolve(None, NoEnvironment, new Dictionary<string, string>()));
        Assert.Equal("", unsaid.Id);
        (ServerOptions o, _, _) = Settings.Options(Settings.Resolve(None, NoEnvironment, new Dictionary<string, string> { ["id"] = " my-clan.cars " }));
        Assert.Equal("my-clan.cars", o.Id);

        // Said at start, to the operator. A game would have to drop an id it cannot put in a link,
        // and the server would be known by its address without anybody having been told.
        foreach (string bad in new[] { "my cars", "-cars", "cars&x=1", new string('a', Wire.IdChars + 1) })
        {
            FormatException e = Assert.Throws<FormatException>(() =>
                Settings.Options(Settings.Resolve(None, NoEnvironment, new Dictionary<string, string> { ["id"] = bad })));
            Assert.Contains("id from", e.Message);
        }
    }

    [Fact]
    public void AnEmptyWorldIsOneTheServerCarries()
    {
        Dictionary<string, string> file = new() { ["empty-world"] = "SolLite" };
        (ServerOptions o, _, _) = Settings.Options(Settings.Resolve(None, NoEnvironment, file));
        Assert.Equal("SolLite", o.EmptyWorld);

        // And a system there is no world for is refused, since the only thing the server could do
        // with it is nothing: it would wait for a player while saying it would not.
        FormatException e = Assert.Throws<FormatException>(() =>
            Settings.Options(Settings.Resolve(None, NoEnvironment, new Dictionary<string, string> { ["empty-world"] = "Mars" })));
        Assert.Contains("Mars", e.Message);
        Assert.Contains("SolLite", e.Message);
    }

    /// <summary>
    /// Said rather than silently taken, because Write changes the first line with a key on it and
    /// Read used to keep the last: set wrote a value the server then ignored, and said nothing.
    /// </summary>
    [Fact]
    public void AKeySetTwiceIsRefused()
    {
        FormatException e = Assert.Throws<FormatException>(() => Settings.Read("port = 1\nname = \"a\"\nport = 2\n"));
        Assert.Contains("line 3", e.Message);
        Assert.Contains("'port' is set twice", e.Message);
    }

    [Fact]
    public void AValueOfTheWrongKindNamesTheSettingAndWhereItCameFrom()
    {
        FormatException e = Assert.Throws<FormatException>(() =>
            Settings.Options(Settings.Resolve(None, k => k == "KSAMP_SPEED" ? "fast" : null, None)));
        Assert.Contains("speed", e.Message);
        Assert.Contains("KSAMP_SPEED", e.Message);
        Assert.Throws<FormatException>(() => Settings.Options(Settings.Resolve(new Dictionary<string, string> { ["port"] = "70000" }, NoEnvironment, None)));
        Assert.Throws<FormatException>(() => Settings.Options(Settings.Resolve(new Dictionary<string, string> { ["bind"] = "not an address" }, NoEnvironment, None)));
    }

    [Fact]
    public void ARunningServerTakesUpANewPasswordAndKeepsItsPort()
    {
        using Rig rig = new();
        int port = rig.Server.Port;
        rig.Server.Reconfigure(rig.Server.Options with { Password = "hunter2", Port = 1 });
        Assert.Equal("hunter2", rig.Server.Options.Password);
        Assert.Equal(port, rig.Server.Port);

        Client wrong = rig.Join("Mallory");
        rig.Until(() => wrong.Out.Length > 0, what: "a join without the new password to be refused");
        Assert.Contains("wrong password", wrong.Out);

        Client right = rig.Join("Alice", password: "hunter2");
        rig.Until(() => right.Joined, what: "a join with the new password");
    }
}
