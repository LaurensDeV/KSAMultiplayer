# Testing the clock, and testing it over a wire that is not loopback

Two things, written together because the second is what the first keeps needing and because it is
easy to build the wrong one. A plan, not a record: nothing here is done except where it says so --
and the trace is, as `tools/warp-trace.py`. What it walks and what its first runs found is at the
end.

## Why timewarp wants a different shape of test from everything else here

`tools/breakup.sh` freezes both worlds and compares them, and that is the right shape for ownership:
the question is what each world *holds*, and a frozen world answers it without a tolerance. Timewarp
is not like that. Every fault in this family lives in a **transition** -- a rate going up, coming
down, being governed down, being left behind, ending -- and a snapshot taken after one is a snapshot
of the recovery rather than of the fault. `docs/TWO-GAMES.md` notes that the freeze is circular for
anything about warping; worse than circular, it is blind.

So this one wants a **trace**: both games and the server sampled together through a scripted sequence
of transitions, with the assertions run over the whole series rather than at the end. Two samples a
second is plenty -- the bugs here last for minutes -- and the bridge already answers everything
needed in one `status`: `universe`, `speed`, `behind_s`, `can`, and `warp` with its speed, phase and
who is waiting.

**This is not hypothetical.** The first thing a trace would have caught is the bug found on
2026-11-10 by running exactly this sequence by hand: ask 1000x, agree it, end it. During the warp
both games sat 130 universe-seconds behind the server, which at 1000x is 0.13 *real* seconds and
entirely healthy. `EndWarp` then dropped the server's clock to 1x without reconciling the gap, and
the same few hundred universe-seconds became a few hundred *real* seconds ahead -- past
`ClockFollow.Speed`'s dead stop at three. Both games froze: A for about eight minutes, B for six. The
whole room, not just whoever asked. `errors.md` 2026-11-09 has the measurement.

**Fixed**, by `Server.FollowTheRoom`: where every game says it is further past the clock than a
nudge can close, the clock keeps up. It moves to the *least*-ahead game, so it passes nobody and one
game cannot drag the room. Three tests in `ServerTests` hold it.

**And the first fix was wrong, which only a game showed.** It reconciled the lead inside `EndWarp`,
on the theory that the gap existed before the rate fell. Measured mid-warp, both games were a
hundred universe-seconds *behind*: the lead is **made by** the ending, in the window between the
server dropping its rate and each game learning of it, which at 1000x is the round trip plus a frame
-- several hundred universe-seconds. So there was nothing to reconcile at that instant, both games
froze exactly as before on a server carrying the fix, and its unit test passed the whole time
because it set the lead by hand and tested the theory rather than the game. This is the second time
in two days that a test written to a plausible story would have passed and been read as a fix
working; `docs/TWO-GAMES.md` has the first.

That bug was invisible to every test this repository had, and it was one minute's work to find once
the sequence was walked deliberately. Which is the argument for the trace.

## The seams, named

Worth writing down because the warp surface is unusually wide, and because a test that does not name
the seam it is pushing on tends to push on the same one twice.

**On the server.** `_warpPhase` is None, Asking or Running. An ask by anyone else during Asking or
Running is dropped; a speed within a thousandth of 1 ends a warp rather than starting one; and for
`WarpQuietSeconds` after an end nobody's ask is taken, the asker's own included. `MostWarp` is
100,000. Every loaded player must answer and all
must answer yes -- silence is not consent, the ask runs out after `--warp-answer` -- and a vote is
`TryAdd`, so the first answer is the only one that counts. And the ask is **not** agreed at the
speed asked: `Flyable` holds it to what the room can fly, and it is not run at what is agreed
either, unless every watching machine has said what it flies -- `Reached` starts it at `GovernStep`
and the governor climbs it by that much a second. The governor keeps moving it during the warp:
down to what a machine says it can fly, back by `GovernBackOff` when the worst player is more than
`GovernLateSeconds` behind and not catching up, and no further up while anybody is being left.
And when the clock's speed falls, `FollowTheRoom` takes it on to the game furthest past it.

