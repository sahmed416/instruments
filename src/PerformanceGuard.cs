using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace Instruments;

/// <summary>
/// Spec §8.2. Shared, side-agnostic predicate for "is this player currently
/// doing something that isn't quietly performing". Used verbatim by both
/// the server tick (<see cref="Server.InstrumentServerState"/>) and the
/// performer's own client tick (<see cref="Client.InstrumentSoundManager"/>)
/// so the two can never diverge. Implements the narrow allow-list ("look
/// around", "sit down") by inverting it into a deny-list of input flags —
/// anything not explicitly excluded fails closed and stops the music.
/// </summary>
public static class PerformanceGuard
{
    public const double MoveThresholdSq = 0.01; // 0.1 blocks, squared

    /// <summary>
    /// Any input state that is NOT allowed while performing.
    /// FloorSitting is deliberately absent — handled separately via
    /// <see cref="IsSitting"/>, because sit/stand has its own asymmetric
    /// rule (spec §8.2 "Sitting") rather than being a flat stop condition.
    /// </summary>
    public static bool AnyDisallowedInput(EntityControls c) =>
        c.TriesToMove          // Forward/Backward/Left/Right — note: EXCLUDES Jump
        || c.Jump
        || c.Sneak
        || c.Sprint
        || c.Up || c.Down      // swim/fly vertical
        || c.Gliding
        || c.IsClimbing
        || c.IsFlying
        || c.LeftMouseDown     // mining / attacking
        || c.RightMouseDown    // placing / using
        || c.HandUse != EnumHandInteract.None; // eating, smithing, aiming, etc.

    public static bool HasMoved(Entity e, Vec3d anchor) =>
        e.Pos.XYZ.SquareDistanceTo(anchor) > MoveThresholdSq;

    public static bool IsSitting(EntityAgent e) =>
        e.Controls.FloorSitting || e.MountedOn != null;
}
