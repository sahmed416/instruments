using System.Collections.Generic;
using Instruments.Network;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

// No System.Linq here on purpose: Vintage Story's in-game source-mod
// compiler (used when loading a mod straight from source, no prebuilt DLL)
// doesn't reference System.Linq, even though it compiles fine under a
// normal `dotnet build`. Plain loops are used throughout to stay portable
// between both paths.

namespace Instruments.Server;

/// <summary>
/// Spec §5.3. Per-player performance state, server-authoritative. Not
/// persisted — lives only in this dictionary for the process lifetime, so
/// "cleared on server shutdown" (§5.3) is automatic; disconnect and death
/// are the two cases that need an explicit hook while the process keeps
/// running (below).
/// </summary>
public class Performance
{
    public string PlayerUid;
    public long EntityId;
    public int InstrumentIndex;
    public long StartedAtMs;
    public Vec3d Anchor;   // position at start; movement away from this stops playback
    public int SlotIndex;  // active hotbar slot at start
    public bool WasSitting; // latch — see spec §8.2 "Sitting"
    public int JamId;      // which jam session this performance belongs to

    /// <summary>
    /// Players who have already been sent this performance's start packet,
    /// so the late-join scan doesn't re-send it every tick. Players who
    /// wander well clear are dropped from here so that returning re-arms
    /// them. Bounded by "players near this performer", and dies with the
    /// performance.
    /// </summary>
    public readonly HashSet<string> NotifiedUids = new();
}

/// <summary>
/// A group of performers whose loops are aligned to one shared clock, so
/// they sound like they're playing together rather than each starting from
/// the top of their own track.
///
/// Modelled as an explicit group with a single clock origin rather than
/// "copy the position of whoever's nearby" on purpose: with pairwise
/// copying, a third player syncing to a second who synced to a first
/// compounds each hop's error. Referencing one origin means the error never
/// accumulates no matter how many people join.
/// </summary>
public class JamSession
{
    public int Id;
    /// <summary>Server ElapsedMilliseconds at which this jam's loop position was 0.</summary>
    public long AnchorMs;
    public readonly HashSet<string> Members = new();
}

/// <summary>
/// Spec §8, §9. Owns the state machine, the network handlers, the periodic
/// stop-condition tick, and the watchdog. <see cref="StopPerformance"/> is
/// the single exit path every stop condition funnels through (§8.1, §10.5).
/// </summary>
public class InstrumentServerState
{
    public const long RateLimitMs = 250;
    public const long WatchdogMs = 10 * 60 * 1000; // 10 minutes, spec §10.5
    public const int TickIntervalMs = 100;

    /// <summary>
    /// How often to look for players who have wandered into earshot of an
    /// ongoing performance. Slower than the stop-condition tick because
    /// nobody covers meaningful ground in half a second, and this one scans
    /// every online player against every active performance.
    /// </summary>
    public const int ListenerScanEveryNTicks = 5; // ~500ms

    /// <summary>
    /// Earshot multipliers for the late-join scan. Notifying slightly beyond
    /// the audible range means the sound is already running (inaudibly) by
    /// the time someone is close enough to hear it, so it fades in rather
    /// than popping. The wider forget threshold is hysteresis — without the
    /// gap, someone standing exactly on the boundary would be dropped and
    /// re-sent over and over.
    /// </summary>
    public const float NotifyRangeFactor = 1.5f;
    public const float ForgetRangeFactor = 2.0f;

    readonly ICoreServerAPI sapi;
    readonly IServerNetworkChannel channel;
    ItemInstrument itemInstrumentCache;

    /// <summary>Lazy — see the doc comment on <see cref="ItemInstrument.ItemCode"/>.</summary>
    ItemInstrument ItemInstrument => itemInstrumentCache ??= sapi.World.GetItem(Instruments.ItemInstrument.ItemCode) as ItemInstrument;

    readonly Dictionary<string, Performance> active = new();
    readonly Dictionary<string, long> lastToggleMs = new();
    readonly Dictionary<string, long> lastNextMs = new();

    readonly Dictionary<int, JamSession> jams = new();
    int nextJamId = 1;
    int tickCounter;

    public InstrumentServerState(ICoreServerAPI sapi, IServerNetworkChannel channel)
    {
        this.sapi = sapi;
        this.channel = channel;

        channel.SetMessageHandler<TogglePlayRequest>(OnToggleRequest);
        channel.SetMessageHandler<StopRequest>(OnStopRequest);
        channel.SetMessageHandler<NextInstrumentRequest>(OnNextRequest);

        sapi.Event.AfterActiveSlotChanged += OnSlotChanged;
        sapi.Event.PlayerDeath += OnPlayerDeath;
        sapi.Event.PlayerDisconnect += OnPlayerDisconnect;

        sapi.Event.RegisterGameTickListener(Tick, TickIntervalMs);
    }

