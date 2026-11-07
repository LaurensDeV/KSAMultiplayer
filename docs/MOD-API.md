# Letting other mods into the session

**Nothing here is built.** This is the plan for one thing: a mod that has never heard of KSA
Multiplayer should be able to put its own state into a shared world, and KSA Multiplayer should carry
it without having heard of that mod either. Written against StarMap 0.5.0 and protocol 18.

The two mods must agree on one thing, and it is a string.

## What the constraint rules out

StarMap loads every mod into an `AssemblyLoadContext` of its own, and has since 0.4.8
(`StarMap.Core/ModRepository/ModAssemblyLoadContext.cs`). A type compiled into KSArmory is a
different runtime type from the same source compiled into KSAMultiplayer, so the usual answers do
not survive contact:

- **A shared contract DLL.** Works, but only if the guest names `ModId = "KSAMultiplayer"` in its
  `mod.toml` and lists the assembly in `ImportedAssemblies` — `ModAssemblyLoadContext.Load` resolves
  from another mod's context only for a name in that list. That is the guest knowing about this mod.
  And when two mods ship different copies of one contract, the failure is not an exception: the two
  copies load into two contexts, `is` returns false, and the feature silently vanishes.
- **`IModServiceRegistry`.** Cannot be used at all for this. `ModServices.Register<T>` throws unless
  `AssemblyLoadContext.GetLoadContext(typeof(T).Assembly)` is the registering mod's own context, so a
  contract made of framework types is rejected by construction, and `Get<T>` throws rather than
  returning null when a provider is absent. The service registry is for mods that *do* know each
  other.
- **Reflection into this mod's internals.** Everything in `src/Mod` is `internal` except
  `MultiplayerMod` and `BridgeCommand`. A guest would be reaching into private names that change
  whenever this mod is refactored, which is a contract nobody is maintaining.

## What StarMap 0.5.0 makes possible

Five facts from its source, all verified at tag `0.5.0`:

1. `IMediator` is registered as a **singleton** and `IEventBus` is a per-mod scoped facade that
   forwards to it unchanged (`StarMap.Core/StarMapCore.cs`, `StarMap.Core/API/EventBus.cs`). The bus
   genuinely crosses mods.
2. The bus keys its buses by `typeof(T)` in a `ConcurrentDictionary<Type, IEventBus>`, with an
   optional `string eventName` for a named channel under that type. No marker interface, no
   registration, no declared dependency (`StarMap.Core/Services/Mediator.cs`).
3. Unlike the service registry, **the bus never looks at where `T` came from**. So a `T` whose
   identity is the same in every load context works.
4. `ModAssemblyLoadContext.Load` resolves from `Default.Assemblies` first and the game's context
   second. **Core-library types and KSA's own types are therefore one identity for every mod**, so a
   delegate taking a `KSA.Vehicle` is a type both mods already share, and a mod that shipped its own
   `KSA.dll` would never have it loaded. This is narrower than "framework types": those two branches
   match on simple name and ignore version, so an out-of-band assembly like `System.Text.Json` binds
   to whatever is already loaded, which is a `MissingMethodException` waiting rather than a
   guarantee. Only types in or forwarding to the core library are safe, which is all this design
   uses.
5. Delivery is synchronous on the publishing thread, a throwing handler is logged and the other
   subscribers still get the message, and there is no replay.

Fact 5's last clause is usually a nuisance and here it is the whole handshake: because `Publish`
is synchronous, a guest knows by the time `Publish` returns whether anybody answered.

## What StarMap 0.5.0 gets wrong, which this has to live with

`RuntimeMod.InitializeMod` calls `ActivatorUtilities.CreateInstance` with no try/catch; `ModsLoader`
handles only a `false` return; `StarMapCore`'s constructor rethrows; and `StarMap/Program.Main` has
no handler. So **an exception from any mod's entry-class constructor is an unhandled exception in
the loader, and the game never starts, for every mod the player has installed.** The same is true of
`ModAssembly.GetTypes()` and of parsing a `mod.toml`. Nothing degrades and nothing reaches
StarMap's own logger.

Two consequences for this design, and they are not small:

- **Nothing in the handshake may throw, on either side.** A guest's bad `take` callback cannot be
  allowed to propagate, and this mod's answer must be built without a chance of throwing. The
  degrade rules below are not politeness.
- **This is worth fixing upstream** rather than designing around. A try/catch around
  `InitializeMod` that logs and skips the offending mod is a few lines, and this repository's owner
  already has a StarMap fork and merged PRs. It would make every mod in the ecosystem safer, this
  one included.

## The mechanism

One named channel on the event bus, with a message type made only of types every context shares:

    channel name:  "ksa.multiplayer.v1"
    message type:  Dictionary<string, object>

That is the entire coupling. KSAMultiplayer subscribes to that channel in its constructor. A guest
publishes a request on it and is handed a dictionary of delegates back, or is handed nothing, which
means multiplayer is not installed and the guest carries on single-player.

Constructors run for every mod in one synchronous pass inside `ModsLoader`, before KSA's entry point
is reached at all, so a guest that asks from `[StarMapAllModsLoaded]` is always answered if this mod
is present, whatever order they loaded in.

**The direction matters, and it is the one thing to get right.** The obvious design is the opposite:
this mod announces its API once and guests listen. That is what Space Engineers' convention does —
the provider broadcasts its delegate table once in `BeforeStart()` — and it is the most-reported bug
in that whole ecosystem, because the bus does not replay and a guest that registered a moment too
late hears nothing, for ever, silently. StarMap's bus has the same property, stated in its own
documentation: it "does not queue or replay messages sent before a subscription is added".

Inverting it removes the bug class rather than managing it. A guest asks when it is ready; the
subscription it is asking into has existed since every constructor ran; and because `Publish` is
synchronous, the answer is already in hand when `Publish` returns. There is also no alternative:
the bus has no `HasSubscribers` and `GetPublisher` always succeeds, so a provider that announced
could never tell whether anybody heard it.

### What a guest must know about asking

Four rules, each of which is a real failure that the shim should hide:

- **Ask from `[StarMapAllModsLoaded]`, never from `[StarMapBeforeMain]`.** `BeforeMain` is invoked
  from inside `InitializeMod` itself, so it is interleaved per mod: a guest asking there may run
  before this mod is constructed, conclude multiplayer is absent, and stay that way for the session.
  StarMap's own event-bus documentation uses `BeforeMain` in both of its examples, so this is the
  trap a guest author will walk into by following the docs.
- **Ask on the main thread.** `Publish` runs handlers on the publishing thread, so a guest that asks
  from a worker hands this mod a callback on that worker. Everything else in the table assumes the
  main thread.
- **Keep the publisher and dispose it.** `Mediator` holds every publisher in a set until disposed.
  A guest that calls `GetPublisher` inline on each session leaks one per call.
- **Use the channel name.** The unnamed bus and the named buses are separate subscription sets, so a
  guest that forgets the name publishes where nobody is listening and silently concludes this mod is
  absent.

