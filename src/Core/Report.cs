using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Pedigree.Core;

/// <summary>Turns a plan into the text the player reads.</summary>
public static class Report
{
    /// <summary>The full plan, for the report window.</summary>
    public static string Full(Plan p)
    {
        var sb = new StringBuilder();
        if (p.Rooms.Count == 0) return string.Join("\n", p.Notes);

        if (p.Alerts.Count > 0 || p.Warnings.Count > 0)
        {
            sb.AppendLine("NEEDS ATTENTION");
            foreach (var a in p.Alerts) sb.AppendLine("  • Now: " + a);
            foreach (var w in p.Warnings) sb.AppendLine("  • " + w);
            sb.AppendLine();
        }

        sb.AppendLine(p.Moves.Count == 0 ? "ROOMS (already set, nothing to move)" : $"ROOMS ({Plural(p.Moves.Count, "move")})");
        var cats = p.Assignment.Keys.ToHashSet();
        foreach (var room in p.Rooms.OrderByDescending(r => r.Stimulation).ThenBy(r => r.Name, StringComparer.Ordinal))
        {
            var members = p.Pairs.Where(x => x.Room == room.Name).ToList();
            var names = p.Assignment.Where(kv => kv.Value == room.Name).Select(kv => NameOf(p, kv.Key)).OrderBy(x => x, StringComparer.Ordinal).ToList();
            sb.AppendLine($"  {Rooms.Display(room.Name)}: {(names.Count == 0 ? "empty" : string.Join(", ", names))}");
            foreach (var pair in members)
                sb.AppendLine($"      {pair.A.Name} × {pair.B.Name}{(pair.Lovers ? " (bonded)" : "")}: up to {pair.Ceiling}{Sevens(pair)}");
        }
        if (p.Moves.Count > 0)
        {
            sb.AppendLine("  Moves:");
            foreach (var m in p.Moves)
                sb.AppendLine($"      {m.Cat.Name}: {(m.From == null || !p.Rooms.Any(r => r.Name == m.From) ? "bring inside" : Rooms.Display(m.From))} → {Rooms.Display(m.To)}");
        }
        sb.AppendLine();

        sb.AppendLine("RUN SQUAD");
        if (p.Squad.Count == 0) sb.AppendLine("  Nobody can go on a run right now.");
        foreach (var s in p.Squad) sb.AppendLine($"  {s.Cat.Name} ({s.Reason})");
        if (p.SquadReadyTomorrow.Count > 0)
            sb.AppendLine("  Ready tomorrow: " + string.Join(", ", p.SquadReadyTomorrow.Select(c => c.Name)));
        sb.AppendLine();

        var keepKittens = p.Kittens.Where(k => k.Promising).ToList();
        if (keepKittens.Count > 0)
        {
            sb.AppendLine("NEW KITTENS WORTH KEEPING");
            foreach (var k in keepKittens) sb.AppendLine($"  {k.Cat.Name}: {k.Reason}");
            sb.AppendLine();
        }

        if (p.Strays.Count > 0)
        {
            sb.AppendLine("STRAYS");
            foreach (var v in p.Strays) sb.AppendLine($"  {v.Cat.Name} ({StatLine(v.Cat)}): {(v.Keep ? "take in" : "pass")}. {v.Reason}");
            sb.AppendLine();
        }

        sb.AppendLine("DONATE");
        if (p.Donations.Count == 0) sb.AppendLine("  Nobody. Every cat has a job.");
        foreach (var d in p.Donations) sb.AppendLine($"  {d.Cat.Name}: {d.Reason}");

        foreach (var n in p.Notes) { sb.AppendLine(); sb.AppendLine(n); }
        return sb.ToString().TrimEnd();
    }

    /// <summary>One line for the bottom of the mod menu.</summary>
    public static string Status(Plan p)
    {
        if (p.Rooms.Count == 0) return p.Notes.FirstOrDefault() ?? "";
        var parts = new List<string>();
        if (p.Alerts.Count + p.Warnings.Count > 0) parts.Add(Plural(p.Alerts.Count + p.Warnings.Count, "alert"));
        parts.Add(p.Moves.Count == 0 ? "rooms OK" : Plural(p.Moves.Count, "move"));
        if (p.Squad.Count > 0) parts.Add("squad: " + string.Join(", ", p.Squad.Select(s => s.Cat.Name)));
        var keep = p.Strays.Where(s => s.Keep).Select(s => s.Cat.Name).ToList();
        if (keep.Count > 0) parts.Add("take in " + string.Join(", ", keep));
        if (p.Donations.Count > 0) parts.Add("donate " + string.Join(", ", p.Donations.Select(d => d.Cat.Name)));
        return string.Join(" · ", parts);
    }

    /// <summary>The short morning summary: only what changed or needs a decision.</summary>
    public static string Morning(Plan p)
    {
        var sb = new StringBuilder();
        foreach (var a in p.Alerts) sb.AppendLine("• " + a);
        foreach (var w in p.Warnings) sb.AppendLine("• " + w);
        foreach (var k in p.Kittens) sb.AppendLine($"• New kitten {k.Cat.Name}: {(k.Promising ? "keep" : "donate")} ({k.Reason}).");
        foreach (var v in p.Strays) sb.AppendLine($"• Stray {v.Cat.Name}: {(v.Keep ? "take in" : "pass")}. {v.Reason}");
        if (p.Moves.Count > 0) sb.AppendLine($"• {Plural(p.Moves.Count, "room move")} suggested (Auto-arrange in F10).");
        return sb.ToString().TrimEnd();
    }

    static string NameOf(Plan p, long id)
    {
        foreach (var m in p.Moves) if (m.Cat.Id == id) return m.Cat.Name;
        foreach (var x in p.Pairs) { if (x.A.Id == id) return x.A.Name; if (x.B.Id == id) return x.B.Name; }
        return p.CatNames.TryGetValue(id, out var n) ? n : id.ToString();
    }

    static readonly string[] Words = { "no", "one", "two", "three", "four", "five", "six", "seven" };
    static string Sevens(PairPick p) => p.Sevens switch { 0 => "", 1 => ", a 7 possible", var n => $", {Words[Math.Min(n, 7)]} 7s possible" };
    static string StatLine(CatInfo c) => string.Join("/", c.Stats) + $", sum {c.StatSum}";
    static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";
}