    // ---------------------------------------------------------------- net

    void OnToggleRequest(IServerPlayer fromPlayer, TogglePlayRequest packet)
    {
        string uid = fromPlayer.PlayerUID;

        if (RateLimited(lastToggleMs, uid)) return;

        if (active.ContainsKey(uid))
        {
            StopPerformance(uid, "key");
            return;
        }

        var slot = fromPlayer.InventoryManager?.ActiveHotbarSlot;
        if (slot?.Itemstack?.Collectible is not ItemInstrument item) return;

        var entity = fromPlayer.Entity;
        if (entity == null) return;

        // Start gate, spec §8.3 — reject rather than start-then-immediately-kill.
        if (PerformanceGuard.AnyDisallowedInput(entity.Controls))
        {
            fromPlayer.SendIngameError("instrumentstartrejected", Lang.Get("instruments:instrument-start-rejected"));
            return;
        }

        StartPerformance(fromPlayer, entity, slot, item);
    }

    /// <summary>
    /// Actually begins a performance — caller is responsible for the start
    /// gate (§8.3) and for making sure the player isn't already active.
    /// Shared by <see cref="OnToggleRequest"/> and <see cref="OnNextRequest"/>
    /// (switching instruments mid-performance restarts through here too).
    ///
    /// <paramref name="inheritJamId"/> forces the performance into an
    /// existing jam instead of searching for one by proximity. Used by the
    /// instrument-switch path so switching mid-song keeps you in time with
    /// whoever you were playing with — and, for a solo player, doesn't
    /// restart their loop from the top.
    /// </summary>
    void StartPerformance(IServerPlayer fromPlayer, EntityAgent entity, ItemSlot slot, ItemInstrument item, int inheritJamId = -1)
    {
        string uid = fromPlayer.PlayerUID;

        var def = item.CurrentDef(slot.Itemstack);
        if (def == null) return;

        var pos = entity.Pos.XYZ.Clone();

        JamSession jam;
        if (inheritJamId >= 0 && jams.TryGetValue(inheritJamId, out jam))
        {
            jam.Members.Add(uid);
        }
        else
        {
            jam = FindOrCreateJam(uid, pos);
        }

        var perf = new Performance
        {
            PlayerUid = uid,
            EntityId = entity.EntityId,
            InstrumentIndex = item.GetInstrumentIndex(slot.Itemstack),
            StartedAtMs = sapi.World.ElapsedMilliseconds,
            Anchor = pos,
            SlotIndex = fromPlayer.InventoryManager.ActiveHotbarSlotNumber,
            WasSitting = PerformanceGuard.IsSitting(entity),
            JamId = jam.Id
        };
        active[uid] = perf;

        var recipients = RecipientsInRange(perf.Anchor, def.Range);
        channel.SendPacket(BuildStartPacket(perf), recipients);
        foreach (var r in recipients) perf.NotifiedUids.Add(r.PlayerUID);

        StartAnimation(entity);
    }

    void OnStopRequest(IServerPlayer fromPlayer, StopRequest packet)
    {
        // Deliberately not rate-limited (spec §9): spurious stops from a
        // client that already stopped locally are expected and harmless.
        if (active.ContainsKey(fromPlayer.PlayerUID))
        {
            StopPerformance(fromPlayer.PlayerUID, "key");
        }
    }

