using ProtoBuf;

namespace Instruments.Network;

// Spec §9. All deliberately empty on the client->server side: the server
// reads the player's own active slot and position and never trusts a
// client-supplied instrument index or position.

[ProtoContract]
public class TogglePlayRequest { }

[ProtoContract]
public class NextInstrumentRequest { }

/// <summary>
/// Sent by the performer's own client when its local mirror of
/// <see cref="PerformanceGuard"/> trips (spec §8.2, §10 — "must not hear a
/// lag spike's worth of music after they act"). No dedicated stop hotkey
/// sends this anymore (removed — several other actions already stop a
/// performance, and a key for something the play toggle already does
/// wasn't worth it), but the type stays: the server still treats it as a
/// hint, stopping if the player is active and doing nothing (no log, no
/// rate limit) otherwise, since spurious stops from a client that already
/// stopped locally are expected and harmless.
/// </summary>
[ProtoContract]
public class StopRequest { }

[ProtoContract]
public class PerformanceStartPacket
{
    [ProtoMember(1)] public string PlayerUid;
    [ProtoMember(2)] public long EntityId;
    [ProtoMember(3)] public int InstrumentIndex;
}

[ProtoContract]
public class PerformanceStopPacket
{
    [ProtoMember(1)] public string PlayerUid;
    [ProtoMember(2)] public bool Fade;    // true = fade out, false = hard stop
    [ProtoMember(3)] public string Reason; // "key" | "action" | "moved" | "gotup" | "slot" | "death" | "disconnect" | "watchdog" | "switch"
}
