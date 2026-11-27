using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Pedigree.Core;

// Runs the planner on fixtures exported from real saves and checks the rules the plan must always follow.
int failures = 0;
void Check(bool ok, string what) { if (!ok) { failures++; Console.WriteLine("  FAIL: " + what); } }

var dir = args.Length > 0 ? args[0] : "../fixtures";
foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f))
{
    var fx = Fixture.Load(file);
    Console.WriteLine($"=== {Path.GetFileName(file)} ({fx.Cats.Count} cats)");
    var settings = new PlannerSettings();
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var plan = new Planner(fx.Cats, fx.Rooms, fx, settings).Build();
    sw.Stop();
    Console.WriteLine(Report.Full(plan));
    Console.WriteLine("-- status: " + Report.Status(plan));
    Console.WriteLine("-- morning:\n" + Report.Morning(plan));
    Console.WriteLine($"-- planned in {sw.ElapsedMilliseconds} ms");

    var byId = fx.Cats.ToDictionary(c => c.Id);
    // 1. No rivals or risky pairs share a room in the plan.
    foreach (var grp in plan.Assignment.GroupBy(kv => kv.Value))
    {
        var m = grp.Select(kv => byId[kv.Key]).ToList();
        for (int i = 0; i < m.Count; i++)
            for (int j = i + 1; j < m.Count; j++)
            {
                bool flagged = plan.Warnings.Any(w => w.Contains(m[i].Name) && w.Contains(m[j].Name))
                               && (plan.Conflicted.Contains(m[i].Id) || plan.Conflicted.Contains(m[j].Id));
                Check(flagged || !Breeding.AreRivals(m[i], m[j]), $"rivals {m[i]} and {m[j]} share {grp.Key}");
                if (!flagged && Breeding.CouldBreed(m[i], m[j], fx))
                    Check(Breeding.DefectRisk(fx.Kinship(m[i], m[j])) <= settings.MaxDefectRisk + 1e-9,
                          $"related pair {m[i]} × {m[j]} ({Breeding.DefectRisk(fx.Kinship(m[i], m[j])):P0}) shares {grp.Key}");
            }
        int capTotal = fx.Rooms.Count * settings.RoomSoftCap;
        if (plan.Assignment.Count <= capTotal) Check(m.Count <= settings.RoomSoftCap, $"{grp.Key} has {m.Count} cats although everyone fits");
    }
    // 2. Squad is legal.
    foreach (var s in plan.Squad) Check(!s.Cat.IsRetired && !s.Cat.IsKitten && !s.Cat.IsOld, $"squad pick {s.Cat} cannot go on a run");
    Check(plan.Squad.Select(s => s.Cat.Id).Distinct().Count() == plan.Squad.Count, "duplicate squad pick");
    // 3. Donations never touch pairs or the squad.
    var busy = plan.Pairs.SelectMany(p => new[] { p.A.Id, p.B.Id }).Concat(plan.Squad.Select(s => s.Cat.Id)).ToHashSet();
    foreach (var d in plan.Donations) Check(!busy.Contains(d.Cat.Id), $"donation {d.Cat} is in a pair or the squad");
    // 4. Every pair is safe.
    foreach (var p in plan.Pairs) Check(p.DefectRisk <= settings.MaxDefectRisk + 1e-9 && !Breeding.AreRivals(p.A, p.B), $"unsafe pair {p.A} × {p.B}");
    // 5. Deterministic.
    var again = new Planner(fx.Cats, fx.Rooms, fx, settings).Build();
    Check(again.Assignment.OrderBy(k => k.Key).SequenceEqual(plan.Assignment.OrderBy(k => k.Key)), "plan is not deterministic");
    // 6. Stable: applying the moves and planning again asks for no more moves.
    var moved = fx.Cats.Select(c => Fixture.Clone(c)).ToList();
    foreach (var c in moved) if (plan.Assignment.TryGetValue(c.Id, out var r)) c.Room = r;
    var after = new Planner(moved, fx.Rooms, fx, settings).Build();
    Check(after.Moves.Count == 0, $"plan is not stable: {after.Moves.Count} more moves after applying it ({string.Join(", ", after.Moves.Select(x => x.Cat.Name + "→" + x.To))})");
    Check(after.Alerts.Count == plan.Warnings.Count, "unexpected alerts after applying the plan: " + string.Join(" | ", after.Alerts));
    // 7. Every cat that cannot be placed safely is either in the squad or suggested for donation.
    foreach (var id in plan.Conflicted)
        Check(plan.Squad.Any(s => s.Cat.Id == id) || plan.Donations.Any(d => d.Cat.Id == id), $"conflicted cat {byId[id]} has no way out");
    Console.WriteLine();
}
Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

