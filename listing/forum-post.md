# The forums thread

Borea's listing requires a thread on `forums.ahwoo.com`: it ties the listing to an Ahwoo account,
settles disputes over an id, and is the takedown tripwire. The thread must exist before
`listings/KSAMultiplayer.toml` can be merged into `KSAModding/content-index`.

Post it under **Kitten Space Agency → Mod Releases**, with the **Gameplay** prefix (the prefix
`tags.toml` maps the `gameplay` tag to). Then put the thread URL in `links.forums` in the listing.

---

## Title

```
KSA Multiplayer — one world, several players, a dedicated server
```

---

## Body

The forum is XenForo, so BBCode below rather than Markdown. Paste it with the editor's **BB code**
mode on (the `[ ]` toggle in the toolbar), or paste into the plain-text editor — pasted into the
rich text editor the tags may show literally.

```bbcode
[SIZE=5][B]KSA Multiplayer[/B][/SIZE]

A shared world for Kitten Space Agency: several players, each in their own game, flying in one universe kept by a [B]dedicated server[/B]. The server runs on Linux, Windows or a Raspberry Pi, needs no copy of the game, and holds the world whether anybody is playing or not.

Any craft syncs: rockets, rovers, kittens walking about on EVA. You see other players fly, stage, burn and crash in real time, each craft carrying its owner's name.

[SIZE=4][B]Playing together[/B][/SIZE]

[LIST]
[*][B]A craft is its owner's.[/B] You can watch one somebody else is flying, ride along with it, and see its engines burn — but not take it, board it or empty it out. When its owner leaves, it comes free.
[*][B]One clock.[/B] Everybody keeps the server's time. A game that falls behind catches up on its own, and nobody is left watching a world minutes out of date.
[*][B]Time warp is asked, not taken.[/B] Changing the simulation speed is put to the room first, and only whoever asked can end it. A game that cannot keep up holds the speed down for everyone, so nothing runs away from the slowest machine.
[*][B]Hold Tab[/B] for who is playing and their ping, [B]Enter[/B] for a chat line.
[*]Launchpads are shared out, so two players launching at one site do not land on top of each other.
[/LIST]

[SIZE=4][B]The server[/B][/SIZE]

One self-contained executable. Point it at a port and it waits; the first player to join gives it their world, and it hands that world to everyone who joins after. Or set [ICODE]empty-world[/ICODE] and it starts with an empty one of its own, so nobody has to seed it.

It keeps the world between runs, saves periodically, and has a console: [ICODE]clear[/ICODE] sweeps up the spent stages a shared world fills with, [ICODE]reload[/ICODE] hands the world out again without anybody reconnecting, and [ICODE]fresh[/ICODE] throws it away and starts over. Players can be made admins and run those commands from inside the game.

There is a server at [B]mp.ksarmory.com[/B] to try it on.

[SIZE=4][B]Installing[/B][/SIZE]

[LIST=1]
[*]Install [URL='https://github.com/StarMapLoader/StarMap']StarMap[/URL] 0.5.3 or later.
[*]Take the zip from [URL='https://github.com/LaurensDeV/KSAMultiplayer/releases']the releases page[/URL], unzip it into KSA's [ICODE]mods[/ICODE] folder and enable it.
[*]Load a world and get into flight. The [B]Multiplayer[/B] window is at the top right, and the start screen can join a server before the world even loads.
[/LIST]

Hosting is the same download: [ICODE]ksamp-server-win-x64.zip[/ICODE] for Windows, a tarball for Linux and ARM, or a container image. The machine you play on can host.

[SIZE=4][B]What you need[/B][/SIZE]

Everyone in a world needs the [B]same KSA build[/B], the same mod version, and every mod the world's craft use. The server turns away anyone who differs and says what is missing, rather than letting a mismatch corrupt the world quietly. This release is built for KSA [B]2026.11.10.5801[/B].

[SIZE=4][B]Honest status[/B][/SIZE]

Two games on one machine have flown in one world through all of this: launching, EVA, chat, nametags, a voted warp, and an 89-part rocket staged while the other game watched and rebuilt the dropped stage as its own craft. Another player's engines are seen to burn even on a craft your game is not simulating, thousands of kilometres away.

What a dozen players across the internet do to it is not known yet. Bug reports are very welcome — that is mostly what this thread is for.

[SIZE=4][B]Links[/B][/SIZE]

[LIST]
[*][URL='https://github.com/LaurensDeV/KSAMultiplayer']Source and releases[/URL]
[*][URL='https://github.com/LaurensDeV/KSAMultiplayer/issues']Bug tracker[/URL]
[*]MIT licensed.
[/LIST]
```

---

## A plain-text version

If BBCode will not paste cleanly, the same text without markup is `description.md`, which reads
fine as prose with its `###` headings removed.