### The request

```csharp
var ask = new Dictionary<string, object>
{
    ["mod"]  = "KSArmory",                                   // for this mod's log only
    ["want"] = 1,                                            // envelope revision the guest speaks
    ["take"] = (Action<Dictionary<string, object>>)Received,  // called synchronously, or not at all
};
_publisher.Publish(ask);
// Received ran, or multiplayer is absent. Nothing to wait for.
```

Two things this mod owes in return. It must **wrap its invocation of `take`**, because an exception
thrown there is caught by `Mediator` and logged against the *subscriber's* mod id — that is,
reported as KSAMultiplayer's fault, with `ex.Message` and no stack trace. And it must **not retain
the ask dictionary**, because `Publish` hands the same instance to every subscriber.

It also cannot stop a second mod subscribing to the same channel and answering, nor one harvesting
every guest's `take` delegate. Mods are already in one process with Harmony; this adds no new
trust boundary, and it is listed under what this does not do rather than pretended away.

### What comes back

Every value is a core-library or KSA type, so no cast crosses a context boundary it cannot.

| Key | Type | What it does |
|---|---|---|
| `api` | `int` | Envelope revision. 1 today. A guest that does not recognise it should do nothing. |
| `mod` | `string` | This mod's version, for the guest's log. |
| `open` | `Func<string, string, int, object?>` | `(channel, modes, mostBytes)` → a handle, or `null` if refused. |
| `close` | `Action<object>` | Give a handle up. |
| `send` | `Action<object, string, byte[]>` | `(handle, craftKey, payload)`. An empty craft key means the room. |
| `on` | `Action<object, Action<string, string, byte[]>>` | `(handle, (fromPlayer, craftKey, payload))`, on the main thread in `Settle`'s window. |
| `riderAt` | `Func<object, string, byte[]?>` | The rider belonging to the state that craft is **drawn** at. Null until one has come. |
| `latest` | `Func<object, string, byte[]?>` | The newest rider heard for that craft, whatever is drawn. Null until one has come. |
| `me` | `Func<string>` | This player's name in the room. |
| `players` | `Func<string[]>` | Everyone in the room. Wrong above 64 players — see below. |
| `mine` | `Func<string, bool>` | Is this craft mine to drive and mine to describe? Eventually consistent. |
| `ownerOf` | `Func<string, string>` | Who owns it, or empty for a craft nobody owns. |
| `keyOf` | `Func<Vehicle, string>` | The key this session knows a craft by. **Not `v.Id`.** |
| `craftOf` | `Func<string, Vehicle?>` | The local craft for a key, or null. Without this a guest can send and not act. |
| `joined` | `Func<bool>` | Is there a session. |
| `inWorld` | `Func<bool>` | Is this game loaded into it. Nothing flows before this is true. |
| `now` | `Func<double>` | The session's universe time. The one clock both machines agree on. Zero when not joined. |
| `delay` | `Func<double>` | How far behind the newest state other craft are drawn, 0.15 s today. |
| `host` | `Func<object, bool>` | For a channel opened `host`: is this machine the one writer. |
| `records` | `Func<object, (string, byte[])[]>` | Every world record this channel has. |
| `put` | `Action<object, string, byte[]>` | `(handle, recordKey, payload)`. Empty payload deletes. |
| `canSend` | `Func<string, string, bool>` | `(player, channel)` — has that peer this channel open. |
| `onCraft` | `Action<object, Action<string, string>>` | `(handle, (what, craftKey))` for `gone`, `swept`, `renamed`, `rebuilt`. |
| `hold` | `Func<object, double, bool>` | Ask the room for a simulation speed. See the warp section. |

`modes` on `open` is a comma-separated list of names — `rider`, `slow`, `event`, `world`, `host`,
`soft` — and not a bitmask. A bitmask would be a set of numbers hard-coded in a copied file, and a
stale copy would then open the wrong kind of channel with no key missing and no cast failing for the
degrade rule to catch. Names fail loudly.

`mine` is the one call a guest must respect. It is the whole of the authority model: a guest writes
its own state for a craft it owns and only ever reads for a craft it does not, exactly as this mod
does for physics. A guest that ignores it will fight this mod over the same craft and lose in a way
that looks like neither mod working. It is **eventually consistent**: `Owners` rounds arrive every
five seconds, a client discards a whole round if a page is missing, and `Lapse` can take a craft
away without warning — so a guest's writes may be silently dropped for seconds after a handover,
and a craft's rider state restarts from the new owner's guest when it changes hands.

Three more contracts the table does not express on its own. A handle is checked against the guest
that opened it, and a stale, null or borrowed handle is a no-op, not an exception. A `Vehicle` or
`Part` from `craftOf` or `partAt` is valid **for the duration of the call** and must never be
cached — a rebuild replaces every object. And `send` may be called from anywhere on the main thread;
it queues, and nothing is written to the wire until the frame hook runs.

### The guest's side of it

A dictionary of delegates is correct and unpleasant to use. So this repository also ships a single
file, `extras/KsaMultiplayerApi.cs`, MIT, which a guest author drops into their own project and
which turns the above into:

```csharp
if (Multiplayer.Open("ksarmory:turrets/v1", "rider", mostBytes: 24) is { } turrets)
    turrets.On((from, craft, bytes) => ApplyTurret(craft, bytes));
...
if (turrets is not null && Multiplayer.Mine(key)) turrets.Send(key, PackTurret(v));
```

Compiled into the guest's own assembly, it is not a shared assembly at all: no `mod.toml`
dependency, no version to skew, nothing to load twice. Two guests holding different revisions of
that file is fine, because neither is a type the other sees. The revision check lives in the `api`
int and in which keys are present, which is what a dictionary is good at. Two caveats: this mod must
never compile the shim into its own shipped assembly, and a guest should keep its copy `internal` —
either would let two `Multiplayer` types coexist and bring the ambiguity back.

Two rules the shim must follow, both learned from the one ecosystem that has run this design at
scale. **Every call degrades.** Look each delegate up by key, keep it if the cast succeeds, log once
if it does not, and have every method return a default instead of throwing — because the alternative,
a `KeyNotFoundException` the first time this mod drops a key, is a crash in somebody else's mod that
they cannot fix, and in this loader a crash at the wrong moment is a game that will not start.
**And the version floor is user-visible.** A guest sends `want`; if this mod cannot meet it, it says
so in a line a player will actually read, naming the guest and telling them to update it. Silent
degradation is how a player ends up reporting that KSArmory is broken.

The shim must also carry what none of these mods has: a way to pack bytes. All four serialise with
`System.Text.Json` and only KSACars has a binary codec. The important part is not brevity but
**precision**, and getting it wrong is silent:

- A **position** must be a double. `GolfBall.PositionCcf` is body-centred at ~6.37e6 m, where a
  float resolves to 0.38 m; `IContact.PositionEcl` is heliocentric at ~1.5e11 m, where a float
  resolves to **16 km**. This mod's own `Wire` uses `V3d` for position and `V3f` for velocity for
  exactly this reason.
