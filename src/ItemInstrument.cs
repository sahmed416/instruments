using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace Instruments;

/// <summary>
/// The single instrument item. Five instruments are selected at runtime via
/// a per-itemstack attribute (spec §5.2, §6) rather than five separate
/// items — cycling is a property of the held item.
/// </summary>
public class ItemInstrument : Item
{
    /// <summary>
    /// Looked up lazily by <see cref="Server.InstrumentServerState"/> and
    /// <see cref="Client.InstrumentSoundManager"/> rather than passed in at
    /// construction — on a multiplayer client, items aren't registered yet
    /// at StartClientSide time (see ModSystem doc comments), so an eager
    /// lookup there can race. By the time any performance packet actually
    /// needs this, the player is already holding the item, so it's safe.
    ///
    /// Named ItemCode rather than Code — Item/CollectibleObject already
    /// declares an instance property called Code (a real `dotnet build`
    /// flagged the original name with CS0108, "hides inherited member").
    /// </summary>
    public static readonly AssetLocation ItemCode = new("instruments:instrument");

    public InstrumentDef[] Defs { get; private set; } = System.Array.Empty<InstrumentDef>();

    /// <summary>
    /// Extra entity codes to treat as hostile for spawn suppression, on top
    /// of anything that already declares <c>group: "hostile"</c> in its own
    /// spawn conditions. Exists because that declaration is opt-in: modded
    /// creatures frequently don't set it, and would otherwise ignore the
    /// suppression entirely. Supports a trailing <c>*</c> wildcard.
    /// </summary>
    public string[] ExtraHostileCodes { get; private set; } = System.Array.Empty<string>();

    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);

        Defs = Attributes?["instruments"]?.AsObject<InstrumentDef[]>() ?? System.Array.Empty<InstrumentDef>();
        if (Defs.Length == 0)
        {
            api.World.Logger.Error("[instruments] instrument.json has no \"instruments\" attribute entries — item will have nothing to play.");
        }

        ExtraHostileCodes = Attributes?["extraHostileCodes"]?.AsObject<string[]>() ?? System.Array.Empty<string>();
    }

    /// <summary>
    /// True if <paramref name="entityCode"/> matches one of
    /// <see cref="ExtraHostileCodes"/>. Trailing <c>*</c> matches a prefix,
    /// mirroring how VS itself writes code patterns.
    /// </summary>
    public bool MatchesExtraHostile(string entityCode)
    {
        if (entityCode == null) return false;
        foreach (var pattern in ExtraHostileCodes)
        {
            if (string.IsNullOrEmpty(pattern)) continue;
            if (pattern.EndsWith("*"))
            {
                if (entityCode.StartsWith(pattern.Substring(0, pattern.Length - 1), System.StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (string.Equals(entityCode, pattern, System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Reads the itemstack's selected index (spec §5.2), clamped defensively
    /// in case the def list shrank since the stack was last saved.
    /// </summary>
    public InstrumentDef CurrentDef(ItemStack stack)
    {
        if (Defs.Length == 0) return null;
        int idx = GetInstrumentIndex(stack);
        if (idx < 0 || idx >= Defs.Length) idx = 0;
        return Defs[idx];
    }

    public int GetInstrumentIndex(ItemStack stack) => stack.Attributes.GetInt("instrumentIndex", 0);

    public override string GetHeldItemName(ItemStack stack)
    {
        var def = CurrentDef(stack);
        return def == null ? base.GetHeldItemName(stack) : Lang.Get("instruments:item-instrument-" + def.Code);
    }

    public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
    {
        base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);

        string playKey = "?", nextKey = "?";
        if (world.Api is ICoreClientAPI capi)
        {
            playKey = capi.Input.GetHotKeyByCode("instrument_play")?.CurrentMapping.ToString() ?? "?";
            nextKey = capi.Input.GetHotKeyByCode("instrument_next")?.CurrentMapping.ToString() ?? "?";
        }

        dsc.AppendLine(Lang.Get("instruments:instrument-heldinfo", playKey, nextKey));
    }
}
