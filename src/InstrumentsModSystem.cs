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

        // Same registration order on both sides — required by the channel
        // contract. Includes StopRequest — client and server must agree on
        // the full message type list, in the same order, or the channel
        // won't decode packets correctly.
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
        // Deliberately no dedicated stop key: there are already several ways
        // to stop a performance (drop the instrument, move, switch slots,
        // ...), so a third key for something the play toggle already does
        // isn't worth the keybind-list clutter. StopRequest itself is
        // unaffected — the client's local guard (InstrumentSoundManager)
        // still sends it automatically whenever it detects a stop condition
        // locally; there's just no manual hotkey that also sends it.
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
        // Detaches the spawn-suppression handlers, so unloading the mod
        // leaves spawning exactly as vanilla found it.
        serverState?.Dispose();
        base.Dispose();
    }
}