- A **velocity, angle or fraction** is fine as a float.
- A payload that is JSON by nature should be **gzipped**. `Blob.Gzip` already exists, and for three
  of the real payloads measured below it is the difference between fitting a cap and not.

`Wire`'s own writer is a `private ref struct` over `Vec3`, so this is an extraction rather than a
copy-paste, and `Vec3` compiled into two guests is two types — harmless for bytes, worth saying in
the file.

One claim not to lean on: the house pattern of sharing a file by copying is real — `Bridge.cs`,
`Build.cs` and `Log.cs` exist in all five of these mods — but those are independently written files
sharing a name, not copies that drifted (`Log.cs` is 247, 289, 270 and 270 lines in the guests and
71 here). The shim is different in kind, because its wire-visible behaviour must stay identical
across every copy. The precedent says the distribution method is familiar, not that drift is safe.

## What a guest may hang state on

This mod knows craft, and it knows players. Those are the only two keys it can offer, and it is
worth being exact about what that covers, because the mods on this machine key their state on
several things it cannot name.

**Craft.** A craft's key is the name every peer agrees on, from `keyOf`. It is **not `v.Id`**:
`KeyOf` returns `Renamed.GetValueOrDefault(v, v.Id)`, because the server renames a collision when
two craft share an id. All four mods key on `v.Id` today, so every one of them has re-keying to do,
and the key can change mid-session — hence `renamed` in `onCraft`. KSACars' cars and almost all of
KSArmory hang off a craft, and so does more than it first appears: a kitten is a KSA `Vehicle`, this
mod already owns and syncs kittens, so KSAGolf's ball and the stance its kitten stands in hang off
that kitten's key like anything else.

There is a sharp edge worth naming, because it is a bug waiting rather than an inconvenience.
KSArmory's settings store keys on a display name its own source says is not unique — craft from one
blueprint share an id and so one entry. In one world that is a documented wart. In a shared world it
means two players flying craft of one blueprint share one settings entry, and whoever saved last
decides whether the other's missiles are enabled. `keyOf` fixes it; only re-keying claims the fix.

**The world.** A lift one building has, a set of team names, a damage scale, a course. Not owned by
anybody, and so needing a writer chosen rather than found: the `host` mode below.

**Parts — and the honest answer is that this does not solve it.** KSArmory keys damage, fires,
bullet holes, leaks and engine wear on `KSA.Part` object references through a
`ConditionalWeakTable`. The tempting offer is an index into `v.Parts.Parts`, on the grounds that two
games building one craft from one definition walk the same XML in the same order. Four things break
it, and together they are decisive:

- **The owner is not built from a definition.** Only puppets are: a craft its owner flies is never
  torn down and rebuilt, so its part order is whatever the editor produced. Two non-owners agree
  with each other and neither agrees with the owner — and under the authority model below the owner
  is precisely the machine sending part-keyed state.
- **The index moves when a part leaves**, not only on a rebuild: `PartTree.RemoveTransferredParts`
  compacts the list in place. For a weapons mod a part leaving the craft is the normal case.
- **A rebuild destroys the ledger, not just the index.** Every `Part` instance is replaced, so a
  guest's health, fires, leaks and holes reset to pristine — and this mod rebuilds a puppet as often
  as every five seconds. A `rebuilt` notice tells a guest the index moved; what it must actually
  mean is "everything you had for this craft is gone, send it again".
- **A sub-part cannot be named at all.** A bullet hole is keyed on a child of a top-level part,
  which is not a member of `v.Parts.Parts`, so an index cannot express the one thing holes need.

So `partAt`/`indexOf` stay in the table as a convenience for the cases that tolerate them, and the
design does **not** claim they are enough for damage. The better answer, which this mod can support
without naming a single part, is in the authority section: send the *cause* and let each machine
compute the effect against its own geometry.

## Shape zero: write it into the save — and why it does not work yet

The cheapest service would be the one needing no code here at all, and it nearly exists.

`World.SaveWorld` calls `GameSaves.MakeUncompressedSave`, then hands the result to `Blob.ZipFolder`,
which takes **every file in the save folder, recursively**. The joiner's `LoadWorld` unzips all of it
back and loads it. So a guest that writes its state into KSA's save would have it carried to every
joining player with no knowledge of this mod and none of it in this mod. That is the trick that made
third-party `PartModule` state work in KSP's multiplayer mods for free.

**It does not work today, and two independent things stop it.** `SaveWorld` deletes the handover
folder, makes the save and zips it in one synchronous run, so nothing written on a later frame is in
the zip — and KSArmory's save patch writes nothing when KSA saves: it sets a flag, and the write
happens when its frame loop next runs, by which time the bytes are already on their way. The host's
world zip therefore contains no `KSArmory/systems.json` at all. Not a stale one — none, because the
folder was deleted first.

The receiving half is sound, which is the useful part: unzip restores the whole folder, and
KSArmory's load patch would read the file if it were there. The mechanism is right and the ordering
is wrong.

Three things to fix, in order of how much they buy:

1. **Give guests a write point inside the save.** Either hold the zip until the frame after the save
   so a guest's own frame-loop write lands, or raise a "the world is being saved" signal guests can
   write from. StarMap's in-flight `StarMapAfterSave` hook does not help on its own, because it
   fires when `Make` returns, which is already too late for this call site.
2. **Stop assuming one save name is one world.** Everything lands under the save `Multiplayer`, so a
   player who joins twice is in the same save name both times and a guest that skips a reload when
   the path has not changed — as KSArmory's store does — silently keeps the first join's settings.
3. **Know what is not in a save.** KSArmory's world rules — damage scale, team names, team rules —
   are session state in `Config` and go near no save. Shape zero delivers the per-craft half and
   none of the world half, even once the timing is fixed.

And one guest does the opposite on purpose: KSAStructures patches `Universe.SerializeSystems` to
**strip** its furniture from every save, because a save naming an uninstalled template ends the game
on load. That is the same part-availability problem as stage two, solved in the other direction, and
a design that assumes guests want their state in the save has to expect it.

## The four shapes of guest state, and where each lands on the wire

A guest's state is one of four shapes, and conflating them is how netcode gets expensive. Each rides
machinery this protocol already has. The four came out of reading what the mods on this machine
would actually need to send — and the section after them lists what still fits none of the four,
which is more useful than the four.

### Rider — latest value wins, per craft, small, frequent

A turret angle, a wheel's steering, a kitten's golf stance. It belongs to a craft, the owner is the
only one who may say it, and losing one is harmless because another is along in 50 ms.

This rides **appended to `Wire.State`**, after `Flags`:

    u8  riders
    ... per rider: u32 channel, u8 bytes, the bytes

`channel` is a 32-bit hash of the channel string, which never itself goes on the wire for a rider.
Channel names are `modid:what/v1` — Minecraft's shape, and deliberately not Space Engineers', whose
`ushort` ids collide often enough that the community maintains a public registry of them.