**In the game.** `HoldTheClock` holds a game to the server's time wherever its world runs, at a
craft or at none: only in the vehicle editor, where KSA stops the world itself, does it let go, at
1x and saying it is late by nothing. `ClockFollow.Speed` divides the gap by the rate, which is what makes a gap safe at high warp
and fatal at low. The catch-up ceiling is `MostCatchUp` (10) with anything of this game's in physics
and `MostCatchUpOnRails` (1000) without, chosen per frame off `World.Analytic`. `ClockFollow.Asked`
then multiplies by up to 2 to make up a slow frame, which is a second feedback path on top of the
first. `Sustains` is what this machine reports it can fly, measured over a window, and it is what the
governor acts on.

## What to assert, worst first

All zero-expected or "both agree", for the reasons `docs/TWO-GAMES.md` gives, and all sampled across
the whole trace rather than at its end.

1. **No game is ever stopped while the server's clock is running.** `speed == 0` with no pause asked
   and the server's rate above zero. This is today's bug, stated so that it fails the run.
2. **No game is ever more than a few real seconds from the server.** `|behind_s| / max(rate, 1)`
   bounded, **through** every transition and not only in the steady state. The bound is the thing to
   argue about; the division by the rate is not.
3. **The room agrees on the rate.** Every game's `warp.speed` equals the server's within a sample.
4. **Universe time never goes backwards**, in any game or on the server. `UniverseClock.SetSpeed`'s
   own comment records a server that rewound coming out of a warp, which is this assertion failing.
5. **A warp never runs without every loaded player's yes**, and never survives a no.
6. **Only the owner changes a running warp.** Another player's ask changes nothing.
7. **A `modspeed` write never reaches the room and a `pickspeed` does.** Five separate `fix(mod)`
   commits say this is the most-repeated failure in the project, and the bridge was built to drive
   it. Cheapest high-value assertion here.
8. **The governor only lowers the rate below what was asked**, and climbs back toward it.

## The transitions to walk, in one scripted run

Fenced with `mp chat` lines so all three logs can be cut at each step, and swept between where a
step leaves wreckage.

- 1 → 10 → 1 with a craft in physics; 1 → 1000 → 1 with everything on rails. Both directions of
  `MostCatchUp` versus `MostCatchUpOnRails`.
- Ended three ways: by the owner asking for 1, by another player's no, by the ask running out.
- Stepped down through normal and straight back up, inside `WarpQuietSeconds` -- and a second ask
  by somebody else in that window. Both are dropped without a word, which the walk writes down
  rather than judges.
- The owner changing speed mid-warp, and a non-owner trying to.
- One game leaving its craft mid-warp, where `HoldTheClock` forces it to 1x while the room warps,
  and coming back.
- A player joining mid-warp, and a player disconnecting mid-warp with a vote outstanding.
- A save loaded in one game mid-warp.
- A machine that cannot fly the asked speed, so the governor has something to do. `modspeed` can
  make a game *look* slow to the governor without needing a slow machine.

## And the wire, which loopback is not

Loopback has no loss, no reordering, no jitter and a round trip of about 0.05 ms. A real player on
`mp.ksarmory.com` measured 9 ms; a European player is 25-50 ms, a transatlantic one 150 ms. A whole
family of faults is proportional to that number and therefore structurally invisible here:

- the clock offset the client estimates from ping and pong, whose error feeds straight into
  `behind_s` and so into every assertion above;
- how far past its newest state a puppet is drawn, which is the trip from one game to the other, and
  `MostAheadSeconds` (1.0), where that stops;
- `SilentSeconds`, the server's 10-second patience, which is how Onyx kept being dropped;
- reordering and loss, which the protocol is built to tolerate and which nothing here has ever made
  it tolerate.

**Be clear about what it would not have caught, though: today's warp bug.** That one is arithmetic
at a rate change and reproduces at 0 ms. Latency is worth building for the list above, not as a
general-purpose hope that more realism finds more bugs.

### How, and how not

**Not `tc netem`.** In WSL's default NAT mode the rig runs a *Windows* server beside two Windows
games, so nothing crosses a Linux interface and there is nothing for `tc` to shape. The same
objection kills a relay written in Python under WSL: a Windows game cannot reach a UDP socket bound
inside WSL's own network stack, which is why `two-games.sh` publishes a Windows server in the first
place.

**A relay mode on the server binary, which exists now.**

