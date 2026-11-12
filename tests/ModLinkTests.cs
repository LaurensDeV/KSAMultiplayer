using Xunit;

namespace KSAMultiplayer.Tests;

/// <summary>
/// The link a mod manager is handed for a world's mods, held against what Borea reads: every mod
/// at a release, KSA's own content left out, and no link at all where one could not say what the
/// world wants, since a manager refuses the whole of a link it does not like.
/// </summary>
public class ModLinkTests
{
    private const string Mine = "Core,KSAMultiplayer@0.19.2+32c2e3f0d1a4b5c6d7e8f9a0b1c2d3e4f5a6b7c8";

    [Fact]
    public void AWorldsModsAreALinkWithAVersionOnEachOfThem()
    {
        // The cars server as it stands: its own mod left open, since the protocol holds that, and
        // the car mod held to a release. The open one is given the version this game has.
        Assert.Equal("borea://modlist?mods=KSAMultiplayer@0.19.2,FastAndPurrious@0.1.14&name=Cars&id=ksamp-mpcars.ksarmory.com",
                     ModLink.For("Core,KSAMultiplayer,FastAndPurrious@0.1.14", Mine, "Cars", "mpcars.ksarmory.com"));
    }

    [Fact]
    public void AWorldThatWantsThisModAloneIsALinkToThisModAlone()
    {
        // What a player with a dozen mods of their own is handed in place of "turn them all off".
        Assert.Equal("borea://modlist?mods=KSAMultiplayer@0.19.2&name=Multiplayer+test+server&id=ksamp-mp.ksarmory.com",
                     ModLink.For("Core,KSAMultiplayer", Mine + ",Shiny@2.0.0,Louder@1.1.0", "Multiplayer test server", "mp.ksarmory.com"));
    }

    [Fact]
    public void AModNobodyHereKnowsAVersionOfIsNoLinkAtAll()
    {
        // The world's first player brought it and the server kept its name alone; this game has
        // not got it. A link without a version is one Borea refuses whole.
        Assert.Null(ModLink.For("Core,KSAMultiplayer,KSArmory", Mine, "Armed", "example.org"));
        // And with it at hand, the version this game has is one the world takes.
        Assert.Equal("borea://modlist?mods=KSAMultiplayer@0.19.2,KSArmory@0.3.0&name=Armed&id=ksamp-example.org",
                     ModLink.For("Core,KSAMultiplayer,KSArmory", Mine + ",KSArmory@0.3.0+abc1234", "Armed", "example.org"));
    }

    [Theory]
    [InlineData("Cars@0.1.12+d2411b6", "Cars@0.1.12")]
    [InlineData("Cars@0.1.14.0", "Cars@0.1.14")]
    [InlineData("Cars@1.0.0-beta.2+5f3a", "Cars@1.0.0-beta.2")]
    public void AVersionIsSaidAsTheReleaseItIs(string wanted, string said) =>
        Assert.Equal($"borea://modlist?mods={said}", ModLink.For(wanted, "", "", ""));

    [Theory]
    [InlineData("")]
    [InlineData("Core")]
    [InlineData("Cars@latest")]
    [InlineData("Cars@1.2")]
    [InlineData("Cars@1.2.3.4")]
    [InlineData("-cars@1.0.0")]
    [InlineData("Cars&id=x@1.0.0")]
    public void WhatALinkCannotSayIsNotSaid(string wanted) => Assert.Null(ModLink.For(wanted, "", "Somewhere", "example.org"));

    [Theory]
    [InlineData("Laurens' Server", "Laurens%27+Server")]
    [InlineData("  a&b=c  ", "a%26b%3Dc")]
    [InlineData("Köln", "Kln")]
    public void ANameIsPlainAsciiAndCannotAddToTheLink(string name, string said) =>
        Assert.Equal($"borea://modlist?mods=Cars@1.0.0&name={said}", ModLink.For("Cars@1.0.0", "", name, ""));

    [Fact]
    public void ANameWithNoPlainLetterInItIsLeftOut() =>
        Assert.Equal("borea://modlist?mods=Cars@1.0.0&id=ksamp-example.org", ModLink.For("Cars@1.0.0", "", "猫の宇宙", "example.org"));

    [Theory]
    [InlineData("mpcars.ksarmory.com", "ksamp-mpcars.ksarmory.com")]
    [InlineData("MP.KSArmory.com:50101", "ksamp-mp.ksarmory.com-50101")]
    [InlineData("[2001:db8::1]:50100", "ksamp-2001-db8-1-50100")]
    [InlineData("  ", "")]
    public void AWorldIsKnownByTheAddressItIsReachedAt(string where, string id) => Assert.Equal(id, ModLink.Id(where));

    [Fact]
    public void AServerThatSaysWhatItIsCalledIsKnownByThatWhereverItIsReached()
    {
        Assert.Equal("borea://modlist?mods=Cars@1.0.0&name=Cars&id=ksamp-mpcars",
                     ModLink.For("Cars@1.0.0", "", "Cars", "45.133.178.13:50101", "mpcars"));
        Assert.Equal("borea://modlist?mods=Cars@1.0.0&name=Cars&id=ksamp-mpcars",
                     ModLink.For("Cars@1.0.0", "", "Cars", "mpcars.ksarmory.com", "mpcars"));
        // What a server says of itself is a stranger's text: one that is no id is not said, and
        // the server is known by its address as one that said nothing is.
        Assert.Equal("borea://modlist?mods=Cars@1.0.0&name=Cars&id=ksamp-example.org",
                     ModLink.For("Cars@1.0.0", "", "Cars", "example.org", "x&mods=Evil@6.6.6"));
        Assert.Equal("borea://modlist?mods=Cars@1.0.0&name=Cars&id=ksamp-example.org",
                     ModLink.For("Cars@1.0.0", "", "Cars", "example.org", new string('a', Wire.IdChars + 1)));
    }

    [Fact]
    public void AnIdIsNoLongerThanBoreaTakesAndEndsOnALetterOrDigit()
    {
        string id = ModLink.Id(new string('a', 56) + ".-.example.org");
        Assert.Equal(64 - 2, id.Length);
        Assert.EndsWith("a", id);
    }

    [Fact]
    public void OnlyALinkThisMadeIsOneToOpen()
    {
        Assert.True(ModLink.Is(ModLink.For("Cars@1.0.0", "", "A b", "example.org")!));
        Assert.False(ModLink.Is("https://example.org/borea://modlist?mods=Cars@1.0.0"));
        Assert.False(ModLink.Is("borea://modlist?mods=Cars@1.0.0 & calc.exe"));
    }
}
