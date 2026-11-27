using System;
using System.Collections.Generic;
using System.Linq;

namespace Pedigree.Core;

/// <summary>
/// Finds where every cat should sleep: good unrelated pairs together in the best rooms, no rivals or related
/// pairs sharing a room, no more than the soft cap per room, fighters spread out, and as few moves as possible.
/// Deterministic local search (moves and swaps) from the current layout plus a few seeded restarts.
/// </summary>
internal sealed class RoomSearch
{
    readonly List<CatInfo> _cats;
    readonly List<RoomState> _rooms;
    readonly IGenetics _g;
    readonly PlannerSettings _s;
    readonly PairScorer _score;

    // weights
    const double CrowdPerCat = 8, RivalPenalty = 300, UnsafeBase = 200, UnsafePerRisk = 400;
    const double FightersTogether = 4, FighterLowComfort = 2, MoveCost = 0.4, LoversApart = 1.5, Dilution = 0.3;

    readonly List<(int, int)> _lovers = new();

    public RoomSearch(List<CatInfo> cats, List<RoomState> rooms, IGenetics g, PlannerSettings s, PairScorer score)
    {
        _cats = cats; _rooms = rooms; _g = g; _s = s; _score = score;
        for (int i = 0; i < cats.Count; i++)
            for (int j = i + 1; j < cats.Count; j++)
                if (Breeding.AreLovers(cats[i], cats[j]) && SafePair(cats[i], cats[j], g, s, score)) _lovers.Add((i, j));
    }

    public List<(CatInfo cat, string room)> Run()
    {
        int n = _cats.Count, r = _rooms.Count;
        var result = new List<(CatInfo, string)>();
        if (n == 0 || r == 0) return result;

        int[] current = new int[n];
        for (int i = 0; i < n; i++)
        {
            int idx = _rooms.FindIndex(x => x.Name == _cats[i].Room);   // -1 for cats outside the breeding rooms
            current[i] = idx >= 0 ? idx : -1;
        }
        // Cats with no room yet (strays to take in) start in the emptiest room.
        for (int i = 0; i < n; i++)
            if (current[i] < 0)
            {
                var counts = new int[r]; for (int k = 0; k < n; k++) if (current[k] >= 0) counts[current[k]]++;
                current[i] = Array.IndexOf(counts, counts.Min());
            }
        int[] home = (int[])current.Clone();
        for (int i = 0; i < n; i++) if (!_rooms.Any(x => x.Name == _cats[i].Room)) home[i] = -1;   // bringing a cat inside is free

        int[] best = Climb((int[])current.Clone(), home, out double bestScore);
        var rng = new Random(12345);
        for (int restart = 0; restart < 16; restart++)
        {
            var start = new int[n];
            for (int i = 0; i < n; i++) start[i] = rng.Next(r);
            var cand = Climb(start, home, out double sc);
            if (sc > bestScore + 1e-6) { best = cand; bestScore = sc; }
        }
        for (int i = 0; i < n; i++) result.Add((_cats[i], _rooms[best[i]].Name));
        return result;
    }

