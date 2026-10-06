namespace OutfitStudio.Core.Geometry;

/// <summary>Compact support for loose fabric near the binding limit.</summary>
internal static class GarmentBinding
{
    public static float Influence(float distanceSquared, float maximumDistance)
    {
        float relative = MathF.Sqrt(distanceSquared) / maximumDistance;
        if (relative <= 0.25f) return 1;
        if (relative >= 1) return 0;
        // Full fitting near the body, with a C2 transition to the unchanged
        // fabric outside the search radius. A hard cutoff pins isolated hem
        // vertices while their immediate neighbors receive the full transform.
        float t = (1 - relative) / .75f;
        return Math.Clamp(t * t * t * (10 + t * (-15 + 6 * t)), 0, 1);
    }

    public static float SmoothFrameInfluence(float distanceSquared, float maximumDistance)
    {
        // Keep the fully trusted inner region's authored fitting detail. Only
        // loose fabric transitions to the shared frame, reaching it halfway
        // through the binding radius, before long offsets magnify face jumps.
        float t = Math.Clamp((MathF.Sqrt(distanceSquared) / maximumDistance - .25f) / .25f, 0, 1);
        return Math.Clamp(t * t * t * (10 + t * (-15 + 6 * t)), 0, 1);
    }
}
