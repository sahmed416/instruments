namespace Instruments;

/// <summary>
/// One entry from the "instruments" attribute array on the instrument item
/// JSON (see assets/instruments/itemtypes/instrument.json). Data-driven per
/// spec §5.1 — adding a sixth instrument is a JSON edit, not a recompile.
/// </summary>
public class InstrumentDef
{
    public string Code;
    public string Sound;
    public float Volume = 1f;
    public float Range = 32f;
}
