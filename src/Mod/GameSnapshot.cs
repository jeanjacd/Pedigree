using System;
using System.Collections.Generic;
using System.Linq;
using Pedigree.Core;
using MewgenicsModSdk.Game;

namespace Pedigree;

/// <summary>
/// Reads the live house into the planner's game-independent model. This is the only file (with the actions in
/// PedigreeMod) that touches game memory, so a game update that moves things around only breaks this layer.
/// Take it on the game thread: every SDK read here is marshalled to that thread, so taking it anywhere else costs a
/// frame per read. <see cref="Take"/> also reads every pair's kinship and attraction up front, so the planner can then
/// run on a worker thread without touching the game.
/// </summary>
internal sealed class GameSnapshot : IGenetics
{
    public const string BoxContainer = "AdventureBox";

    public readonly List<CatInfo> Cats = new();
    public readonly List<RoomState> Rooms = new();
    public readonly Dictionary<long, GameChar> Chars = new();
    public long Day;

    readonly Dictionary<(long, long), double> _kin = new();
    readonly Dictionary<(long, long), double> _att = new();
    bool _frozen;   // after Take: a pair that wasn't read up front is treated as unrelated rather than read off-thread

    public static GameSnapshot Take(Action<string>? log = null)
    {
        var s = new GameSnapshot();
        if (!Mew.IsReady) return s;
        s.Day = Mew.Day;

        var roomInfos = GameBreeding.GetRooms();
        foreach (var r in roomInfos)
            if (r.IsBreedingRoom)
                s.Rooms.Add(new RoomState { Name = r.Name, Stimulation = r.Stimulation, ComfortBase = r.Comfort });
        if (s.Rooms.Count == 0) return s;   // not on the house screen

        var world = GameWorld.Current;
        if (!world.IsValid) return s;

        // The cats of the house. GetPlayerCats() is not used: its "player" flag marks fighting-team cats, so house
        // cats are missing from it. The raw cat database is not used either: it also keeps donated and dead cats.
        // Two sources, merged by id: Mew.Cats (alive and in the house) and the cats each room reports standing in it.
        var all = new Dictionary<long, GameChar>();
        int fromMew = 0, fromRooms = 0;
        foreach (var c in Mew.Cats) if (c.IsLiveAndValid && all.TryAdd(c.CatId, c)) fromMew++;
        foreach (var r in roomInfos)
            foreach (var id in r.CatIds)
                if (!all.ContainsKey(id) && world.GetCatById(id) is GameChar rc && rc.IsLiveAndValid) { all[id] = rc; fromRooms++; }
        log?.Invoke($"[Pedigree] snapshot: {s.Rooms.Count} rooms, {all.Count} cats (house list {fromMew}, room lists +{fromRooms})");

        // Which container each cat is in, from the one room read above. (GameBreeding.RoomOf reads every room again
        // per cat.)
        var roomOf = new Dictionary<long, string>();
        foreach (var r in roomInfos)
            foreach (var id in r.CatIds) roomOf.TryAdd(id, r.Name);

        foreach (var c in all.Values)
        {
            string? room = roomOf.GetValueOrDefault(c.CatId);
            // The box is a container of its own, "AdventureBox". (GameChar.IsInAdventureBox is not used: it checks a
            // spot inside 1st Floor Right and flags cats standing there.) A retired cat in the box just came back
            // from a run and still needs a room; any other cat in it was put there for the next run.
            bool inBox = room == BoxContainer && !(c.IsRetired || !c.CanGoOnAdventure);
            var info = new CatInfo
            {
                Id = c.CatId,
                Name = c.Name ?? "",
                Sex = c.Sex switch { CatSex.Female => Sex.Female, CatSex.Male => Sex.Male, _ => Sex.Neutral },
                Sexuality = c.Sexuality,
                Aggression = c.Aggression,
                Libido = c.Libido,
                LoverId = c.LoverId,
                RivalId = c.RivalId,
                IsKitten = c.IsKitten,
                IsOld = c.IsOld,
                IsRetired = c.IsRetired || !c.CanGoOnAdventure,
                InAdventureBox = inBox,
                // On an adventure: not in any room, not in the box, and listed in the party.
                IsAway = room == null && !inBox && c.IsInAdventureParty,
                Room = inBox ? null : room,
                Level = c.Level,
                ClassName = c.ClassName ?? "",
                SpellCount = c.SpellCount,
                PassiveCount = c.PassiveCount,
                DisorderCount = c.DisorderCount,
                Age = c.Age,
            };
            for (int i = 0; i < 7; i++) info.Stats[i] = c.BaseStat(i);
            s.Cats.Add(info);
            s.Chars[info.Id] = c;
        }

        for (int i = 0; i < s.Cats.Count; i++)
            for (int j = i + 1; j < s.Cats.Count; j++)
            {
                s.Kinship(s.Cats[i], s.Cats[j]);
                s.Attraction(s.Cats[i], s.Cats[j]);
            }
        s._frozen = true;
        return s;
    }

    /// <summary>
    /// Drops cats that are no longer in the house. A donated cat stays in the game's list of live cats (until the day
    /// ends, it seems) but is taken out of every container and loses its body, so its position reads as NaN. A stray
    /// waiting outside has no container either, but it has a position. Call off the game thread: positions are
    /// refreshed by the game thread on request, and waiting for that on the game thread itself would stall it.
    /// </summary>
    public void DropGone(Action<string>? log = null)
    {
        var gone = new List<CatInfo>();
        foreach (var c in Cats)
        {
            if (c.Room != null || c.InAdventureBox || c.IsAway) continue;
            if (!Chars.TryGetValue(c.Id, out var ch)) continue;
            var (x, y) = ch.Position;
            if (double.IsNaN(x) || double.IsNaN(y)) gone.Add(c);
        }
        foreach (var c in gone)
        {
            Cats.Remove(c);
            Gone.Add(c.Id);
            log?.Invoke($"[Pedigree] snapshot: {c.Name} has left the house (no room, no position); left out of the plan");
        }
    }

    /// <summary>Cats dropped by <see cref="DropGone"/>.</summary>
    public readonly HashSet<long> Gone = new();

    static (long, long) Key(CatInfo a, CatInfo b) => a.Id < b.Id ? (a.Id, b.Id) : (b.Id, a.Id);

    public double Kinship(CatInfo a, CatInfo b)
    {
        var k = Key(a, b);
        if (_kin.TryGetValue(k, out var v) || _frozen) return v;
        v = Chars.TryGetValue(a.Id, out var ca) && Chars.TryGetValue(b.Id, out var cb) ? GameBreeding.Kinship(ca, cb) : 0;
        if (double.IsNaN(v)) v = 0;
        _kin[k] = v;
        return v;
    }

    public double Attraction(CatInfo a, CatInfo b)
    {
        var k = Key(a, b);
        if (_att.TryGetValue(k, out var v) || _frozen) return v;
        v = 0;
        if (Chars.TryGetValue(a.Id, out var ca) && Chars.TryGetValue(b.Id, out var cb))
            v = Math.Max(GameBreeding.PairingChance(ca, cb), GameBreeding.PairingChance(cb, ca));
        if (double.IsNaN(v)) v = 0;
        _att[k] = v;
        return v;
    }
}