    void OnNextRequest(IServerPlayer fromPlayer, NextInstrumentRequest packet)
    {
        string uid = fromPlayer.PlayerUID;
        if (RateLimited(lastNextMs, uid)) return;

        // Next while performing seamlessly switches: stop the current
        // instrument (hard cut, no fade — about to restart immediately, so
        // a fade would just overlap oddly with the new instrument's sound)
        // and pick back up on the new one right away, rather than requiring
        // a second keypress. Deliberately keeping the diff to what's asked —
        // still rate-limited above the same as any other next-press, which
        // is what actually bounds "machine-gunning song intros" now instead
        // of a mandatory second press.
        // Capture the jam before stopping — StopPerformance drops the jam
        // once its last member leaves, so a solo player switching would
        // otherwise land in a brand new jam and restart their loop from the
        // top. Remembering the id *and* the anchor lets us put the same jam
        // back if we were the one keeping it alive.
        bool wasPerforming = active.TryGetValue(uid, out var prevPerf);
        int inheritJamId = -1;
        long inheritAnchorMs = 0;
        if (wasPerforming)
        {
            inheritJamId = prevPerf.JamId;
            if (jams.TryGetValue(inheritJamId, out var prevJam)) inheritAnchorMs = prevJam.AnchorMs;
            StopPerformance(uid, "switch");
        }

        var slot = fromPlayer.InventoryManager?.ActiveHotbarSlot;
        if (slot?.Itemstack?.Collectible is not ItemInstrument item) return;
        if (item.Defs.Length == 0) return;

        int idx = item.GetInstrumentIndex(slot.Itemstack);
        idx = (idx + 1) % item.Defs.Length;
        slot.Itemstack.Attributes.SetInt("instrumentIndex", idx);
        slot.MarkDirty();

        if (wasPerforming)
        {
            var entity = fromPlayer.Entity;
            // Re-check the start gate (§8.3) rather than assume it still
            // holds — if something disallowed slipped in between the
            // original stop and now, land stopped rather than force a
            // performance the gate would otherwise have rejected.
            if (entity != null && !PerformanceGuard.AnyDisallowedInput(entity.Controls))
            {
                // Put the jam back if stopping emptied it, so the restart
                // rejoins the same clock instead of starting a fresh one.
                // Reusing the id is safe — nextJamId only ever increments.
                if (inheritJamId >= 0 && !jams.ContainsKey(inheritJamId))
                {
                    jams[inheritJamId] = new JamSession { Id = inheritJamId, AnchorMs = inheritAnchorMs };
                }
                StartPerformance(fromPlayer, entity, slot, item, inheritJamId);
            }
        }
    }

    /// <summary>
    /// Builds a start packet describing a performance as of *right now*.
    /// Rebuilt per send rather than cached, because JamElapsedMs has to
    /// reflect the current moment — a late joiner needs where the jam is
    /// now, not where it was when the performance began.
    /// </summary>
    PerformanceStartPacket BuildStartPacket(Performance perf)
    {
        long jamElapsedMs = jams.TryGetValue(perf.JamId, out var jam)
            ? sapi.World.ElapsedMilliseconds - jam.AnchorMs
            : 0;

        return new PerformanceStartPacket
        {
            PlayerUid = perf.PlayerUid,
            EntityId = perf.EntityId,
            InstrumentIndex = perf.InstrumentIndex,
            JamId = perf.JamId,
            JamElapsedMs = jamElapsedMs
        };
    }

    float RangeOf(int instrumentIndex)
    {
        var defs = ItemInstrument?.Defs;
        if (defs == null || defs.Length == 0) return 32f;
        if (instrumentIndex < 0 || instrumentIndex >= defs.Length) return defs[0].Range;
        return defs[instrumentIndex].Range;
    }

    /// <summary>
    /// Late-join sync: hands ongoing performances to players who have come
    /// within earshot since they started, so walking up to a performance
    /// actually lets you hear it instead of waiting for them to restart.
    ///
    /// The position is carried by the jam clock, so a late joiner drops in
    /// at the correct point in the loop rather than from the top — the same
    /// mechanism that keeps performers in time with each other.
    /// </summary>
    void ScanForNewListeners()
    {
        if (active.Count == 0) return;

        foreach (var kv in active)
        {
            var perf = kv.Value;
            float range = RangeOf(perf.InstrumentIndex);
            double notifySq = range * NotifyRangeFactor * (range * NotifyRangeFactor);
            double forgetSq = range * ForgetRangeFactor * (range * ForgetRangeFactor);

            // Drop anyone who's gone (disconnected, or wandered well clear)
            // so that coming back re-arms them. Without the offline check, a
            // player who reconnects would still be marked notified and would
            // never be sent the performance again.
            if (perf.NotifiedUids.Count > 0)
            {
                foreach (var luid in new List<string>(perf.NotifiedUids))
                {
                    var lp = sapi.World.PlayerByUid(luid);
                    if (lp?.Entity?.Pos == null ||
                        lp.Entity.Pos.XYZ.SquareDistanceTo(perf.Anchor) > forgetSq)
                    {
                        perf.NotifiedUids.Remove(luid);
                    }
                }
            }

            foreach (var p in sapi.World.AllOnlinePlayers)
            {
                var sp = (IServerPlayer)p;
                if (sp.Entity?.Pos == null) continue;
                if (perf.NotifiedUids.Contains(sp.PlayerUID)) continue;
                if (sp.Entity.Pos.XYZ.SquareDistanceTo(perf.Anchor) > notifySq) continue;

                channel.SendPacket(BuildStartPacket(perf), sp);
                perf.NotifiedUids.Add(sp.PlayerUID);
            }
        }
    }

