# Testing the clock, and testing it over a wire that is not loopback

Two things, written together because the second is what the first keeps needing and because it is
easy to build the wrong one. A plan, not a record: nothing here is done except where it says so.

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
whole room, not just whoever asked. `errors.md` 2026-11-09 has the measurement and the fix.

That bug is invisible to every test this repository has, and it was one minute's work to find once
the sequence was walked deliberately.

## The seams, named

Worth writing down because the warp surface is unusually wide, and because a test that does not name
the seam it is pushing on tends to push on the same one twice.

**On the server.** `_warpPhase` is None, Asking or Running. An ask by anyone else during Asking or
Running is dropped; a speed within a thousandth of 1 ends a warp rather than starting one;
`WarpQuietSeconds` after an end belongs to whoever had it, so stepping down through normal and back
up is one warp rather than two votes. `MostWarp` is 100,000. Every loaded player must answer and all
must answer yes -- silence is not consent, the ask runs out after `--warp-answer` -- and a vote is
`TryAdd`, so the first answer is the only one that counts. And the ask is **not** agreed at the
speed asked: `Flyable` holds it to what the room can fly, and the governor keeps moving it during the
warp, backing off by `GovernBackOff` when the worst player is more than `GovernLateSeconds` behind
and climbing back by `GovernStep` when they are not.

**In the game.** `HoldTheClock` stops holding anything when the player is not at a craft, and forces
1x -- so a player who opens the tracking station mid-warp is running at real time while the room
warps. `ClockFollow.Speed` divides the gap by the rate, which is what makes a gap safe at high warp
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
- Stepped down through normal and straight back up, inside `WarpQuietSeconds`, which should be one
  warp and not two votes -- and a second ask by somebody else in that window, which should wait.
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
- `Track.DelaySeconds` (0.15) and `MostAheadSeconds` (1.0), the window a puppet is drawn in -- at
  150 ms the delay *is* the round trip, and the extrapolation past the newest state stops being a
  corner case;
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

**A relay mode on the server binary.** `ksamp-server --relay 127.0.0.1:50100 --port 50200
--latency 25 --jitter 5 --loss 0.01 --reorder 0.02`: one socket facing the games, one endpoint per
client, a delay queue in each direction, and the games pointed at the relay instead of the server.
It reuses `Udp`, `publish-server.sh win-x64` and the Windows build the rig already makes, and it
serves no world, so a real server's session grows no test-only knob an operator could trip. `Udp`
already has `Loss` for exactly this purpose, used only in-process by the tests.

Holding a datagram for `d` in both directions at the relay gives a client-to-server round trip of
`2d` and a game-to-game path of `2d`, which is what a real server `d` away from each player does.
Jitter means datagrams can pass each other, which is the reordering, so the queue has to be ordered
by ready-time rather than by arrival -- that is the whole implementation.

Run the warp trace and `breakup.sh` at 0 ms, 25 ms, 50 ms and 150 ms. A claim that holds at 0 and
fails at 50 is the interesting result, and the report should print the latency it ran at for the same
reason it already prints which server build served the games.

## What none of this reaches

Load: the failures that shipped were found with three or four players and rockets in pieces, and a
third game is ruled out by this card's memory. Two machines, and so each player's own line, NAT and
MTU. And where anything is -- neither `status` nor `craft` reports a position, so whether a puppet is
where its owner says it is still cannot be asked by a test at all.
