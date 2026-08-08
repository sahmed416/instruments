using System.Collections.Generic;
using Instruments.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace Instruments.Client;

/// <summary>
/// Spec §10. Client-side playback: loads/follows/stops the looping sounds
/// for every performance the client has been told about, and — separately —
/// mirrors <see cref="PerformanceGuard"/> locally for the *local* player so
/// they don't hear their own music survive a lag spike after they act
/// (spec §8.2, §9 "StopRequest").
///
/// No System.Linq here on purpose: Vintage Story's in-game source-mod
/// compiler (used when loading a mod straight from source, no prebuilt
/// DLL) doesn't reference System.Linq, even though it compiles fine under
/// a normal `dotnet build`. Plain loops are used throughout to stay
/// portable between both paths.
/// </summary>
public class InstrumentSoundManager
{
    public const int TickIntervalMs = 100;
    public const int MaxConcurrentSounds = 8; // spec §10.6
    public const float StopFadeSeconds = 0.25f; // spec §10.5, single tunable constant

    readonly ICoreClientAPI capi;
    readonly IClientNetworkChannel channel;
    ItemInstrument itemInstrumentCache;

    /// <summary>Lazy — see the doc comment on <see cref="ItemInstrument.ItemCode"/>.</summary>
    ItemInstrument ItemInstrument => itemInstrumentCache ??= capi.World.GetItem(Instruments.ItemInstrument.ItemCode) as ItemInstrument;

    readonly Dictionary<string, ILoadedSound> playing = new();
    readonly HashSet<string> warnedMissingSoundCodes = new();

    /// <summary>Which jam each currently-playing sound belongs to, so a jam's
    /// local anchor can be dropped once nothing here is using it.</summary>
    readonly Dictionary<string, int> soundJamIds = new();

    /// <summary>
    /// Jam id → this client's OWN ElapsedMilliseconds reading at which that
    /// jam's loop position was 0.
    ///
    /// The server sends an elapsed-time figure, but using it directly for
    /// every sound would defeat the point: two sounds arriving at different
    /// times pick up two different network-latency errors, so they'd be
    /// misaligned *against each other* — which is the only misalignment
    /// anyone can actually hear. Nobody hears two machines' speakers at
    /// once, so cross-client absolute position doesn't matter; what matters
    /// is that every sound in one listener's mix agrees.
    ///
    /// So the server figure is used exactly once per jam, to pin a local
    /// anchor. Every sound afterwards derives its position from that single
    /// local clock reading, making them exactly aligned regardless of
    /// jitter. Latency then only shifts the whole jam uniformly on this
    /// client, which is inaudible.
    /// </summary>
    readonly Dictionary<int, long> jamLocalAnchors = new();

    /// <summary>
    /// Sounds whose seek couldn't be applied at start (length not known
    /// yet), retried on tick. Guards against LoadSound handing back a sound
    /// whose buffer isn't measurable the instant it starts — untested
    /// against a real client, so it fails soft rather than silently
    /// desyncing.
    /// </summary>
    readonly Dictionary<string, int> pendingSeeks = new();

    /// <summary>Local mirror of the server's Performance record — anchor + sit
    /// latch — for the local player only. Null when not performing.</summary>
    class LocalPerformance
    {
        public Vec3d Anchor;
        public bool WasSitting;
    }

    LocalPerformance localPerf;

    public InstrumentSoundManager(ICoreClientAPI capi, IClientNetworkChannel channel)
    {
        this.capi = capi;
        this.channel = channel;

        // If a previous session died mid-performance, the player's music is
        // still sitting at 0. Put it back before anything else.
        if (RestoreMusic())
        {
            capi.Logger.Notification("[instruments] restored game music volume after an unclean shutdown");
        }

        channel.SetMessageHandler<PerformanceStartPacket>(OnStartPacket);
        channel.SetMessageHandler<PerformanceStopPacket>(OnStopPacket);

        capi.Event.RegisterGameTickListener(Tick, TickIntervalMs);
    }

    // ------------------------------------------------- game music ducking

    /// <summary>Vanilla client setting holding music volume, 0–100.</summary>
    const string MusicLevelSetting = "musicLevel";

