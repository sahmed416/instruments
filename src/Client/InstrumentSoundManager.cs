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
/// compiler doesn't reference it (confirmed by test-loading this mod
/// against a real 1.22.3 dedicated server — see CLAUDE.md at the repo
/// root), so plain loops are used throughout to stay portable between that
/// path and a normal `dotnet build`.
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

        channel.SetMessageHandler<PerformanceStartPacket>(OnStartPacket);
        channel.SetMessageHandler<PerformanceStopPacket>(OnStopPacket);

        capi.Event.RegisterGameTickListener(Tick, TickIntervalMs);
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

        sound.Start();
        playing[pkt.PlayerUid] = sound;
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

    // -------------------------------------------------------------- tick

    void Tick(float dt)
    {
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

        if (fade) sound.FadeOutAndStop(StopFadeSeconds);
        else sound.Stop();
    }

    void Cleanup(string uid) => playing.Remove(uid);

    /// <summary>Called from the ModSystem on client dispose so no sound leaks past unload.</summary>
    public void Dispose()
    {
        foreach (var uid in new List<string>(playing.Keys)) StopFor(uid, fade: false);
    }
}