Four reasons this is the right seat and not a new datagram:

- The server already relays a `State` and already drops one from anybody but the craft's owner. The
  authority model is free.
- It is phase-spread and rate-limited already: 20 Hz for the craft being flown, 2 Hz for the rest.
- No extra datagram, so no extra 28 bytes of IP and UDP header per craft per tick.
- A late joiner converges without anything being replayed, because the value is resent.

It is not free of the server, though, and the document should not pretend otherwise: `Broadcast`
rebuilds a `State` through `Wire.Write` rather than forwarding bytes, so `src/Server` has to model
the tail's framing to re-emit it. The rider is the one guest carriage the server is **not** fully
opaque to. It never reads a payload; it does read a count and a length.

**The caps, and where they are enforced.** 128 bytes of rider and 4 riders per craft, with one byte
of cost when a craft has none. The framing allows 255 riders of 255 bytes — 66 kB, which `Wire.Write`
cannot fit in the 8 kB buffer and which `Udp.Send` would swallow silently, stopping that craft dead
for everybody with no log line anywhere. So the cap **must** be a byte budget in `ReadState` and
re-checked in the server's relay, not a rule at `open`: a rule at `open` lives in the one process an
attacker controls.

**Riders ride the craft the player is flying.** Not every owned craft, unless a guest asks for that
by name. `SendOwned` walks every owned craft with no bound, and `Wire`'s own history records sixty
fragments of a broken rocket going out twice a second; riders on all of them is the amplification
hole. Debris needs no turret angles.

**The cost, stated properly.** A `State` is 138 bytes with ordinary names, 166 on the wire. A full
tail is 1 + 4×5 + 128 = **149 bytes**, so a flown craft goes from 20×166 = 3.3 kB/s to 20×315 =
**6.3 kB/s** uplink — not an increment but nearly double, and the rider budget approaches the size
of the state it rides on. Each other player in the room pays that again on downlink. `MaxPlayers`
clamps at 250, where one craft's full tail is 249×20×149 ≈ **740 kB/s** added to every client's
downlink. The cap is a cap because of that number, and a room of more than a few players wants it
lower still. (`Roster` tops out at 64 players while `MaxPlayers` allows 250, so above 64 the
`players` list is simply incomplete. Host election survives it, because the roster is ordered by id.)

**Riders do not go through `Track`, and this is the correction that matters most.** The tempting
design is to let the tail ride along inside the tracked state, interpolated beside the physics.
Three things in `Track` make that wrong:

- `Track.At`'s interpolation returns the **bracketing** state's tail, not the newest, and its
  oldest-state clamp returns a tail up to 16 seconds old — which is what a game catching up reads,
  silently.
- `Track.Add` **drops any state whose universe time equals one already held**. A warp at speed zero
  is a pause, so universe time stops while states keep arriving, and every one after the first is
  discarded. No rider would cross machines while the room is paused — exactly when a player is in a
  menu choosing the thing a slow rider carries.
- A slow rider rides one state in ten, so a guest reading tails out of `Track` would see its value
  blink on and off.

So the mod keeps its own latest-value cache per craft per channel, written when a state arrives, and
offers two accessors with different guarantees: `latest`, the newest thing heard, immune to all
three problems above; and `riderAt`, the tail belonging to the state that craft is currently *drawn*
at, for a guest that needs its value to match the hull it is painted on. A turret can use `latest`
and never notice. A guest driving a simulation from received inputs needs `riderAt`, or it runs this
frame's inputs against a pose from 150 ms ago.

**Send the input, not the result.** KSACars has already solved this and its answer is the one to
copy: it sends throttle, steer, brake and gear — about 13 bytes — and each peer runs its own wheel,
spring and tyre simulation, rather than sending four suspension travels, four wheel spins, four
contact flags and an engine speed. It also returns the pose and the inputs from the *same* pair of
states, which is what `riderAt` exists to make possible here.

**`slow`** is for the shape between a rider and an event: per craft, the owner's, changes once in a
minute, and must still be right for somebody who arrives later. A selected weapon or a paint colour.
It rides one state in ten — 2 Hz on the craft being flown, once every five seconds on the rest — so
a joiner is right within half a second on what somebody is flying and within five on the debris,
with no retransmission, so a lossy link stretches both. Three caveats the design owes a guest:
"every tenth" must be counted per craft and per channel in this mod, not off the per-client
sequence; the period jumps tenfold in both directions when a player changes craft, so the worst case
right after a switch is five seconds on the craft they are now flying; and `latest` returns null
until the first one comes, which is how a guest tells "not told yet" from "told, and it is the
default".

**No rider of any kind reaches a craft nobody owns.** States are only sent for owned craft, and
`Wire`'s own notes record twenty of a world's thirty-seven craft being unowned. A slow rider is sold
as the joiner's answer and covers only what somebody currently owns.

### Event — must arrive, discrete, occasional

A shot fired, a structure placed, a score. Dropping one is a permanent divergence, so this needs the
only reliable delivery the protocol has: a `Link` transfer, which chunks, NAKs the gaps, acks the
whole and never looks inside what it carries.

This rides a new `BlobKind.ModEvent`. Its `Key` is empty and its `Data` is a batch of events each
naming its own channel, because the batching below means one transfer carries several channels.
`Blob.Decode`'s kind whitelist is the line that opens it — and its envelope `Universe` must be sane
or the blob is dropped, so the mod must stamp a real universe time.

**Authority is not free here, unlike for riders.** An event's craft key is inside the opaque
payload, so nothing in the protocol stops client A sending "craft X, which B owns, lost a wing".
The server stamps the sender and the API hands it to the guest as `fromPlayer`; it is then the
guest's job to compare that against `ownerOf` and ignore what does not match. That asymmetry with
riders must be said plainly, because `mine` reads like it covers both and it does not.

**One thing about `Link` forces the design.** A link accepts **4 concurrent inbound transfers** and
silently drops the chunks of a fifth; the sender does not notice and resends the **whole** blob every
two seconds for up to two minutes, so a stuck 16 kB event costs nearly a megabyte before it gives
up. Worse, the 400 kB/s pace is spent per *transfer*, not per link, so N transfers send at N × 400
kB/s. And a world transfer holds a slot for its whole length.

So, two rules on this mod that a guest never sees:

- **One event transfer in flight per peer**, the next going when the last is acked or has given up.
  This needs something `Link` does not have: `Send` returns an id and nothing can ask about it, and
  `Busy` is link-wide and non-zero through every world and craft transfer, so gating on `Busy` would
  block events for most of a session. A per-transfer completion signal is a protocol-layer change
  this plan has to budget.
- **The server batches across senders, per destination.** Its links are per destination, so relaying
  the way craft definitions are relayed would put one transfer per *source* onto one destination
  link — fifteen busy guests, four slots. It needs a bounded pending queue per player, and the caps
  below bound a blob but not that queue.

