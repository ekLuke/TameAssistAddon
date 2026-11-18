using System;
using System.Collections.Generic;
using System.Linq;

namespace TheLair.TameAssistAddon.Core;

/// <summary>
/// Conservative vertical-contour diagnostic using authoritative collider
/// height spans projected onto horizontal slices. This is NOT a full 3D
/// pathfinding/terrain check and never authorizes world changes.
/// </summary>
public static class VerticalPenGeometry
{
    public readonly struct Barrier
    {
        public readonly PenGeometry.Wall Wall;
        public readonly double Bottom;
        public readonly double Top;

        public Barrier(PenGeometry.Wall wall, double bottom, double top)
        {
            Wall = wall;
            Bottom = bottom;
            Top = top;
        }
    }

    public sealed class Result
    {
        public bool Candidate { get; internal set; }
        public string Reason { get; internal set; } = "unverified";
        public int LayersChecked { get; internal set; }
        public double? FailedHeight { get; internal set; }
        public int MinimumLayerBarriers { get; internal set; }
    }

    /// <summary>
    /// Check multiple full-height horizontal slabs above the animal's feet.
    /// A collider must span an entire slab; an endpoint in another elevation
    /// cannot be joined using the horizontal 0.75 m snap tolerance.
    /// </summary>
    public static Result Detect(PenGeometry.Point center, double feetY,
        IReadOnlyList<Barrier> barriers, double radius = 28,
        double cellSize = 0.25)
    {
        var result = new Result();
        if (barriers == null || double.IsNaN(feetY) || double.IsInfinity(feetY))
        {
            result.Reason = "invalid-height-input";
            return result;
        }

        // Each band requires complete solid coverage, not merely contact
        // with a horizontal plane. These bands are conservative diagnostic
        // samples of a boar-sized escape corridor, not species-specific proof.
        var bands = new[] { (Bottom: feetY + 0.15, Top: feetY + 0.35),
                            (Bottom: feetY + 0.55, Top: feetY + 0.80) };
        int minimum = int.MaxValue;
        foreach (var band in bands)
        {
            var solids = barriers.Where(b =>
                b.Bottom <= band.Bottom && b.Top >= band.Top &&
                !double.IsNaN(b.Bottom) && !double.IsNaN(b.Top))
                .Select(b => b.Wall).ToArray();
            minimum = Math.Min(minimum, solids.Length);
            result.LayersChecked++;
            var layer = PenGeometry.Detect(center, solids, cellSize, radius);
            if (!layer.Closed || !layer.Contains(center))
            {
                result.Reason = "vertical-gap:" + layer.Reason;
                result.FailedHeight = (band.Bottom + band.Top) * 0.5;
                result.MinimumLayerBarriers = minimum;
                return result;
            }
        }
        result.Candidate = true;
        result.Reason = "vertical-contour-candidate";
        result.MinimumLayerBarriers = minimum;
        return result;
    }
}
