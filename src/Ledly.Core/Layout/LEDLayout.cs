namespace Ledly;

/// <summary>Corner of the screen (seen from the front) where the first LED of the strip sits. Names are the JSON values.</summary>
public enum StartCorner { bottomLeft, topLeft, topRight, bottomRight }

public enum ScreenSide { top, right, bottom, left }

/// <summary>Normalised rectangle (0…1, origin top-left).</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double MaxX => X + Width;
    public double MaxY => Y + Height;
}

public readonly record struct PointD(double X, double Y);

/// <summary>One LED: where it sits around the screen and which part of the image it samples.</summary>
public readonly record struct LEDZone(RectD Rect, PointD Position, ScreenSide Side);

/// <summary>How the strip is glued around the back of the monitor.</summary>
public sealed record LEDLayout
{
    public int Top { get; init; } = 20;
    public int Right { get; init; } = 12;
    public int Bottom { get; init; } = 20;
    public int Left { get; init; } = 12;
    public StartCorner Start { get; init; } = StartCorner.bottomLeft;
    /// <summary>Seen from the front of the screen.</summary>
    public bool Clockwise { get; init; } = true;
    /// <summary>Shifts every LED by this many positions.</summary>
    public int Offset { get; init; }
    /// <summary>LEDs missing in the middle of the bottom edge (monitor stand).</summary>
    public int BottomGap { get; init; }
    /// <summary>How far into the screen each LED samples, as a fraction of width/height.</summary>
    public double Depth { get; init; } = 0.1;
    /// <summary>Area overlap between neighbours (0 = none, 1 = double width).</summary>
    public double Overlap { get; init; }

    public int Count => Math.Max(0, Top + Right + Bottom + Left - Math.Min(BottomGap, Bottom));

    /// <summary>Zones in strip order (index 0 = first LED after the controller).</summary>
    public LEDZone[] Zones()
    {
        // Build the ring clockwise from the top-left corner (as seen from the front).
        var ring = new List<LEDZone>();
        var d = Math.Max(0.01, Math.Min(Depth, 0.5));
        var ov = Math.Max(0, Overlap);

        (double, double) Span(int i, int n)
        {
            var w = 1.0 / n;
            var a = i * w - w * ov / 2;
            return (Math.Max(0, a), Math.Min(1, a + w * (1 + ov)));
        }

        for (var i = 0; i < Math.Max(Top, 0); i++)
        {
            var (a, b) = Span(i, Top);
            ring.Add(new(new(a, 0, b - a, d), new((i + 0.5) / Top, 0), ScreenSide.top));
        }
        for (var i = 0; i < Math.Max(Right, 0); i++)
        {
            var (a, b) = Span(i, Right);
            ring.Add(new(new(1 - d, a, d, b - a), new(1, (i + 0.5) / Right), ScreenSide.right));
        }
        // Bottom runs right → left in clockwise order; the gap is centred.
        var gap = Math.Min(Math.Max(BottomGap, 0), Bottom);
        var gapStart = (Bottom - gap) / 2;
        for (var k = 0; k < Math.Max(Bottom, 0); k++)
        {
            var i = Bottom - 1 - k;
            if (i >= gapStart && i < gapStart + gap) continue;
            var (a, b) = Span(i, Bottom);
            ring.Add(new(new(a, 1 - d, b - a, d), new((i + 0.5) / Bottom, 1), ScreenSide.bottom));
        }
        for (var k = 0; k < Math.Max(Left, 0); k++)
        {
            var i = Left - 1 - k;
            var (a, b) = Span(i, Left);
            ring.Add(new(new(0, a, d, b - a), new(0, (i + 0.5) / Left), ScreenSide.left));
        }
        if (ring.Count == 0) return [];

        var bottomCount = Bottom - gap;
        var cornerIndex = Start switch
        {
            StartCorner.topLeft => 0,
            StartCorner.topRight => Top,
            StartCorner.bottomRight => Top + Right,
            _ => Top + Right + bottomCount,
        };

        var n = ring.Count;
        LEDZone[] ordered;
        if (Clockwise)
        {
            var c = cornerIndex % n;
            ordered = ring.Skip(c).Concat(ring.Take(c)).ToArray();
        }
        else
        {
            // Counter-clockwise from the same corner: the LED just before the corner comes first.
            ordered = Enumerable.Range(0, n).Select(k => ring[((cornerIndex - 1 - k) % n + n) % n]).ToArray();
        }
        if (Offset != 0)
        {
            var s = ((Offset % n) + n) % n;
            ordered = ordered.Skip(s).Concat(ordered.Take(s)).ToArray();
        }
        return ordered;
    }

