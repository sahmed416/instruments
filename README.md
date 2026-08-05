# outerwilds repo — Instruments mod

This repo holds one project: **`instruments/`**, a Vintage Story code mod
that lets players perform music with a held instrument item, audible
positionally to nearby players, synced across multiplayer. The original
design spec is [`instruments-mod-spec.md`](instruments-mod-spec.md) at the
repo root — it's a good read for the *intended* behavior and rationale,
but treat it as a starting point, not ground truth: several of its
assumptions about vanilla asset paths and API shapes turned out to be
wrong once checked against the real game (see "Corrections to the spec"
below), and a few of its design decisions have since been deliberately
overridden at the user's request (see "Deviations" below).

## Build

```bash
cd instruments
dotnet build -c Release
```

A .NET 10 SDK is required (`dotnet --list-sdks` should show a `10.x`).
`VINTAGE_STORY` should point at the game install
(`C:\Users\User\AppData\Roaming\Vintagestory` on this machine) — the
csproj falls back to that exact path if the env var isn't set, so on this
machine you don't need to set it.

Build output stages a complete, ready-to-copy mod folder at
`instruments/dist/instruments/` (gitignored) — DLL, `modinfo.json`,
`modicon.png`, and `assets/`, everything needed. **Nothing outside the
repo is ever written automatically** — copying `dist/instruments` into
the game's actual `Mods` folder is a manual, deliberate step:

```bash
cp -r instruments/dist/instruments "$APPDATA/VintagestoryData/Mods/"
```

Don't add that copy back into the build (a version once had a post-build
step that auto-copied to `%AppData%`; it was removed on request — writes
outside the working directory should stay opt-in).

## Verifying changes without a graphical client

This environment has no way to launch the actual game client, which means
no way to see rendering, hear audio, or watch animations directly. Two
things stand in for that:

1. **`dotnet build`** is the real compiler now (SDK is installed) — trust
   its errors/warnings fully for C# correctness.
2. **The dedicated server, run headless, as a compile+asset-parse check**:
   ```bash
   VintagestoryServer.exe --dataPath <scratch-dir> --addModPath <repo-root> --port <free-port>
   ```
   This catches JSON syntax errors, bad shape/texture paths that don't
   resolve *server-side*, and C# compile errors (VS can compile a mod
   straight from source via its bundled Roslyn — no SDK needed for this
   specific check, which is how it was first done before an SDK was
   available here). **It cannot catch client-only rendering problems** —
   texture/shape resolution failures that only matter for the visual
   pipeline won't show up here. Use a fresh `--dataPath` each run (stale
   savegame locks cause spurious startup crashes), and use a non-default
   `--port` if a real server might already be running.