```bash
ksamp-server --relay 127.0.0.1:50100 --port 50200 --latency 25 --jitter 5 --drop 0.01
KSAMP_LAG_MS=25 KSAMP_LAG_JITTER_MS=5 ./tools/two-games.sh --test   # the rig, behind one
```

One socket faces the games and one more per game, because the port a reply comes back on is what
says which game it belongs to -- the same trick a NAT uses, and the reason the server needs no part
in this. It forwards raw bytes and parses nothing, so a `Wire.Version` bump cannot break it. It
serves no world and takes none of the server's settings, reads and writes no settings file, and adds
no key to `ksamp-server.toml`: a real server's session grows nothing an operator could trip.

Holding a datagram for `d` in both directions gives a game-to-server round trip of `2d` and a
game-to-game path of `2d`, which is what a real server `d` from each player does. Jitter is what
makes datagrams pass each other, so reordering is not a knob of its own -- it is what a delay that
varies already means -- and the queue is therefore ordered by when each datagram is *due* rather than
by when it arrived.

Two things it cost to get right, both worth knowing:

- **The relay has to be the Windows build.** The first attempt ran it under WSL and the game could
  not reach it at all -- which is the constraint written three paragraphs above, confirmed the hard
  way. `two-games.sh` publishes it with `publish-server.sh win-x64`, as it already does the server.
- **Windows naps in whatever its timer resolution is, 15.6 ms by default.** A one-millisecond sleep
  in the pump loop therefore overshot a 25 ms delay by a third: the games measured a 62 ms round trip
  where 50 was asked for. `timeBeginPeriod(1)` brings it to 55 ms measured, about 2.6 ms of overshoot
  per hop. The relay reports the delay it actually applied, per interval rather than cumulatively --
  a whole-run mean is dominated by the burst that hands a game the world -- so a run never has to
  take the asked-for number on trust, and `breakup.sh` prints the round trip it ran at for the same
  reason it prints which server build served the games.

Run the warp trace and `breakup.sh` at 0 ms, 25 ms, 50 ms and 150 ms. A claim that holds at 0 and
fails at 50 is the interesting result.

## What none of this reaches

Load: the failures that shipped were found with three or four players and rockets in pieces, and a
third game is ruled out by this card's memory. Two machines, and so each player's own line, NAT and
MTU. Where anything is could not be asked when this was written, since neither `status` nor `craft`
reports a position; `tools/motion.py` asks it now, of what the bridge can make a craft do.

Nor anything a craft carries that is not a part and not a name. A kitten was found on 2026-11-10
drawn torn open in one game and clean in the other, while both report one part: the two worlds
agreed on every number a test here compares, and the players were looking at different things. That
one turned out to be the rig's own doing -- `errors.md` has it -- but a picture is the only
instrument that found it, and the harness takes none.

## The trace, as built

```bash
./tools/two-games.sh --test          # the rig, whose server now writes its own clock down
./tools/warp-trace.py                # walk it and judge the series; about four minutes
./tools/warp-trace.py --only thousand
./tools/warp-trace.py --check <run>  # judge a trace again, with no game up
```

Both games are asked for `status` together twice a second, and the server writes its clock, its
warp and each player's lateness to a file four times a second and on both sides of every change
(`ksamp-server --trace <path>`, a flag and not a setting). Every time in it is Windows's time of
day: a game's `status` carries the instant it was answered at, the server's lines carry theirs, and
both are processes on one machine. So how far a game was from the server is worked out against the
server's own clock at the instant the game answered, and the game's own `behind_s` is reported
beside that rather than trusted.

**None of it is timed on the Linux side**, which the first run was. WSL's clock ran two percent
fast against Windows's and was stepped back six tenths of a second every half minute, so one step
of the walk was laid over the one before it and a healthy run failed. Where a step begins is asked
of a game now.

It judges the first four assertions above over every sample, and the rest as what each step expects
of the server: STOPPED, LATE, RATE, BACKWARDS, then consent, ownership, the ceiling, and a speed
written by a program against one picked by a player. Exit 0, 1 or 2 as `breakup.sh` does. A made-up
trace with one game 30 s past the clock fails STOPPED and LATE and nothing else.

