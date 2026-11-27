using System;
using System.Collections.Generic;

namespace Pedigree.Core;

public enum Sex { Female, Male, Neutral }

/// <summary>A snapshot of one cat, independent of the game. The mod fills it from live memory; tests fill it from a save.</summary>
public sealed class CatInfo
{
    public long Id;
    public string Name = "";
    public Sex Sex;
    /// <summary>0 = straight, 1 = gay. Neutral cats ignore it.</summary>
    public double Sexuality;
    /// <summary>Base (heritable) stats: STR, DEX, CON, INT, SPD, CHA, LCK.</summary>
    public int[] Stats = new int[7];
    public double Aggression;
    public double Libido;
    public long LoverId = -1;
    public long RivalId = -1;
    public bool IsKitten;
    public bool IsOld;
    /// <summary>True when the cat can no longer go on an adventure (it came back from one).</summary>
    public bool IsRetired;
    /// <summary>On an adventure right now.</summary>
    public bool IsAway;
    /// <summary>Staged in the adventure box.</summary>
    public bool InAdventureBox;
    /// <summary>Room the cat is in, or null when it is outdoors (a stray at the door, or not placed yet).</summary>
    public string? Room;
    public int Level;
    public string ClassName = "";
    public int SpellCount;
    public int PassiveCount;
    public int DisorderCount;
    public int Age;

    public int StatSum { get { int s = 0; foreach (var v in Stats) s += v; return s; } }
    public bool IsOutdoors => Room == null && !IsAway && !InAdventureBox;
    public bool IsHome => !IsAway && !InAdventureBox;
    public override string ToString() => Name;
}

public sealed class RoomState
{
    public string Name = "";
    public double Stimulation;
    /// <summary>Comfort from furniture only (without the crowding penalty).</summary>
    public double ComfortBase;
}

/// <summary>Relatedness and attraction between cats, from the game (or a test double).</summary>
public interface IGenetics
{
    /// <summary>Inbreeding of a kitten of a and b (0..1).</summary>
    double Kinship(CatInfo a, CatInfo b);
    /// <summary>The game's pairing chance, the larger of both directions (0 when one is a kitten or blocked).</summary>
    double Attraction(CatInfo a, CatInfo b);
}

public sealed class PlannerSettings
{
    /// <summary>Highest acceptable chance of a birth defect for a pair (0..1). 0.05 allows only unrelated cats.</summary>
    public double MaxDefectRisk = 0.05;
    public int RoomSoftCap = 4;
    public int SquadSize = 4;
}

public sealed class Move
{
    public CatInfo Cat = null!;
    public string? From;
    public string To = "";
}

public sealed class PairPick
{
    public CatInfo A = null!, B = null!;
    public string Room = "";
    public int Ceiling;
    public double Expected;
    public double DefectRisk;
    public bool Lovers;
    public int Sevens;
}

public sealed class SquadPick
{
    public CatInfo Cat = null!;
    public string Reason = "";
}

public sealed class DonationPick
{
    public CatInfo Cat = null!;
    public string Reason = "";
}

public sealed class StrayVerdict
{
    public CatInfo Cat = null!;
    public bool Keep;
    public string Reason = "";
    public CatInfo? BestPartner;
    public int BestCeiling;
}

public sealed class KittenNote
{
    public CatInfo Cat = null!;
    public bool Promising;
    public string Reason = "";
}

public sealed class Plan
{
    public long Day;
    public List<RoomState> Rooms = new();
    /// <summary>Target room of every cat that should be inside.</summary>
    public Dictionary<long, string> Assignment = new();
    public Dictionary<long, string> CatNames = new();
    public List<Move> Moves = new();
    public List<PairPick> Pairs = new();
    public List<SquadPick> Squad = new();
    public List<CatInfo> SquadReadyTomorrow = new();
    public List<DonationPick> Donations = new();
    public List<StrayVerdict> Strays = new();
    public List<KittenNote> Kittens = new();
    /// <summary>Problems in the house as it is now (before any move).</summary>
    public List<string> Alerts = new();
    /// <summary>Problems no room layout can fix (a cat related to someone in every room): solved by a run or a donation.</summary>
    public List<string> Warnings = new();
    /// <summary>Cats that cannot be placed safely; they go first in the squad, or are suggested for donation.</summary>
    public HashSet<long> Conflicted = new();
    public List<string> Notes = new();
}
