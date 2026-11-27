using System;
using System.Collections.Generic;
using System.Linq;

namespace Pedigree.Core;

/// <summary>
/// Builds the whole plan: which strays to take in, where every cat should sleep tonight, the best pairs,
/// the next run squad, donation suggestions, and alerts about the house as it is now.
/// Pure logic: the same input always gives the same plan.
/// </summary>
public sealed class Planner
{
    readonly List<CatInfo> _cats;
    readonly List<RoomState> _rooms;
    readonly IGenetics _g;
    readonly PlannerSettings _s;
    readonly PairScorer _score;

    public Planner(IEnumerable<CatInfo> cats, IEnumerable<RoomState> rooms, IGenetics genetics, PlannerSettings? settings = null)
    {
        _cats = cats.ToList();
        _rooms = rooms.ToList();
        _g = genetics;
        _s = settings ?? new PlannerSettings();
        _score = new PairScorer(_cats.Where(c => c.IsHome), _g);
    }

    public Plan Build(long day = 0)
    {
        var plan = new Plan { Day = day, Rooms = _rooms };
        foreach (var c in _cats) plan.CatNames[c.Id] = c.Name;
        if (_rooms.Count == 0) { plan.Notes.Add("Open the house screen to get a plan."); return plan; }

        CurrentAlerts(plan);
        EvaluateStrays(plan);

        // Inside the house: cats in a breeding room, cats waiting to be placed (back from a run, new kittens in the pipe),
        // and the strays worth taking in.
        var keepStrays = plan.Strays.Where(v => v.Keep).Select(v => v.Cat).ToList();
        var strayIds = plan.Strays.Select(v => v.Cat.Id).ToHashSet();
        var placed = _cats.Where(c => c.IsHome && (InRoom(c) || !strayIds.Contains(c.Id))).Concat(keepStrays).Distinct().ToList();

        var assignment = new RoomSearch(placed, _rooms, _g, _s, _score).Run();
        foreach (var (cat, room) in assignment)
        {
            plan.Assignment[cat.Id] = room;
            if (cat.Room != room) plan.Moves.Add(new Move { Cat = cat, From = cat.Room, To = room });
        }
        plan.Moves.Sort((a, b) => string.CompareOrdinal(a.Cat.Name, b.Cat.Name));

        foreach (var room in _rooms)
        {
            var members = assignment.Where(t => t.room == room.Name).Select(t => t.cat).ToList();
            foreach (var (a, b) in RoomSearch.BestMatching(members, room, _g, _s, _score, out _))
                plan.Pairs.Add(new PairPick
                {
                    A = a, B = b, Room = room.Name,
                    Ceiling = Breeding.Ceiling(a, b), Sevens = Breeding.Sevens(a, b),
                    Expected = Breeding.Expected(a, b, room.Stimulation),
                    DefectRisk = _score.Risk(a, b), Lovers = Breeding.AreLovers(a, b),
                });
        }
        plan.Pairs.Sort((x, y) => y.Ceiling != x.Ceiling ? y.Ceiling.CompareTo(x.Ceiling) : y.Expected.CompareTo(x.Expected));

        FindConflicts(plan, placed);
        PickSquad(plan, placed);
        JudgeKittens(plan, placed);
        PickDonations(plan, placed);
        return plan;
    }

    bool InRoom(CatInfo c) => c.Room != null && _rooms.Any(r => r.Name == c.Room);

    /// <summary>
    /// A stray is a cat outside the breeding rooms that the player has not decided on yet: a fresh adult with no
    /// relative in the house. Cats back from a run (retired) and kittens are not strays: they just need a room.
    /// </summary>
    bool IsStray(CatInfo c, List<CatInfo> placedCats)
        => c.IsHome && !InRoom(c) && !c.IsRetired && !c.IsKitten
           && placedCats.All(h => _score.Kinship(c, h) <= 1e-9)
           && !placedCats.Any(h => h.LoverId == c.Id || h.RivalId == c.Id || c.LoverId == h.Id || c.RivalId == h.Id);

    // ---------------------------------------------------------------- alerts

    void CurrentAlerts(Plan plan)
    {
        foreach (var grp in _cats.Where(c => c.IsHome && InRoom(c)).GroupBy(c => c.Room!))
        {
            var list = grp.ToList();
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                {
                    var a = list[i]; var b = list[j];
                    if (Breeding.AreRivals(a, b))
                        plan.Alerts.Add($"{a.Name} and {b.Name} hate each other and share {Rooms.Display(grp.Key)}.");
                    if (Breeding.CouldBreed(a, b, _g))
                    {
                        double r = _score.Risk(a, b);
                        if (r > _s.MaxDefectRisk + 1e-9)
                            plan.Alerts.Add($"{a.Name} and {b.Name} are related ({Pct(r)} defect risk for a kitten) and share {Rooms.Display(grp.Key)}.");
                    }
                }
        }
    }