    /// <summary>
    /// Our own setting holding the music volume saved before ducking, or
    /// <see cref="NotDucked"/> when we hold nothing.
    ///
    /// It exists because there's otherwise no way to tell "the player
    /// deliberately set music to 0" apart from "we set it to 0 and never got
    /// to put it back". Since it persists alongside the value we changed, a
    /// crash mid-song is repaired on next launch instead of silently leaving
    /// someone's music off forever with no clue why.
    ///
    /// Stored as a sentinel rather than deleted between uses because
    /// ISettingsClass exposes Get/Set/Exists but no way to remove a key.
    /// </summary>
    const string SavedMusicLevelSetting = "instrumentsSavedMusicLevel";
    const int NotDucked = -1;

    bool musicDucked;

    /// <summary>
    /// Mutes the game soundtrack while anything is playing here, and puts it
    /// back afterwards. Driven off the tick rather than the start/stop packet
    /// handlers because sounds also disappear via the concurrency cap and via
    /// cleanup of finished/unloaded sounds — one check covers every path.
    ///
    /// Works by moving the player's own music volume setting, which the
    /// engine watches and reacts to live. That also means tracks starting
    /// mid-performance come in already silent, with no polling needed.
    /// </summary>
    void UpdateMusicDucking()
    {
        bool wantDucked = playing.Count > 0;
        if (wantDucked == musicDucked) return;

        if (wantDucked) DuckMusic();
        else RestoreMusic();
    }

    void DuckMusic()
    {
        musicDucked = true;

        int current = capi.Settings.Int.Get(MusicLevelSetting, 0);
        if (current <= 0) return; // music already off — nothing to mute or restore

        capi.Settings.Int.Set(SavedMusicLevelSetting, current, false); // no watchers; internal bookkeeping
        capi.Settings.Int[MusicLevelSetting] = 0;                      // watchers ON so the engine ducks now
    }

    /// <summary>Returns true if it actually put a saved volume back.</summary>
    bool RestoreMusic()
    {
        musicDucked = false;

        int saved = capi.Settings.Int.Get(SavedMusicLevelSetting, NotDucked);
        if (saved < 0) return false; // nothing of ours outstanding

        capi.Settings.Int[MusicLevelSetting] = saved;
        capi.Settings.Int.Set(SavedMusicLevelSetting, NotDucked, false);
        return true;
    }

    // --------------------------------------------------------------- net

    void OnStartPacket(PerformanceStartPacket pkt)
    {
        // Validate before touching any existing sound — a malformed/stale
        // packet with a bad index must not tear down a still-valid,
        // currently-playing sound for this uid with nothing to replace it.
        var defs = ItemInstrument?.Defs;
        if (defs == null || pkt.InstrumentIndex < 0 || pkt.InstrumentIndex >= defs.Length) return;
        var def = defs[pkt.InstrumentIndex];

        StopFor(pkt.PlayerUid, fade: false); // defensive — never leak a sound handle

        var entity = capi.World.GetEntityById(pkt.EntityId);

        var sound = capi.World.LoadSound(new SoundParams
        {
            Location = new AssetLocation(def.Sound),
            ShouldLoop = true,      // performances loop until explicitly stopped
            DisposeOnFinish = false, // required for looping sounds; we manage lifetime ourselves
            Position = entity?.Pos.XYZFloat ?? default,
            RelativePosition = false,
            Range = def.Range,
            ReferenceDistance = 3f,
            Volume = def.Volume,
            SoundType = EnumSoundType.Sound // NOT Music — that's routed through the music slider/ducking
        });

        if (sound == null)
        {
            if (warnedMissingSoundCodes.Add(def.Code))
            {
                capi.Logger.Warning("[instruments] failed to load sound for instrument '{0}' ({1}) — is the .ogg present?", def.Code, def.Sound);
            }
            return;
        }

        // Pin this jam's local anchor the first time we hear of it, then
        // start everything in the jam from that one reading. See the
        // jamLocalAnchors doc comment for why the server figure isn't used
        // directly per sound.
        if (!jamLocalAnchors.ContainsKey(pkt.JamId))
        {
            jamLocalAnchors[pkt.JamId] = capi.World.ElapsedMilliseconds - pkt.JamElapsedMs;
        }

        sound.Start();
        playing[pkt.PlayerUid] = sound;
        soundJamIds[pkt.PlayerUid] = pkt.JamId;

        if (!TrySeekToJamPosition(pkt.PlayerUid, sound, pkt.JamId))
        {
            pendingSeeks[pkt.PlayerUid] = pkt.JamId;
        }

        EnforceConcurrencyCap();

        bool isLocalPlayer = pkt.PlayerUid == capi.World.Player?.PlayerUID;
        if (isLocalPlayer && entity is EntityAgent selfAgent)
        {
            localPerf = new LocalPerformance
            {
                Anchor = selfAgent.Pos.XYZ.Clone(),
                WasSitting = PerformanceGuard.IsSitting(selfAgent)
            };

            // Fallback for spec §11 point 2: the server replicated the 3rd-person
            // "knifestab" code, which is what other clients see. That replication
            // may not by itself make the LOCAL first-person view show anything —
            // re-issuing that same code here would be a no-op (IsAnimationActive
            // on it is already true by the time this handler runs, since the
            // replicated state landed first). So explicitly (re-)start the
            // "-fp" variant on our own local copy of the entity instead — this
            // only affects how *we* render our own view; other clients keep
            // seeing the server-driven 3rd-person animation independently.
            // Deliberately unconditional (not guarded by IsAnimationActive):
            // this runs once per performance start, so it can't spam, and it
            // must win over whatever "knifestab" replication already set.
            selfAgent.AnimManager.StartAnimation(new AnimationMetaData
            {
                Animation = AnimConstants.AnimationCodeFp,
                Code = AnimConstants.RunCode,
                AnimationSpeed = 1f,
                EaseInSpeed = 3f,
                EaseOutSpeed = 3f,
                Weight = 1f,
                BlendMode = EnumAnimationBlendMode.AddAverage
            }.Init());
        }
    }

