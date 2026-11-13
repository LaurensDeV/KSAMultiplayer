using KSAMultiplayer.Directories;
using Xunit;

namespace KSAMultiplayer.Tests;

/// <summary>
/// What a link to the page shows where it is pasted, which a chat program reads out of the head
/// without running anything: the count of every server under the page of them all, and a
/// server's own under its own.
/// </summary>
public class CardTests
{
    private static Everything Of(params Listed[] servers) =>
        new(Everywhere.Version, ["d"], [.. servers.Select(s => new Carried(s, ["d"]))]);

    private static readonly Listed Cars = new("cars.example.org", 50101, "Cars", Players: 3, Max: 16, System: "SolLite",
                                              Mods: "Core,KSAMultiplayer,FastAndPurrious@0.1.14+abc1234", Ksa: "2026.11.10.5801", Srv: true);

    // As a reader of the page has it: a mark outside ASCII is written as its number, which is
    // the same mark to anything that reads markup.
    private static string Read(string head) => System.Net.WebUtility.HtmlDecode(head);

    private static readonly Listed Plain = new("example.org", 50100, "Test server", Players: 1, Max: 8);

    [Fact]
    public void ALinkToTheListCountsEveryServerAndEveryPlayer()
    {
        string head = Read(Card.Head(Of(Cars, Plain)));
        Assert.Contains("<meta property=\"og:title\" content=\"KSA Multiplayer servers\">", head);
        Assert.Contains("<meta property=\"og:description\" content=\"2 servers, 4 players online. Cars 3/16 · Test server 1/8\">", head);
    }

    [Fact]
    public void OneServerAndOnePlayerAreNotSaidAsSeveral() =>
        Assert.Contains("content=\"1 server, 1 player online. Test server 1/8\"", Card.Head(Of(Plain)));

    [Fact]
    public void AListOfNothingSaysSo() =>
        Assert.Contains("content=\"No server is listed right now.\"", Card.Head(Of()));

    [Fact]
    public void ALongListNamesTheFirstFewAndCountsTheRest()
    {
        Listed[] many = [.. Enumerable.Range(1, 9).Select(i => new Listed($"s{i}.example.org", 50100, $"S{i}"))];
        Assert.Contains("S6 0 · and 3 more\"", Read(Card.Head(Of(many))));
    }

    [Fact]
    public void ALinkToAServerCountsItsOwnPlayersAndSaysWhatItNeeds()
    {
        string head = Read(Card.Head(Of(Cars, Plain), Cars));
        Assert.Contains("<title>Cars · 3 of 16 players online</title>", head);
        Assert.Contains("content=\"A KSA Multiplayer server at cars.example.org. World: Earth and Moon. "
                        + "Needs KSAMultiplayer, FastAndPurrious 0.1.14. KSA 2026.11.10.5801.\"", head);
    }

    [Theory]
    [InlineData("cars.example.org", "Cars")]
    [InlineData("CARS.example.org:50101", "Cars")]
    [InlineData("example.org", "Test server")]
    [InlineData("example.org:50100", "Test server")]
    public void AServersPageIsFoundByWhatAPlayerTypesOrByItsPortWrittenOut(string where, string name) =>
        Assert.Equal(name, Card.Find(Of(Cars, Plain), where)?.Name);

    [Theory]
    [InlineData("cars.example.org:50100")]
    [InlineData("nowhere.example.org")]
    [InlineData("")]
    public void AnAddressThatNamesNoServerFindsNone(string where) => Assert.Null(Card.Find(Of(Cars, Plain), where));

    [Fact]
    public void WhatAServerCallsItselfCannotAddToThePage()
    {
        Listed rude = new("example.org", 50100, "\"><script>alert(1)</script>", Mods: "Core,<img src=x>@1.0.0", System: "'\"<b>");
        foreach (string head in new[] { Card.Head(Of(rude)), Card.Head(Of(rude), rude) })
        {
            Assert.DoesNotContain("<script", head);
            Assert.DoesNotContain("<img", head);
            Assert.DoesNotContain("<b>", head);
            // Every tag is one this wrote: a quote that closed an attribute would begin another.
            Assert.Equal(8, head.Split('\n').Count(l => l.TrimStart().StartsWith('<')));
            Assert.All(head.Split('\n'), l => Assert.Equal(l.Contains("content=") ? 4 : 0, l.Count(c => c == '"')));
        }
    }
}
