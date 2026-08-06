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

    readonly ICoreServerAPI sapi;
    readonly IServerNetworkChannel channel;
    ItemInstrument itemInstrumentCache;

    /// <summary>Lazy — see the doc comment on <see cref="ItemInstrument.ItemCode"/>.</summary>
    ItemInstrument ItemInstrument => itemInstrumentCache ??= sapi.World.GetItem(Instruments.ItemInstrument.ItemCode) as ItemInstrument;

    readonly Dictionary<string, Performance> active = new();
    readonly Dictionary<string, long> lastToggleMs = new();
    readonly Dictionary<string, long> lastNextMs = new();

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
    /// </summary>
    void StartPerformance(IServerPlayer fromPlayer, EntityAgent entity, ItemSlot slot, ItemInstrument item)
    {
        string uid = fromPlayer.PlayerUID;

        var def = item.CurrentDef(slot.Itemstack);
        if (def == null) return;

        var perf = new Performance
        {
            PlayerUid = uid,
            EntityId = entity.EntityId,
            InstrumentIndex = item.GetInstrumentIndex(slot.Itemstack),
            StartedAtMs = sapi.World.ElapsedMilliseconds,
            Anchor = entity.Pos.XYZ.Clone(),
            SlotIndex = fromPlayer.InventoryManager.ActiveHotbarSlotNumber,
            WasSitting = PerformanceGuard.IsSitting(entity)
        };
        active[uid] = perf;

        var startPacket = new PerformanceStartPacket
        {
            PlayerUid = uid,
            EntityId = entity.EntityId,
            InstrumentIndex = perf.InstrumentIndex
        };
        channel.SendPacket(startPacket, RecipientsInRange(perf.Anchor, def.Range));

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
        bool wasPerforming = active.ContainsKey(uid);
        if (wasPerforming)
        {
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
                StartPerformance(fromPlayer, entity, slot, item);
            }
        }
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

        var defs = ItemInstrument?.Defs;
        InstrumentDef def = null;
        if (defs != null && defs.Length > 0)
        {
            def = perf.InstrumentIndex >= 0 && perf.InstrumentIndex < defs.Length ? defs[perf.InstrumentIndex] : defs[0];
        }
        float range = def?.Range ?? 32f;

        // Hard-cut for cases where the sound must go now (death/disconnect),
        // or where a fade would just overlap oddly with what's about to play
        // next ("switch" — an instrument change is starting a new sound
        // immediately). Fade for everything else, since movement-stop means
        // this fires constantly (§10.5).
        bool fade = reason is not ("death" or "disconnect" or "switch");

        var stopPacket = new PerformanceStopPacket { PlayerUid = uid, Fade = fade, Reason = reason };
        channel.SendPacket(stopPacket, RecipientsInRange(perf.Anchor, range));

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
    /// Spec §9 "Recipient selection" — no whole-server broadcast, and no
    /// per-listener bookkeeping (§10.4 deliberately cut that). Recomputed
    /// fresh from <paramref name="pos"/> (the performer's anchor, which by
    /// construction hasn't moved) each time a start or stop packet goes out.
    /// </summary>
    IServerPlayer[] RecipientsInRange(Vec3d pos, float range)
    {
        var result = new List<IServerPlayer>();
        foreach (var p in sapi.World.AllOnlinePlayers)
        {
            var sp = (IServerPlayer)p;
            if (sp.Entity?.Pos != null && sp.Entity.Pos.DistanceTo(pos) < range * 1.5f)
            {
                result.Add(sp);
            }
        }
        return result.ToArray();
    }
}