    // MARK: layout wizard

    /// <summary>
    /// Strip index of the LED sitting at each screen corner, taken as the first LED of the side
    /// that starts there going along the strip. This is what the layout wizard asks the user to mark.
    /// </summary>
    public Dictionary<StartCorner, int> CornerIndices()
    {
        var zones = Zones();
        int? First(ScreenSide side, Func<LEDZone, double> key)
        {
            int? best = null;
            for (var i = 0; i < zones.Length; i++)
            {
                if (zones[i].Side != side) continue;
                if (best is null || key(zones[i]) < key(zones[best.Value])) best = i;
            }
            return best;
        }
        var result = new Dictionary<StartCorner, int>();
        void Put(StartCorner c, int? v) { if (v is { } x) result[c] = x; }
        if (Clockwise)
        {
            Put(StartCorner.topLeft, First(ScreenSide.top, z => z.Position.X));
            Put(StartCorner.topRight, First(ScreenSide.right, z => z.Position.Y));
            Put(StartCorner.bottomRight, First(ScreenSide.bottom, z => -z.Position.X));
            Put(StartCorner.bottomLeft, First(ScreenSide.left, z => -z.Position.Y));
        }
        else
        {
            Put(StartCorner.topLeft, First(ScreenSide.left, z => z.Position.Y));
            Put(StartCorner.bottomLeft, First(ScreenSide.bottom, z => z.Position.X));
            Put(StartCorner.bottomRight, First(ScreenSide.right, z => -z.Position.Y));
            Put(StartCorner.topRight, First(ScreenSide.top, z => -z.Position.X));
        }
        return result;
    }

    /// <summary>
    /// Works out the layout from what the user marked: the strip's LED count and the index of the LED
    /// at each corner. With <paramref name="hasBottomGap"/>, the bottom gets as many missing LEDs as needed
    /// to match the top edge. Depth and overlap are kept. Null when the marks don't describe a strip going round.
    /// </summary>
    public LEDLayout? Solved(int n, IReadOnlyDictionary<StartCorner, int> marks, bool hasBottomGap)
    {
        if (n < 4) return null;
        if (!marks.TryGetValue(StartCorner.topLeft, out var tl) || !marks.TryGetValue(StartCorner.topRight, out var tr)
            || !marks.TryGetValue(StartCorner.bottomRight, out var br) || !marks.TryGetValue(StartCorner.bottomLeft, out var bl)) return null;
        int[] all = [tl, tr, br, bl];
        if (all.Distinct().Count() != 4 || all.Any(v => v < 0 || v >= n)) return null;
        int D(int a, int b) => ((b - a) % n + n) % n;

        var clockwise = D(tl, tr) < D(tl, bl);
        int top, right, bottom, left;
        if (clockwise) (top, right, bottom, left) = (D(tl, tr), D(tr, br), D(br, bl), D(bl, tl));
        else (left, bottom, right, top) = (D(tl, bl), D(bl, br), D(br, tr), D(tr, tl));
        if (top + right + bottom + left != n || Math.Min(top, Math.Min(right, left)) <= 0) return null;
        var gap = hasBottomGap ? Math.Max(0, top - bottom) : 0;

        LEDLayout? best = null;
        foreach (var start in Enum.GetValues<StartCorner>())
        {
            for (var step = 0; step <= n; step++)
            {
                var found = false;
                foreach (var offset in step == 0 ? new[] { 0 } : new[] { step, -step })
                {
                    if (best is not null && Math.Abs(best.Offset) <= Math.Abs(offset)) { found = true; break; }
                    var candidate = this with
                    {
                        Top = top, Right = right, Bottom = bottom + gap, Left = left, BottomGap = gap,
                        Start = start, Clockwise = clockwise, Offset = offset,
                    };
                    if (candidate.Count == n && SameMarks(candidate.CornerIndices(), marks))
                    {
                        best = candidate;
                        found = true;
                        break;
                    }
                }
                if (found) break;
            }
        }
        return best;
    }

    static bool SameMarks(Dictionary<StartCorner, int> a, IReadOnlyDictionary<StartCorner, int> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);
}
