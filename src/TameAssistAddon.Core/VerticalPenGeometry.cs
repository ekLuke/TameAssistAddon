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
    /// Verify the contour over the entire low escape corridor, including
    /// transitions between overlapping stone/wood colliders. Different
    /// materials can form one wall: every elevation must have a CLOSED 2D
    /// contour, but no individual collider must span the whole corridor.
    /// No 3D pathfinding/terrain validation is implied.
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

        const double minOffset = 0.15;
        const double maxOffset = 0.80;
        const double epsilon = 0.00001;
        const int maxTransitions = 48;

        double bottom = feetY + minOffset;
        double top = feetY + maxOffset;
        // Only barriers close to the discovered 2D pen contour should
        // contribute vertical transition heights. An unrelated roof at the
        // other side of a building must not exhaust this budget.
        var contour = PenGeometry.Detect(center,
            barriers.Select(b => b.Wall).ToArray(), cellSize, radius);
        if (!contour.Closed)
        {
            result.Reason = "horizontal-contour:" + contour.Reason;
            return result;
        }

        var nearContour = barriers.Where(b =>
            !double.IsNaN(b.Bottom) && !double.IsNaN(b.Top) &&
            !double.IsInfinity(b.Bottom) && !double.IsInfinity(b.Top) &&
            b.Top > b.Bottom &&
            contour.NearBoundary(new PenGeometry.Point(
                (b.Wall.A.X + b.Wall.B.X) * 0.5,
                (b.Wall.A.Z + b.Wall.B.Z) * 0.5), 1.6)).ToArray();

        // Every floor/roof joint is a possible transition: split the window
        // there rather than checking one large slab per animal height.
        var transitions = new List<double> { bottom, top };
        foreach (var barrier in nearContour)
        {
            if (barrier.Bottom > bottom + epsilon &&
                barrier.Bottom < top - epsilon)
                transitions.Add(barrier.Bottom);
            if (barrier.Top > bottom + epsilon &&
                barrier.Top < top - epsilon)
                transitions.Add(barrier.Top);
        }
        transitions.Sort();
        var cuts = new List<double>();
        foreach (double y in transitions)
            if (cuts.Count == 0 || y - cuts[cuts.Count - 1] > epsilon)
                cuts.Add(y);
        if (cuts.Count - 1 > maxTransitions)
        {
            result.Reason = "too-many-height-transitions";
            return result;
        }

        int minimum = int.MaxValue;
        for (int i = 1; i < cuts.Count; i++)
        {
            double from = cuts[i - 1], to = cuts[i];
            if (to - from <= epsilon) continue;
            double middle = (from + to) * 0.5;
            // At any given height, stone + wood + other verified materials
            // participate together. An actual empty joint creates an open
            // layer; a seam where their faces touch does not.
            var solids = barriers.Where(b =>
                b.Bottom <= middle && b.Top >= middle &&
                b.Wall.VerifiedClosed &&
                !double.IsNaN(b.Bottom) && !double.IsNaN(b.Top))
                .Select(b => b.Wall).ToArray();
            minimum = Math.Min(minimum, solids.Length);
            result.LayersChecked++;
            var layer = PenGeometry.Detect(center, solids, cellSize, radius);
            if (!layer.Closed || !layer.Contains(center))
            {
                result.Reason = "vertical-gap:" + layer.Reason;
                result.FailedHeight = middle;
                result.MinimumLayerBarriers = minimum;
                return result;
            }
        }
        result.Candidate = result.LayersChecked > 0;
        result.Reason = result.Candidate ?
            "vertical-contour-candidate" : "no-height-layers";
        result.MinimumLayerBarriers = minimum == int.MaxValue ? 0 : minimum;
        return result;
    }

}