    void RetryPendingSeeks()
    {
        foreach (var uid in new List<string>(pendingSeeks.Keys))
        {
            int jamId = pendingSeeks[uid];
            if (!playing.TryGetValue(uid, out var sound) || sound.IsDisposed)
            {
                pendingSeeks.Remove(uid);
                continue;
            }
            if (TrySeekToJamPosition(uid, sound, jamId)) pendingSeeks.Remove(uid);
        }
    }

    void OnStopPacket(PerformanceStopPacket pkt)
    {
        StopFor(pkt.PlayerUid, pkt.Fade);

        if (pkt.PlayerUid == capi.World.Player?.PlayerUID)
        {
            localPerf = null;
            var selfAgent = capi.World.Player?.Entity;
            if (selfAgent != null && selfAgent.AnimManager.IsAnimationActive(AnimConstants.RunCode))
            {
                selfAgent.AnimManager.StopAnimation(AnimConstants.RunCode);
            }
        }
    }

    /// <summary>
    /// Seeks a sound to where the jam currently is, so it drops in on the
    /// beat rather than from the top of its track. Returns false if the
    /// sound can't report its length yet, in which case the caller should
    /// retry — see <see cref="pendingSeeks"/>.
    ///
    /// The modulo happens here, against the real loaded sound, because the
    /// server has no idea how long any .ogg is. Tracks of different lengths
    /// therefore still share one origin; they just wrap at different rates.
    /// </summary>
    bool TrySeekToJamPosition(string uid, ILoadedSound sound, int jamId)
    {
        if (!jamLocalAnchors.TryGetValue(jamId, out long localAnchorMs)) return true; // nothing to sync to

        float lengthSec = sound.SoundLengthSeconds;
        if (lengthSec <= 0) return false;

        double elapsedSec = (capi.World.ElapsedMilliseconds - localAnchorMs) / 1000.0;
        double pos = elapsedSec % lengthSec;
        if (pos < 0) pos += lengthSec;

        sound.PlaybackPosition = (float)pos;
        return true;
    }

    // -------------------------------------------------------------- tick

    void Tick(float dt)
    {
        if (pendingSeeks.Count > 0) RetryPendingSeeks();

        // Snapshot the keys — StopFor()/Cleanup() mutate `playing` mid-loop.
        var uids = new List<string>(playing.Keys);
        foreach (var uid in uids)
        {
            if (!playing.TryGetValue(uid, out var sound)) continue; // already removed this tick
            if (sound.IsDisposed || sound.HasStopped) { Cleanup(uid); continue; }

            var e = capi.World.PlayerByUid(uid)?.Entity;
            if (e == null || !e.Alive)
            {
                // Out of loaded range (or dead) — beyond hearing distance either way.
                StopFor(uid, fade: false);
                continue;
            }
            sound.SetPosition(e.Pos.XYZFloat);
        }

        EnforceConcurrencyCap();
        TickLocalGuard();

        // Last, so it sees the final set of sounds for this tick.
        UpdateMusicDucking();
    }