    // ---------------------------------------------------------------- strays

    void EvaluateStrays(Plan plan)
    {
        var home = _cats.Where(c => c.IsHome && InRoom(c)).ToList();
        var outdoors = _cats.Where(c => IsStray(c, home)).ToList();
        if (outdoors.Count == 0) return;

        var best = new int[7]; var holders = new int[7];
        foreach (var c in home) for (int i = 0; i < 7; i++) best[i] = Math.Max(best[i], c.Stats[i]);
        foreach (var c in home) for (int i = 0; i < 7; i++) if (c.Stats[i] == best[i]) holders[i]++;

        double topStim = _rooms.Max(r => r.Stimulation);
        var adults = home.Where(c => !c.IsKitten && !c.IsOld).ToList();
        int topCeiling = 0;
        for (int i = 0; i < adults.Count; i++)
            for (int j = i + 1; j < adults.Count; j++)
                if (Breeding.IsBreedingPair(adults[i], adults[j], _g) && _score.Risk(adults[i], adults[j]) <= _s.MaxDefectRisk)
                    topCeiling = Math.Max(topCeiling, Breeding.Ceiling(adults[i], adults[j]));

        int females = adults.Count(c => c.Sex == Sex.Female && c.Sexuality < 0.7);
        int males = adults.Count(c => c.Sex == Sex.Male && c.Sexuality < 0.7);

        foreach (var s in outdoors)
        {
            var v = new StrayVerdict { Cat = s };
            var reasons = new List<string>();
            CatInfo? partner = null; double bestQ = double.MinValue;
            foreach (var h in adults)
            {
                if (!Breeding.SexesCompatible(s, h) || _score.Risk(s, h) > _s.MaxDefectRisk) continue;
                double q = _score.Quality(s, h, topStim);
                if (q > bestQ) { bestQ = q; partner = h; }
            }
            v.BestPartner = partner;
            v.BestCeiling = partner == null ? 0 : Breeding.Ceiling(s, partner);

            var newBest = Enumerable.Range(0, 7).Where(i => s.Stats[i] > best[i]).Select(i => $"{Breeding.StatNames[i]} {s.Stats[i]}").ToList();
            var rare = Enumerable.Range(0, 7).Where(i => s.Stats[i] == best[i] && holders[i] == 1 && best[i] >= 6).Select(i => $"{Breeding.StatNames[i]} {s.Stats[i]}").ToList();
            bool scarce = s.Sex == Sex.Neutral || (s.Sex == Sex.Female && females < males);
            bool strongPair = partner != null && v.BestCeiling >= topCeiling;
            bool decentPair = partner != null && v.BestCeiling >= topCeiling - 2;

            if (newBest.Count > 0) reasons.Add("best " + string.Join(", ", newBest) + " in the house");
            else if (rare.Count > 0) reasons.Add("only other cat with " + string.Join(", ", rare));
            if (strongPair) reasons.Add($"top pair with {partner!.Name} (up to {v.BestCeiling})");
            else if (decentPair && scarce) reasons.Add($"good with {partner!.Name} (up to {v.BestCeiling})");
            if (scarce && reasons.Count > 0) reasons.Add(s.Sex == Sex.Neutral ? "a \"?\" cat breeds with anyone" : "you are short on females");
            if (s.Sexuality >= 0.7 && s.Sex != Sex.Neutral) reasons.Clear();   // gay cats will not have kittens

            v.Keep = reasons.Count > 0;
            if (v.Keep)
            {
                if (s.Aggression >= 0.75) reasons.Add("aggressive, keep it away from other fighters");
                v.Reason = Capitalize(string.Join("; ", reasons)) + ".";
            }
            else
            {
                var why = new List<string>();
                if (s.Sexuality >= 0.7 && s.Sex != Sex.Neutral) why.Add("gay, so it will not have kittens");
                else if (partner == null) why.Add("no safe partner in the house");
                else why.Add($"nothing your cats lack (best pair up to {v.BestCeiling}, your best is {topCeiling})");
                v.Reason = Capitalize(string.Join("; ", why)) + ".";
            }
            plan.Strays.Add(v);
        }
    }

    // ---------------------------------------------------------------- conflicts no layout can fix

