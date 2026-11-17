using System;
using System.Collections.Generic;
using System.Linq;

namespace TheLair.TameAssistAddon.Core;

/// <summary>
/// Conservative 2D enclosure search. Barriers must come from authoritative,
/// verified solid colliders. This engine never treats unverified pieces as
/// proof of a closed pen and never writes world state.
/// </summary>
public static class PenGeometry
{
    public readonly struct Point
    {
        public readonly double X, Z;
        public Point(double x, double z) { X = x; Z = z; }
    }

    public readonly struct Wall
    {
        public readonly Point A, B;
        public readonly bool VerifiedClosed;
        public Wall(Point a, Point b, bool verifiedClosed)
        { A = a; B = b; VerifiedClosed = verifiedClosed; }
    }

    public sealed class Result
    {
        public bool Closed { get; internal set; }
        public string Reason { get; internal set; } = "unverified";
        public int InteriorCells { get; internal set; }
        public double AreaSquareMeters { get; internal set; }
        public int BarriersConsidered { get; internal set; }
        public bool Contains(Point point) =>
            Closed && _cells != null && _cells.Contains(Quantize(point, _origin, _cell));
        internal HashSet<(int, int)>? _cells;
        internal Point _origin;
        internal double _cell;
    }

    /// <param name="center">A tamed/partly tamed animal, not a wild spawn.</param>
    /// <param name="walls">Known real solid segments, with gates marked open or unverified.</param>
    /// <param name="cellSize">Small enough to resolve the narrowest legal escape path.</param>
    /// <param name="radius">Maximum conservative search distance.</param>
    public static Result Detect(Point center, IReadOnlyList<Wall> walls,
        double cellSize = 0.25, double radius = 32, int maxCells = 100000)
    {
        var result = new Result();
        if (walls == null || cellSize <= 0 || radius <= cellSize * 4 ||
            maxCells < 16 || cellSize > 0.5 || radius > 80)
        { result.Reason = "invalid-config"; return result; }

        var dimension = (int)Math.Ceiling(2 * radius / cellSize) + 1;
        if ((long)dimension * dimension > maxCells)
        { result.Reason = "too-large"; return result; }

        var min = new Point(center.X - radius, center.Z - radius);
        var wallSegments = walls.Where(w =>
            Math.Max(w.A.X, w.B.X) >= min.X - cellSize &&
            Math.Min(w.A.X, w.B.X) <= center.X + radius + cellSize &&
            Math.Max(w.A.Z, w.B.Z) >= min.Z - cellSize &&
            Math.Min(w.A.Z, w.B.Z) <= center.Z + radius + cellSize).ToArray();
        result.BarriersConsidered = wallSegments.Length;
        if (wallSegments.Length < 3)
        { result.Reason = "insufficient-barriers"; return result; }

        // Mark solid intervals, not points, between grid cell centers. This
        // avoids falsely sealing a pen by rounding diagonal walls to pixels.
        var blocked = new HashSet<(int, int, int, int)>();
        // A missing verified wall is treated as open: never infer closure
        // from an arbitrary nearby object or an open gate.
        foreach (var wall in wallSegments.Where(w => w.VerifiedClosed))
        {
            var lowX = Math.Max(0, (int)Math.Floor((Math.Min(wall.A.X, wall.B.X) - min.X) / cellSize) - 2);
            var highX = Math.Min(dimension - 1, (int)Math.Ceiling((Math.Max(wall.A.X, wall.B.X) - min.X) / cellSize) + 2);
            var lowZ = Math.Max(0, (int)Math.Floor((Math.Min(wall.A.Z, wall.B.Z) - min.Z) / cellSize) - 2);
            var highZ = Math.Min(dimension - 1, (int)Math.Ceiling((Math.Max(wall.A.Z, wall.B.Z) - min.Z) / cellSize) + 2);
            for (int x = lowX; x <= highX; x++)
            for (int z = lowZ; z <= highZ; z++)
            {
                if (x < dimension - 1 && Intersects(
                    Cell(min, cellSize, x, z), Cell(min, cellSize, x + 1, z),
                    wall.A, wall.B))
                    blocked.Add((x, z, x + 1, z));
                if (z < dimension - 1 && Intersects(
                    Cell(min, cellSize, x, z), Cell(min, cellSize, x, z + 1),
                    wall.A, wall.B))
                    blocked.Add((x, z, x, z + 1));
            }
        }

        var start = Quantize(center, min, cellSize);
        if (start.Item1 <= 0 || start.Item1 >= dimension - 1 ||
            start.Item2 <= 0 || start.Item2 >= dimension - 1)
        { result.Reason = "invalid-origin"; return result; }

        var interior = new HashSet<(int, int)>();
        var pending = new Queue<(int, int)>();
        pending.Enqueue(start);
        interior.Add(start);
        while (pending.Count > 0)
        {
            var cell = pending.Dequeue();
            var (x, z) = cell;
            if (x <= 0 || z <= 0 || x >= dimension - 1 || z >= dimension - 1)
            { result.Reason = "open-gap"; return result; }
            foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                var nx = x + dx; var nz = z + dz;
                var edge = (Math.Min(x,nx), Math.Min(z,nz), Math.Max(x,nx), Math.Max(z,nz));
                if (blocked.Contains(edge)) continue;
                if (interior.Add((nx,nz))) pending.Enqueue((nx,nz));
            }
            if (interior.Count >= maxCells)
            { result.Reason = "too-large"; return result; }
        }
        // A real pen needs nontrivial interior space, not a zero-width
        // polygon formed by overlapping fence pieces.
        if (interior.Count < 4)
        { result.Reason = "too-small"; return result; }
        result.Closed = true;
        result.Reason = "closed-2d-candidate";
        result.InteriorCells = interior.Count;
        result.AreaSquareMeters = interior.Count * cellSize * cellSize;
        result._cells = interior;
        result._origin = min;
        result._cell = cellSize;
        return result;
    }

    private static Point Cell(Point min, double size, int x, int z) =>
        new Point(min.X + x * size, min.Z + z * size);

    private static (int,int) Quantize(Point pos, Point origin, double cell) =>
        ((int)Math.Round((pos.X-origin.X)/cell),
         (int)Math.Round((pos.Z-origin.Z)/cell));

    private static double Cross(Point a, Point b, Point c) =>
        (b.X-a.X)*(c.Z-a.Z)-(b.Z-a.Z)*(c.X-a.X);

    private static bool Intersects(Point a, Point b, Point c, Point d)
    {
        const double eps = 1e-8;
        // Collinear grid edges coincident with barriers block traversal.
        var abC = Cross(a,b,c); var abD = Cross(a,b,d);
        var cdA = Cross(c,d,a); var cdB = Cross(c,d,b);
        if (Math.Abs(abC)<eps && Math.Abs(abD)<eps)
            return Math.Max(Math.Min(a.X,b.X), Math.Min(c.X,d.X)) <=
                       Math.Min(Math.Max(a.X,b.X), Math.Max(c.X,d.X)) + eps &&
                   Math.Max(Math.Min(a.Z,b.Z), Math.Min(c.Z,d.Z)) <=
                       Math.Min(Math.Max(a.Z,b.Z), Math.Max(c.Z,d.Z)) + eps;
        return abC*abD <= eps && cdA*cdB <= eps;
    }
}
