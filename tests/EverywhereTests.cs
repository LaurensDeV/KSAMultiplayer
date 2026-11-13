using KSAMultiplayer.Directories;
using Xunit;

namespace KSAMultiplayer.Tests;

/// <summary>
/// What every directory lists, made into the one document a page reads: a row for each address
/// however many directories carry it, and nothing a stranger's directory says taken on trust.
/// </summary>
public class EverywhereTests
{
    private static Listing From(string name, params Listed[] servers) => new(Listings.Version, name, servers);

    [Fact]
    public void AServerSeveralDirectoriesCarryIsOneRowThatNamesThemAll()
    {
        Everything all = Everywhere.Merge(
        [
            From("ours", new Listed("cars.example.org", 50101, "Cars", Players: 2, Seen: 100)),
            From("theirs", new Listed("CARS.example.org", 50101, "Cars", Players: 3, Seen: 160), new Listed("other.example.org", 50100, "Other")),
        ]);

        Assert.Equal(["ours", "theirs"], all.Directories);
        Assert.Equal(2, all.Servers.Length);
        Carried cars = all.Servers[0];
        Assert.Equal(["ours", "theirs"], cars.By);
        // The more recently seen of the two accounts, since neither is this reader's own.
        Assert.Equal(3, cars.Listed.Players);
        Assert.Equal(["theirs"], all.Servers[1].By);
    }

    [Fact]
    public void ADirectoryThatDidNotAnswerIsLeftOutAndTheRestAreKept()
    {
        Everything all = Everywhere.Merge([From("ours"), null, From("theirs", new Listed("a.example.org", 50100))]);
        Assert.Equal(["ours", "theirs"], all.Directories);
        Assert.Single(all.Servers);
    }

    [Fact]
    public void OneDirectoryUnderTwoAddressesIsNamedOnce()
    {
        // The list of directories names this one at both of its addresses, and it is asked at each.
        Listed same = new("a.example.org", 50100, Seen: 5);
        Everything all = Everywhere.Merge([From("ours", same), From("ours", same), From("ours", same)]);
        Assert.Equal(["ours"], all.Directories);
        Assert.Equal(["ours"], Assert.Single(all.Servers).By);
    }

    [Fact]
    public void TheRowsWorthJoiningComeFirst()
    {
        Everything all = Everywhere.Merge(
        [
            From("d", new Listed("b.example.org", 50100, "B"), new Listed("c.example.org", 50100, "C", Players: 4), new Listed("a.example.org", 50100, "A")),
        ]);
        Assert.Equal(["C", "A", "B"], all.Servers.Select(s => s.Listed.Name));
    }

    [Fact]
    public void WhatAStrangersDirectorySaysIsCutToSizeAndAnIdThatIsNoneIsNotPassedOn()
    {
        Everything all = Everywhere.Merge(
        [
            From("d",
                new Listed("a.example.org", 50100, new string('n', 5000), Mods: new string('m', 9000), Id: "x&mods=Evil@6.6.6"),
                new Listed("b.example.org", 50100, "B", Id: "mpcars"),
                new Listed("", 50100, "nowhere"),
                new Listed("c.example.org", 70000, "no such port")),
        ]);
        Assert.Equal(2, all.Servers.Length);
        Listed cut = all.Servers.Single(s => s.Listed.Host == "a.example.org").Listed;
        Assert.Equal(64, cut.Name.Length);
        Assert.Equal(2048, cut.Mods.Length);
        Assert.Equal("", cut.Id);
        Assert.Equal("mpcars", all.Servers.Single(s => s.Listed.Host == "b.example.org").Listed.Id);
    }

    [Fact]
    public void ADocumentIsNeverMoreRowsThanAPageShouldShow()
    {
        Listed[] many = [.. Enumerable.Range(1, Everywhere.MostServers + 50).Select(i => new Listed($"s{i}.example.org", 50100))];
        Assert.Equal(Everywhere.MostServers, Everywhere.Merge([From("d", many)]).Servers.Length);
    }

    [Fact]
    public void ADirectoryListsAServerUnderWhatItsOperatorCallsIt()
    {
        Roster roster = new();
        roster.Announce("", 50101, System.Net.IPAddress.Parse("45.133.178.13"), 0.0);
        roster.Heard(Roster.Key("45.133.178.13", 50101),
                     new ServerFacts("Cars", "SolLite", true, "Core,KSAMultiplayer", 0, 16, false, "B1", LinkId: "mpcars"), 0.0);
        Assert.Equal("mpcars", Assert.Single(roster.Snapshot("d", 0.0, DateTimeOffset.UnixEpoch).Servers).Id);
    }
}
