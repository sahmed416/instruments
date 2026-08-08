using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Instruments.Server;

/// <summary>
/// Makes hostile creatures less likely to spawn around an ongoing
/// performance, scaling with how many instruments are in the jam.
///
/// **Nothing here mutates game state.** It only answers "may this creature
/// spawn near this player?" with a yes/no. That's deliberate: the
/// <see cref="RuntimeSpawnConditions"/> instance handed to the callback is a
/// shared, global object, so writing to its <c>Chance</c> would change
/// spawning for every player on the server, permanently, until restart.
/// Reading it and returning a bool leaves zero residue — stop playing and
/// spawning is immediately, exactly back to vanilla, with nothing to undo.
///
/// THREAD SAFETY: the API documents this callback as "might be called
/// outside the main thread", so it must never touch the live performance
/// dictionaries, which the server tick mutates. Instead the tick publishes
/// an immutable snapshot by reference assignment, and the callback only ever
/// reads the currently-published one. No locks, no torn reads, and a
/// snapshot in flight simply describes a state from a fraction of a second
/// ago — which is harmless for something already probabilistic.
/// </summary>
public class SpawnSuppressor
{
    /// <summary>Suppression percent by jam size. Index 0 is unused.</summary>
    static readonly int[] SuppressionByJamSize = { 0, 50, 75, 85, 100 };

    public const string HostileGroup = "hostile";

    readonly ICoreServerAPI sapi;

    /// <summary>
    /// playerUid → suppression percent. Replaced wholesale, never mutated
    /// after publishing — that's what makes lock-free reads safe. Volatile so
    /// a spawn thread can't observe a stale reference indefinitely.
    /// </summary>
    volatile Dictionary<string, int> published = new();

    /// <summary>Handlers we attached, kept so they can be removed exactly.</summary>
    readonly Dictionary<string, Vintagestory.API.Common.CanSpawnNearbyDelegate> attached = new();

    public SpawnSuppressor(ICoreServerAPI sapi)
    {
        this.sapi = sapi;
    }

    public static int SuppressionForJamSize(int jamSize)
    {
        if (jamSize <= 0) return 0;
        if (jamSize >= SuppressionByJamSize.Length) return SuppressionByJamSize[SuppressionByJamSize.Length - 1];
        return SuppressionByJamSize[jamSize];
    }

    /// <summary>Publishes a new snapshot. Main thread only.</summary>
    public void Publish(Dictionary<string, int> snapshot) => published = snapshot;

    /// <summary>
    /// Attaches to a player's spawn callback. Safe to call repeatedly — it
    /// detaches any previous handler first, so a respawn that hands us a
    /// fresh entity can't leave two handlers stacked on one player.
    /// </summary>
    public void Attach(IServerPlayer player)
    {
        var entity = player?.Entity;
        if (entity == null) return;

        Detach(player);

        string uid = player.PlayerUID;
        Vintagestory.API.Common.CanSpawnNearbyDelegate handler = (type, spawnPos, sc) => CanSpawnNearby(uid, type, sc);
        entity.OnCanSpawnNearby += handler;
        attached[uid] = handler;
    }

    /// <summary>
    /// Removes our handler. Called on disconnect and on mod dispose so no
    /// suppression can outlive the mod — the whole feature is meant to leave
    /// nothing behind.
    /// </summary>
    public void Detach(IServerPlayer player)
    {
        if (player == null) return;
        if (!attached.TryGetValue(player.PlayerUID, out var handler)) return;
        attached.Remove(player.PlayerUID);

        var entity = player.Entity;
        if (entity != null) entity.OnCanSpawnNearby -= handler;
    }

    public void DetachAll()
    {
        foreach (var uid in new List<string>(attached.Keys))
        {
            Detach(sapi.World.PlayerByUid(uid) as IServerPlayer);
        }
        attached.Clear();
        published = new Dictionary<string, int>();
    }

    /// <summary>
    /// The hot path — may run off the main thread. Reads only the published
    /// snapshot and the arguments it was handed.
    /// </summary>
    bool CanSpawnNearby(string uid, EntityProperties type, RuntimeSpawnConditions sc)
    {
        var snapshot = published;
        if (snapshot.Count == 0) return true;
        if (!snapshot.TryGetValue(uid, out int percent) || percent <= 0) return true;

        if (!IsHostile(type, sc)) return true;

        // Denying this fraction of attempts is what produces the percentage
        // reduction. Random.Shared is thread-safe (unlike a shared Random).
        return Random.Shared.Next(100) >= percent;
    }

    bool IsHostile(EntityProperties type, RuntimeSpawnConditions sc)
    {
        string group = sc?.Group ?? type?.Server?.SpawnConditions?.Runtime?.Group;
        if (string.Equals(group, HostileGroup, StringComparison.OrdinalIgnoreCase)) return true;

        // Opt-in extra codes, for modded creatures that never declare a group.
        var item = sapi.World.GetItem(ItemInstrument.ItemCode) as ItemInstrument;
        return item != null && item.MatchesExtraHostile(type?.Code?.ToString());
    }
}
