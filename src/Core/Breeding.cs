using System;
using System.Collections.Generic;
using System.Linq;

namespace Pedigree.Core;

/// <summary>The game's breeding rules (as documented by the SDK) and the planner's pair scoring.</summary>
public static class Breeding
{
    public static readonly string[] StatNames = { "STR", "DEX", "CON", "INT", "SPD", "CHA", "LCK" };

    /// <summary>Below this the game refuses the proposal (before the comfort bonus).</summary>
    public const double MinAttraction = 0.05;

    /// <summary>Chance a kitten takes the better parent value for one stat: (100 + s) / (200 + s).</summary>
    public static double StatInheritChance(double stimulation)
    {
        double s = Math.Max(0, stimulation);
        return (100 + s) / (200 + s);
    }

    public static double DefectDisorderChance(double kinship) => Math.Min(0.42, 0.02 + 0.4 * Math.Max(0, kinship - 0.2));
    public static double DefectPartChance(double kinship) => kinship <= 0.05 ? 0 : Math.Min(1.0, 1.5 * kinship);

    /// <summary>Chance of at least one birth defect for a kitten with this inbreeding.</summary>
    public static double DefectRisk(double kinship)
    {
        double d = DefectDisorderChance(kinship), p = DefectPartChance(kinship);
        return 1 - (1 - d) * (1 - p);
    }

    /// <summary>Whether the two bodies can make a kitten at all (sex rules only).</summary>
    public static bool SexesCompatible(CatInfo a, CatInfo b)
    {
        if (a.Id == b.Id) return false;
        if (a.Sex == Sex.Neutral || b.Sex == Sex.Neutral) return true;
        return a.Sex != b.Sex;
    }

    /// <summary>
    /// Whether these two could have a kitten together now or soon. Kittens count (they grow up overnight),
    /// so a kitten next to its parent is flagged. Old cats cannot breed.
    /// </summary>
    public static bool CouldBreed(CatInfo a, CatInfo b, IGenetics g)
    {
        if (!SexesCompatible(a, b) || a.IsOld || b.IsOld) return false;
        if (a.IsKitten || b.IsKitten) return true;   // the game reports 0 attraction for kittens; assume the worst
        return g.Attraction(a, b) >= MinAttraction;
    }

    /// <summary>Whether this is a pair worth breeding tonight (adults, attracted to each other).</summary>
    public static bool IsBreedingPair(CatInfo a, CatInfo b, IGenetics g)
        => SexesCompatible(a, b) && !a.IsOld && !b.IsOld && !a.IsKitten && !b.IsKitten && g.Attraction(a, b) >= MinAttraction;

    public static bool AreRivals(CatInfo a, CatInfo b) => a.RivalId == b.Id || b.RivalId == a.Id;
    public static bool AreLovers(CatInfo a, CatInfo b) => a.LoverId == b.Id || b.LoverId == a.Id;

    public static int Ceiling(CatInfo a, CatInfo b)
    {
        int s = 0; for (int i = 0; i < 7; i++) s += Math.Max(a.Stats[i], b.Stats[i]); return s;
    }

    public static int Sevens(CatInfo a, CatInfo b)
    {
        int n = 0; for (int i = 0; i < 7; i++) if (Math.Max(a.Stats[i], b.Stats[i]) >= 7) n++; return n;
    }

    public static double Expected(CatInfo a, CatInfo b, double stimulation)
    {
        double p = StatInheritChance(stimulation), e = 0;
        for (int i = 0; i < 7; i++)
        {
            int hi = Math.Max(a.Stats[i], b.Stats[i]), lo = Math.Min(a.Stats[i], b.Stats[i]);
            e += p * hi + (1 - p) * lo;
        }
        return e;
    }
}

/// <summary>Pair scoring with the house context (best values per stat).</summary>
public sealed class PairScorer
{
    readonly IGenetics _g;
    readonly int[] _houseBest = new int[7];
    readonly Dictionary<(long, long), double> _kin = new();

    public PairScorer(IEnumerable<CatInfo> cats, IGenetics g)
    {
        _g = g;
        foreach (var c in cats)
            for (int i = 0; i < 7; i++) _houseBest[i] = Math.Max(_houseBest[i], c.Stats[i]);
    }

    public IReadOnlyList<int> HouseBest => _houseBest;

    public double Kinship(CatInfo a, CatInfo b)
    {
        var k = a.Id < b.Id ? (a.Id, b.Id) : (b.Id, a.Id);
        if (!_kin.TryGetValue(k, out var v)) { v = _g.Kinship(a, b); _kin[k] = v; }
        return v;
    }

    public double Risk(CatInfo a, CatInfo b) => Breeding.DefectRisk(Kinship(a, b));

    /// <summary>
    /// How good a kitten of a and b would be in a room with this stimulation: the expected stat sum, plus a bonus
    /// for 7s and for the house's best values (so rare stats stay in the gene pool), plus a bit for bonded pairs.
    /// </summary>
    public double Quality(CatInfo a, CatInfo b, double stimulation)
    {
        double q = Breeding.Expected(a, b, stimulation);
        for (int i = 0; i < 7; i++)
        {
            int hi = Math.Max(a.Stats[i], b.Stats[i]);
            if (hi >= 7) q += 0.75;
            if (hi >= _houseBest[i]) q += 0.35;
        }
        if (Breeding.AreLovers(a, b)) q += 0.8;
        if (a.Aggression >= 0.7 && b.Aggression >= 0.7) q -= 1.5;
        return q;
    }
}
