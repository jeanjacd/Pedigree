using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Pedigree;

/// <summary>
/// Where each room is, learned from where its own cats stand. The game files a cat under whichever room its position
/// falls in, and the SDK's room-center move lands on the wall between the two first-floor rooms of a bigger house
/// (the game then files the cat back into the left room). Placing a cat in the middle of the span its roommates
/// have been seen in avoids that and adapts to house upgrades by itself.
/// Thread-safe: learned on a worker thread, read on the game thread.
/// </summary>
internal sealed class RoomSpots
{
    /// <summary>Span of x seen for a room's cats, and the lowest y seen (the floor; higher y is furniture).</summary>
    record struct Span(double MinX, double MaxX, double FloorY);

    readonly Dictionary<string, Span> _spans = new();
    readonly string _file;
    readonly Action<string> _log;
    bool _dirty;

    public RoomSpots(string file, Action<string> log)
    {
        _file = file;
        _log = log;
        Load();
    }

    /// <summary>Adds where these cats of <paramref name="room"/> stand. Positions are world units; NaN is ignored.</summary>
    public void Observe(string room, IEnumerable<(double x, double y)> positions)
    {
        var pts = positions.Where(p => !double.IsNaN(p.x) && !double.IsNaN(p.y)).ToList();
        if (pts.Count == 0) return;
        var seen = new Span(pts.Min(p => p.x), pts.Max(p => p.x), pts.Min(p => p.y));
        lock (_spans)
        {
            if (_spans.TryGetValue(room, out var old))
            {
                // Cats on one floor share a floor height; a very different y means another floor's room took the name
                // (house rebuilt): start over rather than merge.
                if (Math.Abs(old.FloorY - seen.FloorY) > 2.5) old = seen;
                seen = new Span(Math.Min(old.MinX, seen.MinX), Math.Max(old.MaxX, seen.MaxX), Math.Min(old.FloorY, seen.FloorY));
                if (seen == old) return;
            }
            _spans[room] = seen;
            _dirty = true;
        }
    }

    /// <summary>
    /// A spot inside <paramref name="room"/> for the <paramref name="slot"/>-th cat sent there in one go: the middle of
    /// the room, then alternating a step right and left, kept a step away from the edges. Null if never seen.
    /// </summary>
    public (double x, double y)? Spot(string room, int slot = 0)
    {
        Span s;
        lock (_spans) { if (!_spans.TryGetValue(room, out s)) return null; }
        double mid = (s.MinX + s.MaxX) / 2;
        double step = (slot + 1) / 2 * (slot % 2 == 1 ? 1 : -1);
        double lo = Math.Min(s.MinX + 1, mid), hi = Math.Max(s.MaxX - 1, mid);
        return (Math.Clamp(mid + step, lo, hi), s.FloorY);
    }

    public void SaveIfChanged()
    {
        List<string> lines;
        lock (_spans)
        {
            if (!_dirty) return;
            _dirty = false;
            lines = _spans.Select(kv => string.Create(CultureInfo.InvariantCulture,
                $"{kv.Key} {kv.Value.MinX:0.###} {kv.Value.MaxX:0.###} {kv.Value.FloorY:0.###}")).ToList();
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllLines(_file, lines);
            _log($"[Pedigree] rooms: learned {string.Join(", ", lines)}");
        }
        catch (Exception ex) { _log($"[Pedigree] rooms: saving failed: {ex.Message}"); }
    }

    void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            foreach (var line in File.ReadAllLines(_file))
            {
                var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length != 4) continue;
                var ci = CultureInfo.InvariantCulture;
                _spans[p[0]] = new Span(double.Parse(p[1], ci), double.Parse(p[2], ci), double.Parse(p[3], ci));
            }
        }
        catch (Exception ex) { _log($"[Pedigree] rooms: reading saved rooms failed: {ex.Message}"); }
    }
}