    /// <summary>
    /// Mirrors <see cref="PerformanceGuard"/> on the local player at the same
    /// cadence as the server tick (spec §8.2) so a lag spike doesn't leave
    /// the performer hearing their own music after they've already acted.
    /// </summary>
    void TickLocalGuard()
    {
        if (localPerf == null) return;

        var entity = capi.World.Player?.Entity;
        if (entity == null) { LocalStop(); return; }

        // Mirrors the same gap-fix on the server tick (§8.2): dropping the
        // instrument doesn't fire a slot-changed event (that only fires on
        // switching to a different slot index), so check directly.
        if (capi.World.Player?.InventoryManager?.ActiveHotbarSlot?.Itemstack?.Collectible is not ItemInstrument _)
        {
            LocalStop();
        }
        else if (PerformanceGuard.AnyDisallowedInput(entity.Controls))
        {
            LocalStop();
        }
        else if (PerformanceGuard.HasMoved(entity, localPerf.Anchor))
        {
            LocalStop();
        }
        else if (localPerf.WasSitting && !PerformanceGuard.IsSitting(entity))
        {
            LocalStop();
        }
        else if (!localPerf.WasSitting && PerformanceGuard.IsSitting(entity))
        {
            localPerf.WasSitting = true;
            localPerf.Anchor = entity.Pos.XYZ.Clone();
        }
    }

    void LocalStop()
    {
        localPerf = null;
        string myUid = capi.World.Player?.PlayerUID;
        if (myUid != null) StopFor(myUid, fade: true); // don't wait for the server round-trip
        channel.SendPacket(new StopRequest()); // hint — server owns the real state (§9)
    }

    /// <summary>Spec §10.6 — cap concurrently playing sounds, keeping the nearest.</summary>
    void EnforceConcurrencyCap()
    {
        if (playing.Count <= MaxConcurrentSounds) return;

        var selfPos = capi.World.Player?.Entity?.Pos?.XYZ;
        if (selfPos == null) return;

        var uids = new List<string>(playing.Keys);
        uids.Sort((a, b) => SquareDistanceOf(b, selfPos).CompareTo(SquareDistanceOf(a, selfPos))); // farthest first

        int excess = uids.Count - MaxConcurrentSounds;
        for (int i = 0; i < excess; i++)
        {
            StopFor(uids[i], fade: true);
        }
    }

    double SquareDistanceOf(string uid, Vec3d fromPos)
    {
        var e = capi.World.PlayerByUid(uid)?.Entity;
        return e == null ? double.MaxValue : e.Pos.XYZ.SquareDistanceTo(fromPos);
    }

    // ------------------------------------------------------------- exit

    /// <summary>Idempotent — safe to call on a uid that isn't playing (§8.3, §12).</summary>
    void StopFor(string uid, bool fade)
    {
        if (!playing.TryGetValue(uid, out var sound)) return;
        playing.Remove(uid);
        ForgetSoundJam(uid);

        if (fade) sound.FadeOutAndStop(StopFadeSeconds);
        else sound.Stop();
    }

    void Cleanup(string uid)
    {
        playing.Remove(uid);
        ForgetSoundJam(uid);
    }

    /// <summary>
    /// Drops a sound's jam association, and the jam's local anchor too once
    /// nothing on this client is playing in that jam any more. Keeping the
    /// anchor alive while *any* member is still audible is what lets a
    /// player stop and restart — or switch instruments — and come back in on
    /// the beat with whoever else is still going.
    /// </summary>
    void ForgetSoundJam(string uid)
    {
        pendingSeeks.Remove(uid);
        if (!soundJamIds.TryGetValue(uid, out int jamId)) return;
        soundJamIds.Remove(uid);

        foreach (var kv in soundJamIds)
        {
            if (kv.Value == jamId) return; // someone's still in this jam
        }
        jamLocalAnchors.Remove(jamId);
    }

    /// <summary>Called from the ModSystem on client dispose so no sound leaks past unload.</summary>
    public void Dispose()
    {
        foreach (var uid in new List<string>(playing.Keys)) StopFor(uid, fade: false);
        // Unloading the mod must not leave the player's music muted.
        RestoreMusic();
    }
}
