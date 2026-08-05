using Instruments.Client;
using Instruments.Network;
using Instruments.Server;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace Instruments;

public class InstrumentsModSystem : ModSystem
{
    public const string ChannelName = "instruments";

    ICoreClientAPI capi;
    IClientNetworkChannel clientChannel;
    InstrumentSoundManager soundManager;
    InstrumentServerState serverState;

    public override void Start(ICoreAPI api)
    {
        base.Start(api);

        api.RegisterItemClass("ItemInstrument", typeof(ItemInstrument));

        // Same registration order on both sides — required by the channel contract.
        // Includes StopRequest, which the spec's §9 registration snippet omits
        // but its own prose (§7.1, §9) clearly needs — see CLAUDE.md at the
        // repo root.
        api.Network.RegisterChannel(ChannelName)
            .RegisterMessageType<TogglePlayRequest>()
            .RegisterMessageType<NextInstrumentRequest>()
            .RegisterMessageType<StopRequest>()
            .RegisterMessageType<PerformanceStartPacket>()
            .RegisterMessageType<PerformanceStopPacket>();
    }

    public override void StartServerSide(ICoreServerAPI sapi)
    {
        base.StartServerSide(sapi);
        var channel = sapi.Network.GetChannel(ChannelName);
        serverState = new InstrumentServerState(sapi, channel);
    }

    public override void StartClientSide(ICoreClientAPI capi)
    {
        base.StartClientSide(capi);
        this.capi = capi;

        clientChannel = capi.Network.GetChannel(ChannelName);
        soundManager = new InstrumentSoundManager(capi, clientChannel);

        RegisterHotkeys(capi);
    }

    void RegisterHotkeys(ICoreClientAPI capi)
    {
        // G/H are placeholders per spec §7 — check for collisions with
        // vanilla bindings on your own keyboard layout before shipping.
        // Both are rebindable through the normal controls menu because
        // they're registered hotkeys.
        //
        // No dedicated stop key (spec §7.1 had one as an idempotent escape
        // hatch for client/server desync) — removed at the user's request:
        // there are already several ways to stop a performance (drop the
        // instrument, move, switch slots, ...), and a third key for
        // something the toggle already does wasn't worth the keybind-list
        // clutter for that edge case. StopRequest itself is unaffected —
        // the client's local guard (InstrumentSoundManager) still sends it
        // automatically whenever it detects a stop condition locally; only
        // the manual hotkey that also sent it is gone.
        capi.Input.RegisterHotKey("instrument_play", Lang.Get("instruments:hotkey-instrument_play"),
            GlKeys.G, HotkeyType.CharacterControls);
        capi.Input.RegisterHotKey("instrument_next", Lang.Get("instruments:hotkey-instrument_next"),
            GlKeys.H, HotkeyType.CharacterControls);

        capi.Input.SetHotKeyHandler("instrument_play", OnPlayHotkey);
        capi.Input.SetHotKeyHandler("instrument_next", OnNextHotkey);
    }

    /// <summary>True only when holding an instrument — otherwise return false
    /// so the key isn't swallowed and can still do whatever else it's bound
    /// to elsewhere (spec §7.1).</summary>
    bool IsHoldingInstrument() =>
        capi.World.Player?.InventoryManager?.ActiveHotbarSlot?.Itemstack?.Collectible is ItemInstrument;

    bool OnPlayHotkey(KeyCombination _)
    {
        if (!IsHoldingInstrument()) return false;
        clientChannel.SendPacket(new TogglePlayRequest());
        return true;
    }

    bool OnNextHotkey(KeyCombination _)
    {
        if (!IsHoldingInstrument()) return false;
        clientChannel.SendPacket(new NextInstrumentRequest());
        return true;
    }

    public override void Dispose()
    {
        soundManager?.Dispose();
        base.Dispose();
    }
}