A batched event is therefore not instant: a round trip at best, and under load as long as the queue.
That is the right trade for something that must not be lost, and a guest that wants "now" and can
afford to lose it wants a rider. A guest that wants both should send both — the rider for the look of
it and the event for the truth of it.

Caps: 4 kB an event, 16 kB a blob, and a per-player byte rate at the server.

### World record — the server keeps it, a joiner is told

Placed structures, a scoreboard, a laid course. The precedent is already written: `BlobKind.Vehicle`
is bytes the server cannot read, which it ownership-checks, stores latest-wins per key, relays,
replays to each joiner and prunes. That whole lifecycle exists; a second kind of opaque record needs
no new ideas.

So `BlobKind.ModWorld`, `Data` = bytes, empty `Data` deletes, and a `Key` of `channel + "/" +
recordKey`. The key limit is not what it looks like: `Blob.Encode` clips to 96 *characters* and then
truncates the UTF-8 to 255 *bytes*, without throwing, so two record keys sharing their first 255
bytes collapse onto one record — `Wire`'s own notes on `Owners` say it best, a count of characters
is not a size. The caps must be in bytes, `Wire.Clip` also strips control characters and trims so a
key is not round-tripped verbatim, and `/` must be forbidden in a record key or `a` + `b/c` and
`a/b` + `c` are the same key.

**It needs a writer rule, and this is the hole the first draft left wide open.** A world record is
by construction not any craft's, so nothing in the design stopped any client overwriting or deleting
every record of every channel — including channels it never opened, since the channel name is only
ever an opaque string inside a key. A token bucket limits the rate of that, not the fact of it. Two
modes, and a channel says which at `open`:

- **`host`** — only the elected host writes. Right for a scoreboard, a lift, a course.
- **`own`** — the server stamps `Blob.Owner` with the sender and refuses a write from anybody else.
  Right for a record each player keeps of their own.

Caps: 64 kB a record, 4 MB a world, refused past that with a line in the log.

**The replay is where this gets dangerous**, and it repeats a mistake the shipped mod already makes.
`OnLoaded` pushes one transfer per craft definition into the joiner's link in a single loop — into
the four slots described above, so the surplus is dropped and resent whole every two seconds. That
is a live defect today, independent of this plan, and a world with many recently-made craft joins
slowly and wastes a great deal of bandwidth for it. Records must be **batched into one blob**, and
the definitions should be too.

Three lifecycle facts to settle rather than discover:

- **A restart loses every record.** This is not "like craft definitions": a definition is a delta
  over the save and is pruned once the save covers it, so losing it loses nothing. A record is the
  only copy of state the save does not hold, which is the whole reason the service exists. A third
  file beside `world.zip` and `world.json` is where it goes, and it belongs in stage one, not later.
- **A snapshot can duplicate state.** Once shape zero works, the same guest state may be inside
  `world.zip` *and* in the record store with no rule for which wins. The rule has to be written
  down: the save is the floor and a record is a delta over it, with the record's universe time
  deciding.
- **`ReloadAll` re-applies every record** over a world that may already contain its effects, so a
  record must be idempotent — a *statement of what is*, never an increment. Say it in the API, or
  structures get placed twice.

### World rider — nobody owns it, so somebody is chosen

A lift's height. A lap count while a race runs. Not a craft's, not a record to keep, and small and
frequent enough that neither of the other two fits. KSAStructures' office lift is the clean example:
one lift, world-global static, and two players pressing different buttons is the obvious way for it
to go wrong.

`Roster` already carries every player's id and whether they are loaded, so **the host is the loaded
player with the lowest id** — the same formula on every machine and at the server, with no new field
and no election. A channel opened `host` is written only by that machine, and the server drops a
write from anybody else as it already drops a `State` from a non-owner.

It rides one new datagram, `Kind.Shared`: owner, channel hash, **a sequence**, length, bytes, at a
rate the guest asks for and 10 Hz by default. The sequence is not optional — every other repeated
datagram here carries one, UDP reorders, and without it a receiver cannot tell a late datagram from
the old host from a fresh one from the new.

**Handover is worse than a stutter, and the fix is to lean on the record store.** Ids are reused —
`Admit` scans up from 1 — so a joiner can take id 1 and become host having heard nothing on the
channel, and would start writing from its own default: the lift jumps to the ground floor the moment
somebody joins. Meanwhile the server's view of who is loaded changes before any client's does, and
the roster is an unacknowledged broadcast, so a lost one leaves a stale machine believing it is host
for up to two seconds with its writes silently discarded.

So a `host` channel's value is **also kept as a world record**, written by the host whenever it
changes. A new host and a new joiner both read the record and continue from it instead of from a
default, and the datagram becomes what it should be: a fast path over durable state, not the only
copy of it.

One thing a `host` channel cannot do: apply a side effect to somebody else's craft. The lift moves
the kittens standing in it, and only a craft's owner may write it — so the host computes the lift's
height and each machine must derive the nudge to its own kitten from the height it received. A
`host` channel implies local re-derivation, and that belongs in the API's documentation.

## Who may join, what is advertised, and what bumps the version

`Wire.Version` is an exact-match gate: a game of another version is named and refused, and the
number moves even when the bytes do not, because it also gates *behaviour*. A guest changing its
payload cannot be allowed to move it, or every player who never installed that guest is locked out.

So the rule this design adds:

> `Wire.Version` covers the envelope. The channel string covers the contents.

A guest versions its payload in its channel name — `ksarmory:turrets/v1` becomes `/v2` — and its
body carries a version byte too, so an added field does not need a rename. `Wire.Version` moves when
the rider tail, the two blob kinds and `Kind.Shared` land, and not for anything a guest does
afterwards.

**The envelope half of that rule is sound. The channel half needs something the first draft
deferred.** `Refusal` compares mod *ids*, so KSArmory 1.0 and 2.0 both join and then skew silently
on a channel name: one writes `/v2`, the other listens on `/v1`, and nobody anywhere learns that the
turrets stopped moving. Identical mod sets do not imply identical channel sets. The same gap makes a
local hash collision unfixable — refusing one guest's channel at `open` because an unrelated guest
loaded first and hashed into the same slot is a failure its author cannot reproduce.

So **channel advertisement is stage one, not stage two**: `Hello` carries the player's channel names,
the server keeps the set per player, and `canSend(player, channel)` answers from it — Minecraft's
design, whose other half the first draft kept and whose negotiation it dropped. That gives a guest
somewhere to put a readable warning, makes a hash collision nameable instead of mysterious, and lets
a guest skip work nobody can use.

## The server should say which mods a world allows

Today it does not say, it *infers*. `_worldMods` is whatever the player who happened to hand over
the world had enabled, kept in `world.json`, and `Refusal` then demands exact set equality in both
directions: you are refused for missing one of them and refused again for having one they lacked.
Three things are wrong with that, and the first is the worst:

- **The policy is set by accident.** Whoever seeds a world decides, for its lifetime, what every
  later player must run. Nobody chose it and nothing but a new world changes it.
