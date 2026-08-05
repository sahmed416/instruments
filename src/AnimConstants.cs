namespace Instruments;

/// <summary>
/// Shared between server (starts/stops the animation) and client (may need
/// to mirror it locally in first person, spec §11 point 2). One place so
/// the two never drift if the animation code ever changes.
///
/// "knifestab" / "knifestab-fp" verified present in the player entity shape
/// (assets/game/shapes/entity/humanoid/seraph.json) against the real game
/// install. Reusing the knife/butchering animation per spec §2 non-goals
/// ("Custom animations").
/// </summary>
public static class AnimConstants
{
    public const string AnimationCode = "knifestab";
    public const string AnimationCodeFp = "knifestab-fp";

    /// <summary>Our own AnimManager entry code, distinct from the animation asset code.</summary>
    public const string RunCode = "instrumentplay";
}