    /// <summary>
    /// Finds the jam of the nearest performer already playing within earshot,
    /// or starts a new one. "Within earshot" uses that performer's own
    /// instrument range, so the rule reads as: if you can hear someone
    /// playing, you play along with them.
    ///
    /// Deliberately does NOT merge jams when a player stands between two of
    /// them: merging would force everyone in one of the jams to jump to a
    /// different position mid-song, which is audible. The newcomer picks the
    /// nearest and the two jams stay independent.
    /// </summary>
    JamSession FindOrCreateJam(string uid, Vec3d pos)
    {
        var defs = ItemInstrument?.Defs;

        JamSession best = null;
        double bestDistSq = double.MaxValue;

        foreach (var kv in active)
        {
            var other = kv.Value;
            if (other.PlayerUid == uid) continue;
            if (!jams.TryGetValue(other.JamId, out var otherJam)) continue;

            float range = 32f;
            if (defs != null && other.InstrumentIndex >= 0 && other.InstrumentIndex < defs.Length)
            {
                range = defs[other.InstrumentIndex].Range;
            }

            double distSq = other.Anchor.SquareDistanceTo(pos);
            if (distSq <= range * range && distSq < bestDistSq)
            {
                bestDistSq = distSq;
                best = otherJam;
            }
        }

        if (best == null)
        {
            best = new JamSession { Id = nextJamId++, AnchorMs = sapi.World.ElapsedMilliseconds };
            jams[best.Id] = best;
        }

        best.Members.Add(uid);
        return best;
    }

    bool RateLimited(Dictionary<string, long> map, string uid)
    {
        long now = sapi.World.ElapsedMilliseconds;
        if (map.TryGetValue(uid, out long last) && now - last < RateLimitMs) return true;
        map[uid] = now;
        return false;
    }

    // -------------------------------------------------------------- tick

    void Tick(float dt)
    {
        if (active.Count == 0) return;

        // Snapshot the keys — StopPerformance() mutates `active` mid-loop.
        var uids = new List<string>(active.Keys);
        foreach (string uid in uids)
        {
            if (!active.TryGetValue(uid, out var perf)) continue; // already removed this tick

            long ageMs = sapi.World.ElapsedMilliseconds - perf.StartedAtMs;
            if (ageMs > WatchdogMs)
            {
                sapi.World.Logger.Warning("[instruments] watchdog force-stopped a performance for player {0} after {1} ms — a stop condition failed to fire.", uid, ageMs);
                StopPerformance(uid, "watchdog");
                continue;
            }

            var player = sapi.World.PlayerByUid(uid) as IServerPlayer;
            var entity = player?.Entity;
            if (entity == null)
            {
                // Shouldn't normally happen — PlayerDisconnect/PlayerDeath should
                // have already caught this — but fail closed instead of leaking.
                StopPerformance(uid, "disconnect");
                continue;
            }

            // AfterActiveSlotChanged (§8.2) only fires on switching to a
            // DIFFERENT slot index — dropping/consuming/destroying the
            // instrument leaves the same slot selected but now empty (or
            // holding something else), which that event never sees. Catch
            // it here instead of adding another event hook, consistent with
            // the tick already being the backstop for everything input
            // flags miss.
            if (player?.InventoryManager?.ActiveHotbarSlot?.Itemstack?.Collectible is not ItemInstrument _)
            {
                StopPerformance(uid, "slot");
                continue;
            }

            // Order matters — check inputs before position so the reported
            // reason is the action rather than the displacement it caused (§8.2).
            if (PerformanceGuard.AnyDisallowedInput(entity.Controls))
            {
                StopPerformance(uid, "action");
            }
            else if (PerformanceGuard.HasMoved(entity, perf.Anchor))
            {
                StopPerformance(uid, "moved");
            }
            else if (perf.WasSitting && !PerformanceGuard.IsSitting(entity))
            {
                StopPerformance(uid, "gotup");
            }
            else if (!perf.WasSitting && PerformanceGuard.IsSitting(entity))
            {
                perf.WasSitting = true;
                perf.Anchor = entity.Pos.XYZ.Clone();
            }
        }

        // Deliberately after the stop checks: anything that ended this tick
        // is already out of `active`, so a listener who just walked up can't
        // be handed a performance that's in the middle of dying.
        if (++tickCounter % ListenerScanEveryNTicks == 0)
        {
            ScanForNewListeners();
        }
    }