sealed class Fixture : IGenetics
{
    public List<CatInfo> Cats = new();
    public List<RoomState> Rooms = new();
    Dictionary<string, double> _kin = new(), _att = new();

    static string Key(CatInfo a, CatInfo b) => $"{Math.Min(a.Id, b.Id)}-{Math.Max(a.Id, b.Id)}";
    public double Kinship(CatInfo a, CatInfo b) => _kin.TryGetValue(Key(a, b), out var v) ? v : 0;
    public double Attraction(CatInfo a, CatInfo b) => _att.TryGetValue(Key(a, b), out var v) ? v : 0;

    public static Fixture Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement; var f = new Fixture();
        foreach (var r in root.GetProperty("rooms").EnumerateArray())
            f.Rooms.Add(new RoomState { Name = r.GetProperty("Name").GetString()!, Stimulation = r.GetProperty("Stimulation").GetDouble(), ComfortBase = r.GetProperty("ComfortBase").GetDouble() });
        foreach (var c in root.GetProperty("cats").EnumerateArray())
            f.Cats.Add(new CatInfo
            {
                Id = c.GetProperty("Id").GetInt64(), Name = c.GetProperty("Name").GetString()!,
                Sex = Enum.Parse<Sex>(c.GetProperty("Sex").GetString()!), Sexuality = c.GetProperty("Sexuality").GetDouble(),
                Stats = c.GetProperty("Stats").EnumerateArray().Select(x => x.GetInt32()).ToArray(),
                Aggression = c.GetProperty("Aggression").GetDouble(), Libido = c.GetProperty("Libido").GetDouble(),
                LoverId = c.GetProperty("LoverId").GetInt64(), RivalId = c.GetProperty("RivalId").GetInt64(),
                IsKitten = c.GetProperty("IsKitten").GetBoolean(), IsOld = c.GetProperty("IsOld").GetBoolean(),
                IsRetired = c.GetProperty("IsRetired").GetBoolean(),
                Room = c.GetProperty("Room").ValueKind == JsonValueKind.Null ? null : c.GetProperty("Room").GetString(),
                Level = c.GetProperty("Level").GetInt32(), ClassName = c.GetProperty("ClassName").GetString()!,
                SpellCount = c.GetProperty("SpellCount").GetInt32(), PassiveCount = c.GetProperty("PassiveCount").GetInt32(),
                DisorderCount = c.GetProperty("DisorderCount").GetInt32(), Age = c.GetProperty("Age").GetInt32(),
            });
        foreach (var p in root.GetProperty("kinship").EnumerateObject()) f._kin[p.Name] = p.Value.GetDouble();
        foreach (var p in root.GetProperty("attraction").EnumerateObject()) f._att[p.Name] = p.Value.GetDouble();
        return f;
    }

    public static CatInfo Clone(CatInfo c) => new()
    {
        Id = c.Id, Name = c.Name, Sex = c.Sex, Sexuality = c.Sexuality, Stats = (int[])c.Stats.Clone(), Aggression = c.Aggression,
        Libido = c.Libido, LoverId = c.LoverId, RivalId = c.RivalId, IsKitten = c.IsKitten, IsOld = c.IsOld, IsRetired = c.IsRetired,
        IsAway = c.IsAway, InAdventureBox = c.InAdventureBox, Room = c.Room, Level = c.Level, ClassName = c.ClassName,
        SpellCount = c.SpellCount, PassiveCount = c.PassiveCount, DisorderCount = c.DisorderCount, Age = c.Age,
    };
}