- **It is ids only, so a version is invisible.** Two players running different builds of one mod are
  indistinguishable to the server, which is the same hole that lets channel names skew.
- **It is all-or-nothing.** There is no way to say "this one is required", "this one is welcome",
  "this one is not", which is the distinction an operator actually has in mind.

An operator should state it. `mods.json` in `--data`, beside `world.json` and `admins.json`, written
the way `Admins` already writes its file, with console verbs to edit it live and a listing in
`/players`-style output:

    {
      "required": [ { "id": "KSArmory",      "version": "0.10.0" } ],
      "allowed":  [ { "id": "FastAndPurrious", "atLeast": "0.1.12" },
                    { "id": "KSAGolf" } ],
      "forbidden":[ { "id": "SomeCheatMod" } ]
    }

`required` must be present and satisfied; `allowed` may be present; `forbidden` never may; anything
not named in any list is refused, unless `allowed` is absent entirely, which means "anything not
forbidden". With no file at all the server behaves exactly as it does now, inferring from the world,
so nothing in a running deployment changes until somebody writes one.

### Where a version comes from, which is the hard part

**KSA has no concept of a mod version.** `ModEntry` is an id and an enabled flag; the `mod.toml`
`Mod` type has a name and a great many graphics settings and no version field at all — so the one
mod on this machine that declares `version = "0.1.0"` is being ignored by the game entirely. There
is nothing to read, so the mod has to work one out, from three sources in this order:

1. **`version` in `mod.toml`.** Ignored by KSA, read by us. It is the only source that is
   human-authored, the only one that works for an asset-only mod, and asking authors to add one line
   is a cheap convention — this family of mods already shares conventions by agreement rather than
   by code.