    void FindConflicts(Plan plan, List<CatInfo> placed)
    {
        var inPair = plan.Pairs.SelectMany(p => new[] { p.A.Id, p.B.Id }).ToHashSet();
        foreach (var grp in placed.GroupBy(c => plan.Assignment[c.Id]))
        {
            var m = grp.ToList();
            for (int i = 0; i < m.Count; i++)
                for (int j = i + 1; j < m.Count; j++)
                {
                    var a = m[i]; var b = m[j];
                    bool rivals = Breeding.AreRivals(a, b);
                    bool risky = Breeding.CouldBreed(a, b, _g) && _score.Risk(a, b) > _s.MaxDefectRisk + 1e-9;
                    if (!rivals && !risky) continue;
                    // The one to send away: not in a planned pair, then can still go on a run, then the weaker one.
                    var victim = new[] { a, b }
                        .OrderBy(c => inPair.Contains(c.Id) ? 1 : 0)
                        .ThenBy(c => c.IsRetired || c.IsKitten || c.IsOld ? 1 : 0)
                        .ThenBy(c => c.StatSum).ThenBy(c => c.Name, StringComparer.Ordinal).First();
                    plan.Conflicted.Add(victim.Id);
                    string what = rivals ? "hate each other" : $"are related ({Pct(_score.Risk(a, b))} defect risk)";
                    plan.Warnings.Add($"{a.Name} and {b.Name} {what}, and no room layout keeps them apart. Send {victim.Name} on a run or donate it.");
                }
        }
    }

    // ---------------------------------------------------------------- squad

    void PickSquad(Plan plan, List<CatInfo> placed)
    {
        var inPair = new Dictionary<long, PairPick>();
        foreach (var p in plan.Pairs) { inPair[p.A.Id] = p; inPair[p.B.Id] = p; }

        // Cats already waiting in the adventure box are the squad the player chose.
        var boxed = _cats.Where(c => c.InAdventureBox && !c.IsAway).ToList();
        foreach (var c in boxed) plan.Squad.Add(new SquadPick { Cat = c, Reason = "already in the adventure box" });
        if (plan.Squad.Count >= _s.SquadSize) return;

        var eligible = placed.Where(c => !c.IsRetired && !c.IsKitten && !c.IsOld && !c.IsAway && !c.InAdventureBox).ToList();
        double Strength(CatInfo c) => c.StatSum + 0.4 * c.SpellCount + 0.6 * c.PassiveCount + c.Level;

        // Cost of taking a breeder away: how much its room loses.
        double BreederCost(CatInfo c)
        {
            if (!inPair.TryGetValue(c.Id, out var p)) return 0;
            var room = _rooms.First(r => r.Name == p.Room);
            var members = plan.Assignment.Where(kv => kv.Value == p.Room).Select(kv => _cats.First(x => x.Id == kv.Key)).ToList();
            RoomSearch.BestMatching(members, room, _g, _s, _score, out double with);
            RoomSearch.BestMatching(members.Where(x => x.Id != c.Id).ToList(), room, _g, _s, _score, out double without);
            return with - without;
        }

        foreach (var c in eligible.Where(c => plan.Conflicted.Contains(c.Id)).OrderByDescending(Strength))
        {
            if (plan.Squad.Count >= _s.SquadSize) break;
            plan.Squad.Add(new SquadPick { Cat = c, Reason = "no safe room for it tonight" });
        }
        var taken = plan.Squad.Select(x => x.Cat.Id).ToHashSet();
        var free = eligible.Where(c => !inPair.ContainsKey(c.Id) && !taken.Contains(c.Id)).OrderByDescending(Strength).ThenBy(c => c.Name, StringComparer.Ordinal).ToList();
        var breeders = eligible.Where(c => inPair.ContainsKey(c.Id) && !taken.Contains(c.Id)).Select(c => (c, cost: BreederCost(c)))
                               .OrderBy(t => t.cost).ThenByDescending(t => Strength(t.c)).ToList();

        foreach (var c in free)
        {
            if (plan.Squad.Count >= _s.SquadSize) break;
            plan.Squad.Add(new SquadPick { Cat = c, Reason = $"no breeding role, stats {c.StatSum}" });
        }
        foreach (var (c, cost) in breeders)
        {
            if (plan.Squad.Count >= _s.SquadSize) break;
            plan.Squad.Add(new SquadPick { Cat = c, Reason = cost < 1 ? "its room still breeds well without it" : "fills the last slot; its pair stops breeding while it is away" });
        }
        if (plan.Squad.Count < _s.SquadSize)
            plan.SquadReadyTomorrow.AddRange(placed.Where(c => c.IsKitten && !c.IsRetired).OrderByDescending(c => c.StatSum));
    }

