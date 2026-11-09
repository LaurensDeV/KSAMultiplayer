using System.Net;
using KSAMultiplayer.Directories;
using Xunit;

namespace KSAMultiplayer.Tests;

/// <summary>The rules a directory keeps: what it will list, what it will not, and when it stops.</summary>
public class RosterTests
{
    private static readonly IPAddress Somewhere = IPAddress.Parse("45.133.178.13");
    private static readonly IPAddress Elsewhere = IPAddress.Parse("203.0.112.9");

    private static ServerFacts Said(int players = 0, int max = 16, string name = "A server") =>
        new(name, "SolLite", true, "Core", players, max, false, "B1");

    [Theory]
    // Addresses nobody on the internet can reach: the commonest thing a server list carries
    // wrongly, because nothing about announcing one fails.
    [InlineData("127.0.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("10.4.1.9", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.254", false)]
    [InlineData("192.168.1.50", false)]
    [InlineData("169.254.3.4", false)]
    [InlineData("100.64.0.1", false)]       // carrier-grade NAT, where a home server cannot be reached
    [InlineData("100.127.255.255", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("192.0.2.1", false)]        // the documentation ranges
    [InlineData("198.51.100.7", false)]
    [InlineData("203.0.113.7", false)]
    [InlineData("198.18.0.1", false)]
    // And ones they can, including the edges of the ranges above.
    [InlineData("45.133.178.13", true)]
    [InlineData("172.15.0.1", true)]
    [InlineData("172.32.0.1", true)]
    [InlineData("100.63.255.255", true)]
    [InlineData("100.128.0.1", true)]
    [InlineData("192.167.1.1", true)]
    [InlineData("203.0.112.9", true)]
    [InlineData("8.8.8.8", true)]
    public void OnlyAnAddressPlayersCanReachIsListed(string address, bool reachable) =>
        Assert.Equal(reachable, Roster.Reachable(IPAddress.Parse(address)));

    [Fact]
    public void AnAnnouncementFromAnUnreachableAddressIsRefusedInWordsItsOperatorCanActOn()
    {
        Roster roster = new();
        Noted noted = roster.Announce("", Wire.DefaultPort, IPAddress.Parse("100.72.3.4"), 0.0);

        Assert.False(noted.Ok);
        Assert.False(noted.Listed);
        // Not "not listed" alone, which sends somebody to check a firewall that is fine.
        Assert.Contains("carrier-grade NAT", noted.Say);
        Assert.Equal("100.72.3.4", noted.Seen);
    }

    [Fact]
    public void AServerOnTheDirectorysOwnMachineIsHeldToTheNameItGives()
    {
        Roster roster = new();
        // A server sharing a machine with a directory never gets an address of its own: the host
        // turns its announcement back, so what arrives is the bridge it left by. With a name to be
        // listed under that is enough, because an address no player can reach is one only this
        // machine could have sent -- a stranger's announcement arrives from a public address.
        IPAddress bridge = IPAddress.Parse("172.19.0.1");
        Noted named = roster.Announce("mp.example.com", 50100, bridge, 0.0);
        Assert.True(named.Ok);
        Assert.False(named.Listed);
        Assert.Equal("172.19.0.1", named.Seen);
        Assert.Equal((1, 0), roster.Count(0.0));

        // With no name there is nothing to go on, and the refusal says which of the two cases this
        // is rather than leaving an operator to guess.
        Noted anonymous = roster.Announce("", 50101, bridge, 0.0);
        Assert.False(anonymous.Ok);
        Assert.Contains("say which name to list it under", anonymous.Say);

        // It is still listed only once a probe of the name has answered.
        roster.Heard(Roster.Key("mp.example.com", 50100), Said(players: 2), 1.0);
        Listed row = Assert.Single(roster.Snapshot("d", 1.0, DateTimeOffset.UnixEpoch).Servers);
        Assert.Equal("mp.example.com", row.Host);
        Assert.Equal(2, row.Players);
    }

    [Fact]
    public void NothingIsListedUntilAProbeOfItHasAnswered()
    {
        Roster roster = new();
        Noted noted = roster.Announce("", 50100, Somewhere, 0.0);
        Assert.True(noted.Ok);
        // Announcing is a claim, and a claim is not evidence.
        Assert.False(noted.Listed);
        Assert.Empty(roster.Snapshot("d", 0.0, DateTimeOffset.UnixEpoch).Servers);

        roster.Heard(Roster.Key("45.133.178.13", 50100), Said(players: 3), 1.0);
        Listed[] listed = roster.Snapshot("d", 1.0, DateTimeOffset.UnixEpoch).Servers;
        Assert.Single(listed);
        Assert.Equal("45.133.178.13", listed[0].Host);
        Assert.Equal(3, listed[0].Players);
        Assert.Equal("A server", listed[0].Name);
        Assert.Equal(Wire.Version, listed[0].Protocol);
    }

    [Fact]
    public void WhatIsListedIsWhatWasProbedAndNotWhatWasAnnounced()
    {
        Roster roster = new();
        roster.Announce("", 50100, Somewhere, 0.0);
        string key = Roster.Key("45.133.178.13", 50100);

        // An announcement carries an address and a port and nothing else. Every other field on the
        // row comes from the answer, so there is nothing for a server to overstate.
        roster.Heard(key, Said(players: 11, max: 12, name: "Honest"), 1.0);
        Listed row = Assert.Single(roster.Snapshot("d", 1.0, DateTimeOffset.UnixEpoch).Servers);
        Assert.Equal("Honest", row.Name);
        Assert.Equal(11, row.Players);
        Assert.Equal(12, row.Max);
        Assert.Equal("SolLite", row.System);
        Assert.Equal("B1", row.Ksa);
    }

    [Fact]
    public void AServerThatStopsAnsweringLeavesTheList()
    {
        Roster roster = new();
        roster.Announce("", 50100, Somewhere, 0.0);
        string key = Roster.Key("45.133.178.13", 50100);
        roster.Heard(key, Said(), 0.0);

        // Still answering and still announcing.
        roster.Announce("", 50100, Somewhere, 120.0);
        roster.Heard(key, Said(), 120.0);
        Assert.Single(roster.Snapshot("d", 150.0, DateTimeOffset.UnixEpoch).Servers);

        // Announcing, but the probes have stopped being answered: the process is gone and something
        // is still saying it is there. Expiry is a comparison at read time, so this needs no sweep.
        roster.Announce("", 50100, Somewhere, 300.0);
        Assert.Empty(roster.Snapshot("d", 120.0 + Roster.ListedForSeconds + 1.0, DateTimeOffset.UnixEpoch).Servers);
    }

    [Fact]
    public void AServerThatStopsAnnouncingItselfLeavesTheList()
    {
        Roster roster = new();
        roster.Announce("", 50100, Somewhere, 0.0);
        string key = Roster.Key("45.133.178.13", 50100);

        // Answering all along, but it has stopped asking to be listed -- an operator who turned
        // advertising off and whose withdrawal was lost.
        for (double t = 0.0; t < Roster.AnnouncedWithinSeconds + 60.0; t += 30.0) roster.Heard(key, Said(), t);
        Assert.Empty(roster.Snapshot("d", Roster.AnnouncedWithinSeconds + 60.0, DateTimeOffset.UnixEpoch).Servers);
    }

    [Fact]
    public void OnlyTheAddressThatPutAServerInMayTakeItOut()
    {
        Roster roster = new();
        roster.Announce("", 50100, Somewhere, 0.0);
        roster.Heard(Roster.Key("45.133.178.13", 50100), Said(), 0.0);

        // A withdrawal is not a way to unlist somebody else's server.
        Assert.False(roster.Withdraw("45.133.178.13", 50100, Elsewhere));
        Assert.Single(roster.Snapshot("d", 0.0, DateTimeOffset.UnixEpoch).Servers);

        Assert.True(roster.Withdraw("45.133.178.13", 50100, Somewhere));
        Assert.Empty(roster.Snapshot("d", 0.0, DateTimeOffset.UnixEpoch).Servers);
    }

    [Fact]
    public void OneAddressMayOnlyFillSoMuchOfTheList()
    {
        Roster roster = new();
        for (int i = 0; i < Roster.MostPerAddress; i++)
        {
            Assert.True(roster.Announce("", 50100 + i, Somewhere, 0.0).Ok);
        }
        // Whoever registers five hundred servers does not bury the ones somebody plays on.
        Noted over = roster.Announce("", 50100 + Roster.MostPerAddress, Somewhere, 0.0);
        Assert.False(over.Ok);
        Assert.Contains("as many as one address may", over.Say);

        // Another machine is unaffected.
        Assert.True(roster.Announce("", 50100, Elsewhere, 0.0).Ok);
    }

    [Fact]
    public void AnnouncingAgainSaysWhereTheServerStandsRatherThanMakingASecondEntry()
    {
        Roster roster = new();
        roster.Announce("mp.example.com", 50100, Somewhere, 0.0);
        string key = Roster.Key("mp.example.com", 50100);

        Noted again = roster.Announce("MP.Example.COM", 50100, Somewhere, 30.0);
        Assert.True(again.Ok);
        Assert.False(again.Listed);
        Assert.Equal((1, 0), roster.Count(30.0));

        roster.Heard(key, Said(), 31.0);
        Noted now = roster.Announce("mp.example.com", 50100, Somewhere, 60.0);
        Assert.True(now.Listed);
        Assert.Equal("listed", now.Say);
        Assert.Equal((1, 1), roster.Count(60.0));
    }

    [Fact]
    public void AServerNobodyIsAnnouncingAndNothingAnswersIsForgotten()
    {
        Roster roster = new();
        roster.Announce("", 50100, Somewhere, 0.0);
        Assert.Equal((1, 0), roster.Count(0.0));

        // Kept for a while after it stops being listed, so its operator can still be told why it is
        // not; forgotten once nobody is waiting to hear.
        Assert.Equal(0, roster.Forget(Roster.ForgetAfterSeconds - 1.0));
        Assert.Equal(1, roster.Forget(Roster.ForgetAfterSeconds + 1.0));
        Assert.Equal((0, 0), roster.Count(Roster.ForgetAfterSeconds + 1.0));
    }

    [Fact]
    public void TheBusiestServersComeFirst()
    {
        Roster roster = new();
        foreach ((int port, int players) in new[] { (50100, 1), (50101, 7), (50102, 0) })
        {
            roster.Announce("", port, Somewhere, 0.0);
            roster.Heard(Roster.Key("45.133.178.13", port), Said(players: players), 0.0);
        }
        // An empty list of three real servers beats three hundred rows of nothing, and within a real
        // list the rows worth joining are the ones with somebody on them.
        Assert.Equal([7, 1, 0], roster.Snapshot("d", 0.0, DateTimeOffset.UnixEpoch).Servers.Select(s => s.Players));
    }

    [Fact]
    public void ADirectoryNamesItselfAndTheDocumentItSpeaks()
    {
        Roster roster = new();
        Listing listing = roster.Snapshot("ksarmory", 0.0, DateTimeOffset.UnixEpoch);
        Assert.Equal(Listings.Version, listing.V);
        Assert.Equal("ksarmory", listing.Directory);
        Assert.Empty(listing.Servers);
    }
}
