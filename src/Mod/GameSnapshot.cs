using System;
using System.Collections.Generic;
using System.Linq;
using Pedigree.Core;
using MewgenicsModSdk.Game;

namespace Pedigree;

/// <summary>
/// Reads the live house into the planner's game-independent model. This is the only file (with the actions in
/// PedigreeMod) that touches game memory, so a game update that moves things around only breaks this layer.
/// Call on the game thread.
/// </summary>
internal sealed class GameSnapshot : IGenetics
{
    public readonly List<CatInfo> Cats = new();
    public readonly List<RoomState> Rooms = new();
    public readonly Dictionary<long, GameChar> Chars = new();
    public long Day;

    readonly Dictionary<(long, long), double> _kin = new();
    readonly Dictionary<(long, long), double> _att = new();

    public static GameSnapshot Take(Action<string>? log = null, ISet<long>? staged = null)
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

        foreach (var c in all.Values)
        {
            string? room = GameBreeding.RoomOf(c)?.Name;
            // Not GameChar.IsInAdventureBox: it looks for the box inside 1st Floor Right, where it was in an older
            // build. Cats the mod put in the box are known here; the planner recognises the others.
            bool inBox = room == null && staged != null && staged.Contains(c.CatId);
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
        return s;
    }

    static (long, long) Key(CatInfo a, CatInfo b) => a.Id < b.Id ? (a.Id, b.Id) : (b.Id, a.Id);

    public double Kinship(CatInfo a, CatInfo b)
    {
        var k = Key(a, b);
        if (_kin.TryGetValue(k, out var v)) return v;
        v = Chars.TryGetValue(a.Id, out var ca) && Chars.TryGetValue(b.Id, out var cb) ? GameBreeding.Kinship(ca, cb) : 0;
        if (double.IsNaN(v)) v = 0;
        _kin[k] = v;
        return v;
    }

    public double Attraction(CatInfo a, CatInfo b)
    {
        var k = Key(a, b);
        if (_att.TryGetValue(k, out var v)) return v;
        v = 0;
        if (Chars.TryGetValue(a.Id, out var ca) && Chars.TryGetValue(b.Id, out var cb))
            v = Math.Max(GameBreeding.PairingChance(ca, cb), GameBreeding.PairingChance(cb, ca));
        if (double.IsNaN(v)) v = 0;
        _att[k] = v;
        return v;
    }
}