    // ---------------------------------------------------------------- kittens & donations

    void JudgeKittens(Plan plan, List<CatInfo> placed)
    {
        var adults = placed.Where(c => !c.IsKitten).ToList();
        if (adults.Count == 0) return;
        var sums = adults.Select(c => c.StatSum).OrderBy(x => x).ToList();
        int median = sums[sums.Count / 2];
        foreach (var k in placed.Where(c => c.IsKitten))
        {
            int sevens = k.Stats.Count(v => v >= 7);
            var note = new KittenNote { Cat = k };
            if (sevens > 0 || k.StatSum >= median + 1)
            {
                note.Promising = true;
                note.Reason = sevens > 0
                    ? $"{string.Join(", ", Enumerable.Range(0, 7).Where(i => k.Stats[i] >= 7).Select(i => Breeding.StatNames[i] + " 7"))}, sum {k.StatSum}: run it, then breed it"
                    : $"sum {k.StatSum}, above your average: run it, then breed it";
            }
            else note.Reason = $"sum {k.StatSum}, below your average of {median}";
            plan.Kittens.Add(note);
        }
    }

    void PickDonations(Plan plan, List<CatInfo> placed)
    {
        var inPair = new HashSet<long>(plan.Pairs.SelectMany(p => new[] { p.A.Id, p.B.Id }));
        var inSquad = new HashSet<long>(plan.Squad.Select(s => s.Cat.Id));
        var adults = placed.Where(c => !c.IsKitten).ToList();
        if (adults.Count == 0) return;
        var sums = adults.Select(c => c.StatSum).OrderBy(x => x).ToList();
        int median = sums[sums.Count / 2];

        var best = new int[7]; var holders = new int[7];
        foreach (var c in placed) for (int i = 0; i < 7; i++) best[i] = Math.Max(best[i], c.Stats[i]);
        foreach (var c in placed) for (int i = 0; i < 7; i++) if (c.Stats[i] == best[i]) holders[i]++;
        bool OnlyHolder(CatInfo c) => Enumerable.Range(0, 7).Any(i => c.Stats[i] == best[i] && holders[i] == 1 && best[i] >= 6);

        int capacity = _rooms.Count * _s.RoomSoftCap;
        bool crowded = placed.Count > capacity;

        foreach (var c in placed.OrderBy(c => c.StatSum).ThenBy(c => c.Name, StringComparer.Ordinal))
        {
            if (inPair.Contains(c.Id) || inSquad.Contains(c.Id)) continue;
            string? why = null;
            bool gay = c.Sexuality >= 0.7 && c.Sex != Sex.Neutral;
            if (plan.Conflicted.Contains(c.Id)) why = "no room without a relative or a rival for it";
            else if (c.IsKitten)
            {
                var k = plan.Kittens.FirstOrDefault(x => x.Cat.Id == c.Id);
                if (k != null && !k.Promising && !OnlyHolder(c)) why = $"weak kitten ({k.Reason})";
            }
            else if (c.IsOld && c.IsRetired) why = "old and retired: it can no longer breed or fight";
            else if (c.IsRetired && gay) why = "retired and gay: it will not have kittens";
            else if (c.IsRetired && !OnlyHolder(c)) why = "retired with no breeding partner in the plan";
            else if (!c.IsRetired && !OnlyHolder(c) && (c.StatSum <= median - 2 || crowded) && !c.IsOld)
                why = c.StatSum <= median - 2 ? $"weakest stats ({c.StatSum}), no breeding role and not needed for the squad"
                                              : "no breeding role and the house is over capacity";
            if (why != null) plan.Donations.Add(new DonationPick { Cat = c, Reason = why });
        }
    }

    static string Pct(double r) => $"{Math.Round(r * 100)}%";
    static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

/// <summary>Display names of the house rooms.</summary>
public static class Rooms
{
    public static string Display(string? name) => name switch
    {
        null => "outdoors",
        "Floor1_Large" => "1st Floor Left",
        "Floor1_Small" => "1st Floor Right",
        "Floor2_Small" => "2nd Floor Left",
        "Floor2_Large" => "2nd Floor Right",
        "Attic" => "Attic",
        _ when name.StartsWith("Basement", StringComparison.Ordinal) && int.TryParse(name.AsSpan(8), out int n) => $"Basement {n + 1}",
        _ => name.Replace('_', ' '),
    };
}