    // ------------------------------------------------------------ events

    void OnSlotChanged(IServerPlayer player, ActiveSlotChangeEventArgs args)
    {
        if (active.ContainsKey(player.PlayerUID))
        {
            StopPerformance(player.PlayerUID, "slot");
        }
    }

    void OnPlayerDeath(IServerPlayer player, DamageSource damageSource)
    {
        StopPerformance(player.PlayerUID, "death");
    }

    void OnPlayerDisconnect(IServerPlayer player)
    {
        StopPerformance(player.PlayerUID, "disconnect");
        lastToggleMs.Remove(player.PlayerUID);
        lastNextMs.Remove(player.PlayerUID);
    }

    // ------------------------------------------------------------- exit

    /// <summary>
    /// The single exit path (§8.1, §10.5). Must be safe to call on a player
    /// who isn't performing — every stop condition routes through here,
    /// including ones that fire opportunistically (e.g. the tick's
    /// disconnect fallback) alongside an explicit event that already did.
    /// </summary>
    public void StopPerformance(string uid, string reason)
    {
        if (!active.TryGetValue(uid, out var perf)) return;
        active.Remove(uid);

        // Leave the jam; drop it once nobody's left in it. A jam outliving
        // its last member would keep an increasingly stale clock around for
        // someone who wanders back later.
        if (jams.TryGetValue(perf.JamId, out var jam))
        {
            jam.Members.Remove(uid);
            if (jam.Members.Count == 0) jams.Remove(perf.JamId);
        }

        // Hard-cut for cases where the sound must go now (death/disconnect),
        // or where a fade would just overlap oddly with what's about to play
        // next ("switch" — an instrument change is starting a new sound
        // immediately). Fade for everything else, since movement-stop means
        // this fires constantly (§10.5).
        bool fade = reason is not ("death" or "disconnect" or "switch");

        // Addressed to exactly the players who were told to start it, rather
        // than re-deriving "who's nearby" — those two sets no longer match
        // now that late-join hands the performance out over time and only
        // drops listeners at a wider radius. A listener sitting between the
        // notify and forget thresholds would otherwise never be told to
        // stop, and would be left with a silent sound running forever.
        var stopPacket = new PerformanceStopPacket { PlayerUid = uid, Fade = fade, Reason = reason };
        channel.SendPacket(stopPacket, NotifiedRecipients(perf));

        var player = sapi.World.PlayerByUid(uid) as IServerPlayer;
        if (player?.Entity != null)
        {
            StopAnimation(player.Entity);
        }
    }

    void StartAnimation(EntityAgent entity)
    {
        entity.AnimManager.StartAnimation(new AnimationMetaData
        {
            Animation = AnimConstants.AnimationCode,
            Code = AnimConstants.RunCode,
            AnimationSpeed = 1f,
            EaseInSpeed = 3f,
            EaseOutSpeed = 3f,
            Weight = 1f,
            BlendMode = EnumAnimationBlendMode.AddAverage
        }.Init());
    }

    void StopAnimation(EntityAgent entity)
    {
        entity.AnimManager.StopAnimation(AnimConstants.RunCode);
    }

    /// <summary>
    /// Spec §9 "Recipient selection" — no whole-server broadcast. Used for
    /// the initial send only ("who is nearby the instant this starts");
    /// everything afterwards is addressed via <see cref="NotifiedRecipients"/>,
    /// since late-join means the audience grows over time.
    /// </summary>
    IServerPlayer[] RecipientsInRange(Vec3d pos, float range)
    {
        var result = new List<IServerPlayer>();
        foreach (var p in sapi.World.AllOnlinePlayers)
        {
            var sp = (IServerPlayer)p;
            if (sp.Entity?.Pos != null && sp.Entity.Pos.DistanceTo(pos) < range * NotifyRangeFactor)
            {
                result.Add(sp);
            }
        }
        return result.ToArray();
    }

    /// <summary>
    /// The players currently believed to have this performance's sound
    /// running — i.e. everyone sent a start packet and not since dropped.
    /// Offline uids are skipped rather than pruned; the performance is about
    /// to be discarded anyway wherever this is used.
    /// </summary>
    IServerPlayer[] NotifiedRecipients(Performance perf)
    {
        var result = new List<IServerPlayer>();
        foreach (var luid in perf.NotifiedUids)
        {
            if (sapi.World.PlayerByUid(luid) is IServerPlayer sp) result.Add(sp);
        }
        return result.ToArray();
    }
}
