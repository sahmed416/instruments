# Instruments

A [Vintage Story](https://www.vintagestory.at/) mod that lets players
perform music together. Hold an instrument, start playing, and anyone
nearby hears it — positionally, in real time, synced across multiplayer.

## Features

- **Seven instruments in one item** — Banjo, Harmonica, Flute, Drums,
  Whistle, Bow, and Piano. Cycle between them with a hotkey; no inventory
  clutter.
- **Positional audio** — louder up close, fades out as listeners move
  away, following the performer if they're seated or just looking around.
- **Built for multiplayer** — server-authoritative from the ground up.
  Multiple players can perform different songs at once, and everyone
  hears the mix.
- **Two keys, nothing to memorize** — play/stop and switch instrument.
  Everything else that stops a performance (walking, mining, dropping the
  item, ...) is just the normal thing you'd already do.

## How it works

Hold the instrument in your active hotbar slot to enable two bindable
keys (defaults shown — rebindable in the normal Controls menu):

| Key | Action |
|---|---|
| **G** | Play / Stop (toggle) |
| **H** | Next instrument |

Press **G** to start — the performance loops until you stop it. While
playing you're free to **look around** and **sit down**; almost anything
else (walking, jumping, sneaking, mining, attacking, using an item,
switching hotbar slots, dropping the instrument, standing back up, dying,
disconnecting) stops it immediately. Press **H** mid-performance to switch
songs — it cuts to the new instrument right away, no need to press play
again.

A player who walks into range mid-performance won't hear anything until
the performer starts again — there's no catching a song partway through.

There's no crafting recipe yet. Grab the item from the creative inventory
(search "instrument"), or with cheats enabled:

```
/giveitem instruments:instrument 1 <yourname>
```

## Installation

1. Download the mod, or [build it yourself](#building-from-source).
2. Drop the `instruments` folder into your `Mods` directory:
   - Windows: `%AppData%\VintagestoryData\Mods\`
   - Linux: `~/.config/VintagestoryData/Mods/` (or wherever your data
     folder lives)
3. Launch the game — it should show up as "Instruments" in the Mod
   Manager.

Requires Vintage Story **1.22.3** or later.

## Building from source

Requires:
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A local Vintage Story install (the build references its DLLs directly)

```bash
dotnet build -c Release
```

The project looks for the game at
`C:\Users\User\AppData\Roaming\Vintagestory` by default — set a
`VINTAGE_STORY` environment variable (or edit the fallback in
`instruments.csproj`) if yours lives somewhere else.

A successful build stages a complete, ready-to-install mod folder at
`dist/instruments/` — DLL, `modinfo.json`, `modicon.png`, and all
`assets/` — which you can copy straight into your `Mods` directory as
described above. Nothing is copied there automatically as part of the
build.

## Project structure

```
instruments/
├─ modinfo.json                 # mod metadata: id, version, game dependency
├─ modicon.png                  # icon shown in the Mod Manager
├─ instruments.csproj           # build configuration
├─ assets/instruments/
│  ├─ itemtypes/instrument.json     # the item: model, textures, instrument list
│  ├─ lang/en.json                  # display names, hotkey labels, tooltips
│  ├─ shapes/, textures/            # the held model
│  └─ sounds/instrument/            # one .ogg per instrument
└─ src/
   ├─ InstrumentsModSystem.cs           # entry point: registration, hotkeys, network channel
   ├─ ItemInstrument.cs                 # the item class
   ├─ InstrumentDef.cs                  # per-instrument definition (sound, volume, range)
   ├─ PerformanceGuard.cs               # shared "is this player quietly performing" check
   ├─ Network/Packets.cs                # client↔server message contracts
   ├─ Server/InstrumentServerState.cs   # authoritative performance state machine
   └─ Client/InstrumentSoundManager.cs  # client-side sound playback
```

## Design notes

Code comments throughout `src/` cite section numbers from the original
design spec this mod was built against (e.g. "spec §8.2"). That document
isn't included in this repo — the citations are kept as stable references
to *why* a given piece of behavior exists, even without the source text on
hand; the surrounding comment always explains the reasoning in full
regardless.

The server owns all performance state — clients only ever send empty
requests ("toggle play", "next instrument") and react to what the server
broadcasts. Broadcasts go only to players within hearing range of the
performer, never the whole server, and there's deliberately no
listener-tracking or late-join sync — that keeps the server-side
bookkeeping to a single dictionary of active performances plus a
per-player position anchor.

Stop conditions are an allow-list, not a block-list: a small shared
predicate (`PerformanceGuard`) defines the narrow set of things allowed
mid-performance (looking around, sitting down) and treats everything else
as a reason to stop, checked identically on both the server and the
performer's own client. New actions added by future game updates fail
closed by default instead of silently being allowed through.

## Known limitations

- No crafting recipe yet — creative inventory or `/giveitem` only.
- One song per instrument; no note-by-note or MIDI-style play.
- Only a single instrument is available at this time.

## Contributing

PRs welcome. This is fundamentally a multiplayer feature, so testing with
two real clients (not just singleplayer) matters more than usual here —
please do that before submitting anything touching the state machine or
networking.