2. **The entry assembly's version**, for a StarMap code mod: the DLL named by `[StarMap]
   EntryAssembly`, read with `AssemblyName.Version`. Reliable where it exists — KSArmory reads
   0.10.0 and KSACars 0.1.12 — but it is the *built* version, so a mod whose release tooling stamps
   the version at release time reads `0.0.0` from a developer's build, which two of these four do.
3. **Nothing.** A mod that offers neither can be pinned by id and not by version, and the server
   should say so plainly when an operator tries, rather than silently matching everything.

A fourth source is worth having for a different question: **a hash of the mod's folder** answers "are
we running the same bytes", which is what matters when two players desync and neither knows why. It
is useless in a hand-written pin because it changes on every rebuild, so it belongs in a diagnostic
— the roster, or a console verb — and not in `mods.json`.

### What it costs on the wire

`Hello.Mods` is a comma-separated list of ids in a `LongText(4000)`. Versions make it `id@version`
pairs, which roughly doubles it, and that field is clipped by **characters** rather than bytes — the
hazard `Wire`'s own notes on `Owners` spell out — so it wants a byte budget at the same time.

`Welcome.Refused` is a `Text(200)`, and 200 characters cannot say "you need KSArmory 0.10.0 and have
0.9.1, and FastAndPurrious is not allowed here" for more than one mod at a time. A refusal nobody
can act on is the failure mode this whole feature exists to prevent, so `Refused` should become a
`LongText` in the same change. Both are protocol changes and belong in the single version bump.

### How this changes the rest of the plan

It replaces the weakest idea in it. Letting a guest declare itself `soft` — "my presence does not
change what a save contains" — asks mod authors to be right about something they will sometimes get
wrong, and the failure lands on other players. An operator pinning their own server's mods is the
same decision made by somebody who knows the room, can see what broke, and can change it without a
release. So `soft` stops being a trust decision and becomes a *hint*: a guest says it believes it is
soft, the server shows that in a listing, and the operator decides.

The other half stands and gets better alongside it: a craft using parts a peer lacks should be
skipped rather than the player refused, which is how a server with an `allowed` list stays usable
when somebody turns a mod on mid-session.

## Warp, which this already gets wrong with a guest installed

This is not a gap in the plan so much as a live collision, and it should be fixed before any of the
rest.

`Hooks` patches both `Universe.SetSimulationSpeed` overloads: a write that differs from the held
speed is overwritten with the held speed **and read as that player asking the room to change
speed**. KSArmory's warp policy holds the world's speed down while rounds are in the air, and
`LimitWarpInFlight` defaults to true. So a KSArmory player in a shared world silently fails to slow
their own world *and* sends a warp ask to everybody, every time they fire — which is version 13's
bug exactly ("an old mod asks everybody to pause each time its player opens the vehicle editor"),
recreated by a second mod rather than an old one.

The API therefore needs `hold`, a guest asking the room for a speed through the same vote the panel
uses, and this mod needs to stop reading a *programmatic* speed write as a player's ask. A guest that
has not asked should find the world's speed simply not its to write, and be told so rather than
having its write turned into a request on everyone else's behalf.

## What a guest callback may do

Every delivered callback runs on KSA's main thread, so a guest needs no locking against this mod.
The handshake's `take` is the exception: it runs wherever the guest published, which is why asking
off-thread is forbidden above. Three rules are not negotiable:

- **Do not add, remove, split or stage a vehicle from a callback.** Receives are delivered from
  `Sync.Settle()`'s window in the `RefreshVehiclesInFrame` prefix, the one point in the frame where
  nothing is walking the world's vehicles. Everything else is inside a live enumerator over
  `CurrentSystem.All`, where a change throws `Collection was modified` out of the physics step for
  every craft in the bubble.
- **Do not throw.** A guest's exception must not reach the bus, which would log it against this mod
  with no stack trace, and must not reach StarMap's loader at all.
- **Do not write the physics of a craft that is not yours.** For a craft `mine` says is somebody
  else's, a guest may write what it looks like and must leave its motion to this mod. This is not
  advice: a guest with its own `Vehicle.PrepareWorker` prefix — KSACars has one, for its wheels, and
  so does this mod, for puppets — writes the same state in the same window as `Pull`, with Harmony
  deciding the order and neither mod knowing the other is there. Two mods both driving one craft
  looks like neither of them working.

None of the three is enforced by anything today, and the plan should not pretend otherwise. The
ten-fault switch in `MultiplayerMod` is reachable only from the frame and GUI hooks, sets a private
field that disables the whole mod, and names no guest; the receive window logs and carries on
without counting anything; and `Sync` and `Hooks` are static and cannot reach the instance at all.
So this is work: wrap every call into a guest, attribute the fault to the guest by name, close that
guest's channel after a few faults rather than disabling multiplayer, and make `joined` and `open`
report honestly once the mod has switched itself off — otherwise a guest asking afterwards gets a
live-looking table from a mod that has stopped running.

One ordering rule to keep: `[StarMapAllModsLoaded]` callbacks fire in manifest order, because each
is a Harmony postfix registered as its mod initialises. A guest listed before this mod will call
`open` before `Hooks.Install()` has run. So `open` must be safe before this mod's own setup — it
records the channel and wires it when install happens.

## What this gives KSArmory

KSArmory wrote its own multiplayer plan against KSA's networking — `docs/MULTIPLAYER.md` in that
repository — and then stopped, for a reason stated plainly in it: rounds have nothing to sync
against, because KSA replicates no vehicles, has no shared clock, and a craft the host flies does
not move on anybody else's machine. All three are false here. That document is a list of
requirements for this channel, written before it existed, which makes it the best test of whether
this design is the right shape. Against its seven items:

1. **Round identity.** The channel gives the owner half — a stable name from `me` and `players` — and
   the counter is the guest's. **Fits**, as far as naming a round goes; see the gap below about
   *addressing* one on the wire.
2. **Events, not state.** The event service, and the reason it exists. A Pantsir at 5,000 rpm has
   300 slugs in the air, which is 312 kB/s at 20 Hz, so "fired, with launcher, part, tube, munition,
   aimpoint and time" at 80–120 bytes is the only affordable form. **Fits** — with the caveat that a
   burst's start and stop ride a path that is explicitly not instant.
3. **Outcomes decided on the authority.** Authority here is **per craft**, which is better than the
   global host that document assumed — but the two-event sketch of the first draft was too glib, and
   the right form is narrower. For a **blast**, send the cause: burst point, charge, munition, the
   warhead's velocity and nose. Each machine recomputes fragment cones against its own geometry, so
   **part identity never crosses the wire** and the part-index problem disappears. That is the shape
   to build, and it needs KSArmory's own pending work — "an outcome is data before it is applied" —
   because the blast path today is threaded through a 4,500-line class and cannot be called from
   outside a live weapon system. For a **kinetic** strike it does not work: the hit comes from a
   raycast against the firer's copy of a target drawn 0.15 s in the past, which at 1,000 m/s is 150
   m of displacement, so a CIWS burst that hits on one machine misses on the other. That wants a
   "where was this craft at time t" accessor, which the table does not have and which is the
   standard answer.
   And for a craft **nobody owns** there is no authority at all, so either every machine applies the
   damage locally and the worlds diverge, or none does. Unowned craft are common. This is the real
   limit of per-craft authority and it should not be glossed.
4. **Fire control in one place per craft.** `mine`. **Fits**, and it is one line.
5. **Corrections for long-lived guided rounds.** The shared clock answers the sharpest objection in
   that document — 50 ms is 1.5 km in ecliptic coordinates. But a rider is per *craft*, so eight
   missiles up from one launcher share one payload: 24 bytes each at a 128-byte cap. **Partly**, and
   the missing piece is a per-object key.
6. **State for a joiner.** Magazines and per-system settings as `slow` riders, the team roster and
   the world's damage rules as a `host` channel. `SystemSettings` already has `Differs()`, so it is
   already a dirty-detected payload — but as JSON it measures **531 bytes** against a 128-byte cap,
   and hand-packed about 45. **Fits only packed**, which is why the shim ships a packer.
7. **A decision about the network rule.** None needed, and this is the design's cleanest win.
   KSArmory's CI fails the build if a socket type appears outside one file, and this hands it
   `byte[]` and a callback. The constraint that looked like a problem for its plan is an argument
   for this shape.

Its other document, `docs/SHARED-PICTURE.md`, describes its declared next step — one craft's scope
drawing another craft's tracks — and that **fits none of the four shapes**. A track needs a handle,
position (doubles, for the 16 km reason above), velocity, acceleration, radius, heat and flags: about
60 bytes, times 150 shells in a burst, is **9 kB rebuilt every frame**. That is 70× a rider and
twice an event, and an event is the wrong shape for state recomputed every frame. It is the
strongest single piece of evidence that these four shapes do not yet cover this mod.

## What fits none of the four shapes

Named, because a plan that only lists what works is not a plan:

- **Live projectiles.** Hundreds in flight, not craft, so no craft key — and there is **no
  per-object identity space on the wire at all**. Firing is an event and that works; matching a
  shoot-down or a dud to a specific round across machines, and correcting a guided round mid-flight,
  have nowhere to go.
- **A shared radar picture.** 9 kB a frame, as above.
- **Bullet holes and ground scorch.** 400 holes at ~56 bytes is 22 kB, it changes with every shell,
  and the sub-part a hole hangs on cannot be named.
- **Authority that moves with the action.** Two players on one golf hole: the striker should own the
  ball, and ownership moves with each stroke. `mine` is per craft and the ball is not a craft;
  `host` is the lowest loaded id and does not follow the action.
- **A guest that spawns craft on every machine.** Two of the four mods do it — KSAStructures' chair
  and monitor, KSACars' AI opponents — and the result is not a duplicate but one per player, each
  claimed locally, each renamed by the server, each syncing to everyone else and each costing a
  `State`. A guest needs either "this craft is mine to spawn, suppress it elsewhere" or a rule for
  deciding which machine spawns. It has neither.
- **"Hands off this craft."** KSACars forbids taking control of, boarding or picking up another
  racer's car and had to build that itself. Nothing here stops a player claiming a guest's craft.
- **A rewind.** "Where was this craft at universe time t" — needed for hit arbitration, and `Track`
  already holds the states to answer it.

## Fast & Purrious is the case to build for

KSACars is the mod this channel is for, and it is the one worth designing against, because it is the
only guest here whose state has already been put over a wire and so already has the answers.

**A car is an ordinary KSA `Vehicle`.** So a car already syncs today, unmodified, with no API at all:
its body's position, attitude and velocity go over as any craft's do, and another player would see it
drive. That is the cheapest experiment in this whole document and it needs nothing written — put
KSACars in two games on one server and drive. What would be missing is the layer on top: wheels not
spinning, suspension not moving, headlights not matching, and no road at all on the other machine.

So the question is not whether KSACars should replace its own UDP stack. It should not, and it does
not have to: its stack is how a player hosts a race from their own machine with no server and no
account, which is a thing this mod cannot do and should not try to. What it gains is a **second
mode** — a race inside somebody else's shared world, alongside whatever else is flying there. The
two can coexist, and framing it as a switch is how the requirements get mis-read.

What that mode needs, in the order it will break:

1. **Two mods writing one craft in one window.** KSACars patches `Vehicle.PrepareWorker` to step its
   wheels and springs; this mod patches the same method to pull a puppet to its network position.
   Harmony runs both prefixes with no defined order, and neither mod knows the other exists. This is
   the first thing that goes wrong and nothing in this design addresses it.
   The rule has to be: **for a craft `mine` says is not yours, a guest may write what it looks like
   and must not write its physics.** The owner drives; everyone else is shown. KSACars already has
   the machinery — `Buggies.Hold` drives a remote car as a puppet from received inputs — and under
   this channel that becomes a visual-only derivation, with the body's motion coming from `Pull`.
   This needs more than a sentence in a guest's documentation: it wants a stated frame-order contract
   and probably a Harmony priority, and it is the one place where "a guest writing physics is
   fighting this mod" stops being advice and becomes a thing two of these mods must negotiate.
2. **The five-second rebuild.** `Buggies.Active` is keyed by a `Vehicle` reference and dropped when
   the craft's car part changes; a puppet here is torn down and rebuilt as often as every five
   seconds, so a remote car would lose its drive state on that cycle, permanently. This is the
   ladder problem under another name, it is the deepest blocker in the set, and it is this mod's
   bug rather than the API's.
3. **The road is not in the world.** A circuit lives in `<KSA user dir>/KSACars/Circuits/*.json` and
   beside the DLL, not in the save, and the roads themselves are runtime meshes laid from it. So the
   world zip carries no course and shape zero cannot help. A circuit is 17–19 kB of JSON and 3.4 kB
   gzipped, which fits a world record comfortably: the host puts the circuit in a record, every
   machine lays its own roads from it, and the `Generation` counter it already has is the version. The
   lift's pattern exactly — a `host` channel plus local re-derivation.
4. **The warp lock.** KSACars pins every world's speed for the duration of a race. In a shared world
   that has to become `hold`, and until the warp fix below it is worse than missing: the write is
   swallowed and re-broadcast as that player asking the room to change speed.
5. **Hands off.** Nothing stops a player claiming or boarding another racer's car, or each machine
   adding its own set of AI opponents. KSACars built both rules itself and this channel has no way
   to express either.

Against the shapes, the rest is easy and that is the encouraging part. `DriveInput` — throttle,
steer, brake, gear, lights — is about 13 bytes and is a rider, which is the design's best case: send
the input and let each peer run its own tyres, which is what KSACars already does. It is also the
one guest that *needs* `riderAt` rather than `latest`, because it returns the pose and the inputs
from the same pair of states on purpose, so the wheels spin with the body they belong to. Headlights,
downforce, boost, scoop and hatches are slow riders. A crash, a boarding and a race start are events.
The lap tally is computed locally from poses, as it is now, or a record if it ever needs to be
agreed.

And one thing it would be rid of: `RacePose` exists only because each player lays a course in a
world of their own, so a pose has to be measured against the road rather than the planet. One world
makes ordinary body-fixed coordinates correct, and `RemoteTrack`'s least-lag clock fit is unnecessary
for the same reason. That is a real simplification, and it is the clearest evidence that a shared
world is worth having underneath a racing mod.

## The work, in order

0. **Drive a car in a shared world**, with nothing written. Deploy KSACars into both games of
   `tools/two-games.sh` and see exactly how far an unmodified guest already gets. Everything below
   is a guess until that has been watched once, and three of the blockers above would show up in the
   first minute.
1. **Fix warp**, because it is a present collision with an installed mod rather than preparation: a
   guest's programmatic speed write must stop becoming a warp ask, and `hold` must exist.
2. **Settle who writes a puppet's physics**, as a frame-order contract between this mod and a guest
   patching `Vehicle.PrepareWorker`. Nothing else matters if two mods fight over the same craft.
3. **Make shape zero work**: hold the zip a frame, or signal guests to write. Then check with two
   games whether KSArmory's settings arrive. It buys nothing for KSACars, whose state is not in a
   save, and a good deal for KSArmory.
4. **The bus handshake and the delegate table**, everything refusing, with an example guest in
   `extras/` that opens a channel and logs. Proves the coupling and the load order with no wire
   change. The entry class gains a second constructor taking `IEventBus` — alongside the
   parameterless one, which is what keeps StarMap 0.4.8 working: 0.5.0's `ActivatorUtilities` picks
   the injected constructor and 0.4.8's `Activator.CreateInstance` picks the parameterless one,
   whichever order they are declared. That leans on `ActivatorUtilities` behaviour StarMap's own
   documentation does not promise — it says to use one public constructor — so it wants a test, and
   dropping 0.4.8 remains a reasonable *choice*. It is not a constraint. Worth knowing what the
   0.4.8 failure would otherwise be: not a mod that fails to load but a `MissingMethodException` out
   of StarMap's loader and a game that will not start.
5. **Riders**, the `State` tail, the read-side caps, the two accessors and the latest-value cache.
6. **Events**, `BlobKind.ModEvent`, the batch, the per-destination queue, the `Link` completion
   signal, the server relay and the rate limit.
7. **World records**, `BlobKind.ModWorld`, the two writer modes, the batched join replay, the third
   file on disk, and the idempotence rule.
8. **World riders**, `Kind.Shared` with its sequence, the host rule and its record backing.
   KSAStructures' lift is the test, and a good one, because it is the only shape whose authority
   changes hands.
9. **Channel advertisement and mod pinning**: the channel list in `Hello`, versions beside the mod
   ids, the per-player set at the server, `canSend`, `mods.json` and its console verbs, and
   `Refused` widened enough to name what is wrong. The pinning half stands alone and does not wait
   for the rest of this — it is worth doing whenever a server needs it.
10. **`extras/KsaMultiplayerApi.cs`**, written against the finished thing, and a real guest. A car's
   wheels in KSACars is the test, because it exercises riders, `riderAt`, a world record and the
   puppet rule at once; a turret angle in KSArmory is the smaller version of the same thing.
11. **Then**: skipping a craft whose parts this game lacks; `soft` as a hint an operator can read;
   and only after those have been flown, the gaps above that want new shapes.

Steps 5 to 8 are each a protocol change and should be **one** version bump rather than four, because
nothing in this protocol lets a server tolerate a kind it was not built for.

Step 2 is the one that could grow: a craft rebuilt every five seconds destroys whatever a guest hung
on it, so following a change of shape without tearing the hull down — the ladder problem — is on the
path to a working KSACars rather than beside it.

Two things belong wherever they fit. **The join replay already overruns the transfer limit** without
any of this, so batching craft definitions is a fix worth making on its own. And a `byte[]` field on
`Wire.State` breaks the record's generated equality, which `tests/WireTests.cs` relies on when it
round-trips a state.

## What this does not do

- **It does not give parts a durable identity**, and the index it offers is not good enough for
  damage. Send the cause and recompute the effect.
- **It does not make the server understand anything.** A guest's bytes are a length and a key — with
  the one exception that the server models the rider tail's framing in order to re-emit a state. A
  guest that needs the server to arbitrate would need a server-side plugin. Not in this plan, but
  the shape leaves room and it is not fanciful: DarkMultiPlayer has exactly that, keyed on the same
  string, in a server with no game installed.
- **It does not interpolate, predict or reconcile a guest's state.** The smoothing this mod does for
  position and attitude took `docs/SYNC-SMOOTHNESS.md` to get wrong twice; a guest wanting it must
  do its own, and should read that document first.
- **It does not let two guests share state with each other.** Two mods that know each other should
  use `IModServiceRegistry` and a contract assembly, which is what it is for.
- **It is not a trust boundary.** Any mod can subscribe to the handshake channel, answer first, or
  harvest a guest's callback. Mods already share a process and patch the same game.
- **It cannot stop a guest lying.** `mine` is advice. A guest that sends a rider for a craft it does
  not own is dropped by the server; a guest that sends an *event* about one is not, which is why a
  receiver must check `fromPlayer` itself.