LATE is the one whose bound took arguing. Six seconds from the clock, in seconds of the rate then
running, and for how long: three seconds past it ahead, since a game ahead stands still for
exactly as long as it is ahead; and behind, three more than the catch-up it is meant to make takes,
since the end of a warp turns a tenth of a second at 1000x into a hundred at 1x on the instant and
what matters is whether a game then makes them up. `ClockFollow` halves a gap every 1.4 s.

The rig has a mode for this work: `./tools/two-games.sh --server` puts this tree's server under the
two games as they stand, in under a minute, where bringing the games up again is four.

Walked, in order: nothing asked; `modspeed` with no warp running; `pickspeed`, refused by the other
game; 10x agreed and ended by its asker; an ask refused; an ask nobody answers; 4x, changed to 8x by
its asker, then asked to change and to end by the other player, then written at by `modspeed` in
each game; an ask by its own asker inside the quiet; the faster key pressed three times in a second
and the key for normal speed; 1000x agreed, held for half a minute and ended. **Not walked**: a
player leaving their craft mid-warp, joining or leaving mid-warp, a save loaded mid-warp, and a
machine made to look slow. Which catch-up ceiling a step ran under is reported and not arranged:
the bridge can light an engine and cannot throttle one.

### What it found, 2026-11-10, on loopback

The whole walk five times in the day, with parts of it in between, and the first four failed. Each
failure was something wrong with the game, the server or the trace, and `errors.md` has them in
order:

- **A warp asked at 1000x ran for one second and then at 1x**, LATE by two orders of magnitude.
  The governor: a warp started at the ask in a room that had said nothing, and backing off for
  lateness made the lateness worse.
- **With that fixed a warp held 1000x for the first time, and the end of it stood one game still
  for 42 s and counting**, STOPPED. A game eased its idea of the server's rate towards a new one
  over more than a second, and the clock went on to the nearest game where it should, just after
  its own speed has fallen, go on to the furthest.
- **Two more presses of the faster key did nothing while a warp was climbing**, a claim of the
  step's own. The key stepped from where the world was and not from what had been asked.
- **And the first walk of all, a short one, failed on a healthy game**, because it was timed on
  WSL's clock.

The fifth holds every claim: no game stood still while the clock ran; no universe time went
backwards; no game named a rate the server had not; a refused ask and an unanswered one never ran;
the other player could neither steer nor end a warp; no warp ran above what was asked; `modspeed`
never reached the room, with no warp running or with one, in the asker's game or the other's;
`pickspeed` did; three presses raised what was asked three times. Through every warp both games
sat within 0.02 s of the clock, a warp to 1000x got there in seventeen seconds and stayed, and at
its end the games were 20 s and 17 s past a clock that went on 21 s to them.

Worth knowing before reading a run: the server's log says `the world rises to` once a second for
the whole of a climb, and a warp to 1000x is seventeen of them; and whether a room can fly what was
asked depends on what is in it -- with a rocket's wreckage in the sea the same two machines topped
out near seventy.

### And over the wire, the same day

```bash
KSAMP_LAG_MS=50 ./tools/two-games.sh --server && ./tools/warp-trace.py
KSAMP_LAG_MS=50 KSAMP_LAG_JITTER_MS=20 KSAMP_LAG_DROP=0.02 ./tools/two-games.sh --server
```

| each way | every claim | what failed |
| --- | --- | --- |
| 25 ms | held | |
| 50 ms | held | |
| 150 ms | failed, then held | the clock followed one game's lead twice |
| 50 ms give or take 20, one in fifty dropped | failed three walks in six | things said once: the ask for a world, the end of a warp, an ask or a vote |

Delay alone scales one number and breaks nothing until a round trip is a fair part of a second:
a game's lead at the end of 1000x is twenty seconds on loopback and about a second more for every
millisecond each way, and the clock goes on to it. At 300 ms round trip a ping written before its
game had heard the clock move arrived after the clock had moved, and was followed again.

Loss and reordering found nothing wrong with what the protocol repeats -- states, pieces of a
world -- and three things wrong with what it said once. `errors.md` has all of it. What is left
said once is an ask for a warp and a vote on one, which the walk now makes again when unheard, a
line of chat, and the word to a loading game that a craft has gone.

The claim that wanted a number for where a craft is has one now: `tools/motion.py` records both
games at every physics step, and `tests/TrackTests.cs` flies a known path through a simulated wire
with no game at all. What they found is in `errors.md`.