    int[] Climb(int[] a, int[] home, out double total)
    {
        int n = a.Length, r = _rooms.Count;
        var roomScore = new double[r];
        for (int k = 0; k < r; k++) roomScore[k] = RoomScore(k, a);
        double Global(int[] x) => GlobalTerms(x, home);
        total = roomScore.Sum() + Global(a);

        bool improved = true; int guard = 0;
        while (improved && guard++ < 500)
        {
            improved = false;
            // single moves
            for (int i = 0; i < n; i++)
                for (int to = 0; to < r; to++)
                {
                    int from = a[i]; if (to == from) continue;
                    a[i] = to;
                    double sf = RoomScore(from, a), st = RoomScore(to, a);
                    double cand = total - roomScore[from] - roomScore[to] + sf + st - Global(Swap(a, i, from)) + Global(a);
                    if (cand > total + 1e-6) { roomScore[from] = sf; roomScore[to] = st; total = cand; improved = true; }
                    else a[i] = from;
                }
            // swaps
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    int ri = a[i], rj = a[j]; if (ri == rj) continue;
                    double before = Global(a);
                    a[i] = rj; a[j] = ri;
                    double si = RoomScore(ri, a), sj = RoomScore(rj, a);
                    double cand = total - roomScore[ri] - roomScore[rj] + si + sj - before + Global(a);
                    if (cand > total + 1e-6) { roomScore[ri] = si; roomScore[rj] = sj; total = cand; improved = true; }
                    else { a[i] = ri; a[j] = rj; }
                }
        }
        return a;
    }

    // Returns the assignment as it was before moving cat i into its current room (for the global delta).
    static int[] Swap(int[] a, int i, int previousRoom) { var b = (int[])a.Clone(); b[i] = previousRoom; return b; }

    double GlobalTerms(int[] a, int[] home)
    {
        double g = 0;
        for (int i = 0; i < a.Length; i++) if (home[i] >= 0 && a[i] != home[i]) g -= MoveCost;
        foreach (var (i, j) in _lovers) if (a[i] != a[j]) g -= LoversApart;
        return g;
    }

    double RoomScore(int room, int[] a)
    {
        var members = new List<CatInfo>();
        for (int i = 0; i < a.Length; i++) if (a[i] == room) members.Add(_cats[i]);
        return Score(members, _rooms[room]);
    }

    double Score(List<CatInfo> m, RoomState room)
    {
        double score = 0;
        int over = Math.Max(0, m.Count - _s.RoomSoftCap);
        score -= CrowdPerCat * over;
        double comfort = room.ComfortBase - over;

        for (int i = 0; i < m.Count; i++)
        {
            if (m[i].Aggression >= 0.7 && comfort < 3) score -= FighterLowComfort;
            for (int j = i + 1; j < m.Count; j++)
            {
                var a = m[i]; var b = m[j];
                if (Breeding.AreRivals(a, b)) score -= RivalPenalty;
                if (a.Aggression >= 0.7 && b.Aggression >= 0.7) score -= FightersTogether;
                if (Breeding.CouldBreed(a, b, _g))
                {
                    double risk = _score.Risk(a, b);
                    if (risk > _s.MaxDefectRisk + 1e-9) score -= UnsafeBase + UnsafePerRisk * risk;
                }
            }
        }

        var matching = BestMatching(m, room, _g, _s, _score, out double value);
        score += value;

        // Dilution: a matched cat that could also pair with a worse partner in the same room.
        foreach (var (x, y) in matching)
        {
            double qxy = _score.Quality(x, y, room.Stimulation);
            foreach (var z in m)
            {
                if (z.Id == x.Id || z.Id == y.Id) continue;
                foreach (var w in new[] { x, y })
                    if (SafePair(w, z, _g, _s, _score)) score -= Dilution * Math.Max(0, qxy - _score.Quality(w, z, room.Stimulation));
            }
        }
        return score;
    }

    internal static bool SafePair(CatInfo a, CatInfo b, IGenetics g, PlannerSettings s, PairScorer score)
        => Breeding.IsBreedingPair(a, b, g) && !Breeding.AreRivals(a, b) && score.Risk(a, b) <= s.MaxDefectRisk + 1e-9;

    /// <summary>Best set of disjoint safe pairs in a room (exact, by bitmask over the room's cats).</summary>
    internal static List<(CatInfo, CatInfo)> BestMatching(List<CatInfo> m, RoomState room, IGenetics g, PlannerSettings s, PairScorer score, out double value)
    {
        int n = Math.Min(m.Count, 16);
        var q = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                q[i, j] = q[j, i] = SafePair(m[i], m[j], g, s, score) ? score.Quality(m[i], m[j], room.Stimulation) : double.NaN;

        var memo = new Dictionary<int, (double v, int pick)>();
        (double v, int pick) Solve(int used)
        {
            int i = 0; while (i < n && (used & (1 << i)) != 0) i++;
            if (i >= n) return (0, -1);
            if (memo.TryGetValue(used, out var hit)) return hit;
            var best = (Solve(used | (1 << i)).v, -1);   // leave cat i unpaired
            for (int j = i + 1; j < n; j++)
            {
                if ((used & (1 << j)) != 0 || double.IsNaN(q[i, j])) continue;
                double v = q[i, j] + Solve(used | (1 << i) | (1 << j)).v;
                if (v > best.Item1 + 1e-9) best = (v, j);
            }
            memo[used] = best;
            return best;
        }

        var pairs = new List<(CatInfo, CatInfo)>();
        value = Solve(0).v;
        int mask = 0;
        while (true)
        {
            int i = 0; while (i < n && (mask & (1 << i)) != 0) i++;
            if (i >= n) break;
            var (_, pick) = Solve(mask);
            if (pick < 0) mask |= 1 << i;
            else { pairs.Add((m[i], m[pick])); mask |= (1 << i) | (1 << pick); }
        }
        return pairs;
    }
}