For anything that needs actual rendering/hearing/animation confirmed, the
only way is asking the user to test and share what they see, or read their
`client-main.log` (`%AppData%\VintagestoryData\Logs\client-main.log`) —
asset resolution warnings ("Did not find required shape...", "Texture
asset ... not found") show up there and nowhere server-side-visible.

## Architecture

- `src/ItemInstrument.cs` — the item class. Instrument list is data-driven
  from the item JSON's `attributes.instruments` array, not hardcoded.
- `src/PerformanceGuard.cs` — the shared stop-condition predicate
  (disallowed-input check, movement check, sitting check). Used verbatim
  by both the server tick and the client's local mirror — if these two
  ever diverge you get desync bugs that only reproduce under latency.
- `src/Server/InstrumentServerState.cs` — server-authoritative state
  machine: network handlers, the periodic stop-condition tick, the
  watchdog. `StopPerformance` is the single exit path every stop
  condition routes through.
- `src/Client/InstrumentSoundManager.cs` — client playback: loads/follows/
  fades sounds for every performance the client's been told about, plus a
  *local* mirror of the guard predicate for the performer's own client (so
  they don't hear their own music survive a lag spike after they act).
- `src/Network/Packets.cs` — protobuf message contracts.
- `assets/instruments/itemtypes/instrument.json` — item definition. The
  held model is vanilla's own flute shape/textures, **copied into this
  mod's own domain** rather than referenced cross-domain (see gotchas).

## Deviations from the spec (deliberate, at user request)

- **No dedicated stop hotkey.** The spec had one (G=play/stop toggle,
  H=next, J=stop) purely as a desync escape hatch. Removed — several
  other actions already stop a performance (drop the item, move, switch
  slots), and a third key for the toggle's own job wasn't worth it. Only
  `instrument_play` (G) and `instrument_next` (H) exist now. The
  `StopRequest` packet type itself is still used — the client's local
  guard still sends it automatically on a detected stop condition — only
  the manual hotkey path to it is gone.
- **Seamless instrument switching.** The spec had "next while performing"
  stop and require a second keypress to resume, to avoid spamming song
  intros. Changed: next while performing now stops (hard cut) and
  immediately restarts on the new instrument, bounded by the existing
  250ms rate limit instead of a mandatory second press.
- **Held model is the vanilla flute, not the knife.** The spec's own
  skeleton assumed the knife model; that was tried first, didn't render
  (see gotchas), and was replaced by reusing vanilla's own `instrument`
  item (a flute, `assets/survival/itemtypes/utility/instrument.json`) —
  only its shape/textures, not its class or right-click animations.

## Gotchas learned the hard way (read before touching related code)

- **Don't reference another domain's assets directly (`survival:...` from
  a mod named `instruments`).** This was tried twice — once with an
  incomplete texture override, once with a `modinfo.json` dependency to
  fix load order — and both still failed to resolve in-game
  (`client-main.log`: "Did not find required shape ... anywhere") for
  reasons never fully pinned down even after ruling out load order, stale
  deploys, and casing. **Fix that actually worked: copy the needed shape +
  texture files into this mod's own `assets/instruments/` and reference
  them with bare (unprefixed) paths.** Do this from the start for any
  future vanilla-asset reuse rather than rediscovering this.
- **The in-game Roslyn source-mod compiler doesn't reference
  `System.Linq`**, even though it compiles fine under a normal SDK build.
  If loading this mod as loose source into a real game/server (not via
  `dotnet build`) is ever relevant again, LINQ will fail with `CS1061`
  errors there even though `dotnet build` is clean. The codebase currently
  avoids LINQ entirely (plain loops, `List<T>.Sort`) specifically to stay
  portable between both paths — keep it that way unless the loose-source
  path stops mattering.
- **`AfterActiveSlotChanged` only fires on switching to a *different* slot
  index** — it does not fire when the *contents* of the currently active
  slot change (e.g. dropping the item leaves the same slot selected, now
  empty). Any "is the player still holding X" invariant needs an active
  poll (both tick loops here do this), not just that event hook.
- **A member/property named the same as a type breaks bare type-pattern
  matching.** `InstrumentServerState` and `InstrumentSoundManager` both
  have a property called `ItemInstrument` (returns the `ItemInstrument`
  type). Writing `x is not ItemInstrument` (no capture) inside either
  class fails with `CS9135` — the compiler prefers the property and treats
  it as a constant pattern. Always add a capture, even a discard:
  `is not ItemInstrument _`.
- **`core.autocrlf = true` on this repo.** After an external tool (Visual
  Studio, a linter) rewrites a file, `git status` can show it as
  "modified" purely from CRLF vs. the repo's LF-stored blobs, even when
  `git diff`/`git diff --staged` show zero real changes. Trust `git diff`
  over `git status` if this happens — it's cosmetic, not a real edit
  lingering.
- **Git doesn't track empty directories.** Abandoned experiments (e.g. a
  reverted model swap) can leave hollow directory trees behind after the
  files inside are deleted. Worth an occasional
  `find . -type d -empty -delete` sweep, excluding `.git`, `bin`, `obj`,
  `dist`, `.vs`.

## Corrections to the spec (verified against the real 1.22.3 install)

- Vanilla knife/flute assets live in the **`survival`** domain, not
  `game` as the spec's skeleton JSON assumed.
- The spec's §9 channel-registration code sample omits `StopRequest` from
  the `RegisterMessageType<...>()` chain even though the prose clearly
  needs it sent client→server. All five message types are registered here
  (`TogglePlayRequest, NextInstrumentRequest, StopRequest,
  PerformanceStartPacket, PerformanceStopPacket`).
- §12's edge-case table claims "drops/stores the instrument → Stop (slot
  change covers it)" — false, see the `AfterActiveSlotChanged` gotcha
  above. Fixed with an active poll instead.

## Still unverified / open

No graphical client has ever confirmed these — they're implemented per
spec but not watched/heard directly:

- Whether the server-driven third-person performance animation replicates
  correctly to other clients, and whether the performer sees anything in
  first person (spec §11). A client-side fallback exists
  (`AnimConstants.AnimationCodeFp`) but its necessity was never confirmed.
- Whether the five placeholder `.ogg` files (ffmpeg-generated sine tones)
  actually loop seamlessly — never tested on a 2-minute repeat as §13
  asks. Treat them as wiring-only placeholders, not final assets.
- The full §15 test matrix (multi-client positional audio, the §12
  stop-condition table row by row, abuse/spam testing) — needs two real
  clients, which this environment can't provide.
