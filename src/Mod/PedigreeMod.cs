using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Pedigree.Core;
using MewgenicsModSdk;
using MewgenicsModSdk.Game;

namespace Pedigree;

/// <summary>
/// Pedigree: room plan with one-click arrange, run squad, strays worth taking in, donation suggestions.
/// Everything lives in the F10 mod menu; the full plan opens in its own window. It never donates or deletes cats.
/// </summary>
public sealed class PedigreeMod : MewgenicsMod
{
    public override string Id => "pedigree";
    public override string Name => "Pedigree";
    public override string Version => "0.1.11";
    public override string Description =>
        "Room plan, run squad, recruits and donations. Moves cats only when you press a button.";
    public override string Category => "Quality of Life";
    public override bool AutoEnable => true;

    // ---- F10 menu (the SDK registers these fields when the mod loads) ----
    readonly ButtonSetting _show;
    readonly ButtonSetting _arrange;
    readonly ButtonSetting _stageButton;
    readonly ButtonSetting _undo;
    readonly BoolSetting _morning = new("morning_popup", "Morning summary popup", true);
    readonly ChoiceSetting _hotkey = new("hotkey", "Plan hotkey", "F9",
        new[] { ("none", "None"), ("F6", "F6"), ("F7", "F7"), ("F8", "F8"), ("F9", "F9"), ("F11", "F11") });
    readonly IntSetting _maxRisk = new("max_risk", "Max pair defect risk %", 5, 2, 40);

    bool _supported;
    bool _subscribed;
    bool _refreshStatus = true;
    bool _morningDue;
    int _frame;
    /// <summary>Where each moved cat slept before the first arrange of the day: Undo puts them all back.</summary>
    readonly Dictionary<long, string> _dayStart = new();
    /// <summary>The squad move in progress, advanced a step at a time from the house update.</summary>
    volatile StageJob? _job;
    /// <summary>A world position known to be inside the adventure box (learned, then saved in box.txt).</summary>
    (double x, double y)? _boxSpot;
    int _houseFrame;
    volatile bool _planning;
    /// <summary>Room moves waiting to be checked (arrange or undo).</summary>
    volatile MoveCheck? _check;
    RoomSpots? _roomSpots;
    RoomSpots Spots => _roomSpots ??= new RoomSpots(Path.Combine(GameAssets.ModFolder(Id), "rooms.txt"), s => Log(s));
    long _lastMoveTick;

    sealed class MoveCheck
    {
        public string What = "";
        public readonly List<(long id, string name, string to, int slot, string label)> Items = new();
        public int CheckAt;
        public bool Retried;
        public string Note = "";
    }
    int _gameThreadId;

    sealed class StageJob
    {
        public readonly List<(long id, string name, string? from)> Cats = new();
        public readonly List<(double x, double y)> Spots = new();   // tried in order with the first cat
        public int SpotIdx;
        public bool Found;
        public bool Retried;
        public int CheckAt;
        public int Placed;
        public readonly List<string> Failed = new();
    }

    long _lastBackupDay = -1;

    public PedigreeMod()
    {
        _show = new ButtonSetting("show_plan", "Show plan", ShowPlan);
        _arrange = new ButtonSetting("arrange", "Auto-arrange rooms", Arrange);
        _stageButton = new ButtonSetting("stage_box", "Squad to adventure box", StageSquad);
        _undo = new ButtonSetting("undo", "Undo today's moves", Undo);
    }

    protected override void OnEnable()
    {
        _supported = GameBuild.IsSupported(GameBase);
        if (!_supported)
        {
            Status($"Off: needs game build {GameBuild.Supported}.");
            Log($"[Pedigree] unsupported game build (PE timestamp 0x{GameBuild.ReadPeTimestamp(GameBase):X8}); staying idle");
            return;
        }
        if (!_subscribed)
        {
            GameEvents.OnHouseUpdate += OnHouseUpdate;
            GameEvents.OnDayAdvanced += OnDayAdvanced;
            GameEvents.OnAdventureReturn += OnAdventureReturn;
            GameEvents.OnKeyDown += OnKeyDown;
            _subscribed = true;
        }
        TidyMenu();
        _refreshStatus = true;
        Status("Ready.");
    }

    protected override void OnDisable()
    {
        if (_subscribed)
        {
            GameEvents.OnHouseUpdate -= OnHouseUpdate;
            GameEvents.OnDayAdvanced -= OnDayAdvanced;
            GameEvents.OnAdventureReturn -= OnAdventureReturn;
            GameEvents.OnKeyDown -= OnKeyDown;
            _subscribed = false;
        }
    }

    // ---------------------------------------------------------------- events

    void OnDayAdvanced(DayAdvancedEvent e) { _morningDue = true; _refreshStatus = true; _dayStart.Clear(); _job = null; _check = null; }
    void OnAdventureReturn(AdventureReturnEvent e) { _refreshStatus = true; }

    void OnKeyDown(KeyEventArgs e)
    {
        if (e.IsRepeat || !_supported) return;
        var key = _hotkey.Value switch
        {
            "F6" => SDL_Scancode.F6, "F7" => SDL_Scancode.F7, "F8" => SDL_Scancode.F8,
            "F9" => SDL_Scancode.F9, "F11" => SDL_Scancode.F11, _ => SDL_Scancode.Unknown,
        };
        if (key != SDL_Scancode.Unknown && e.Scancode == key) GameThread.Enqueue(ShowPlan);
    }

    /// <summary>Every frame on the house screen: cheap unless a refresh or the morning summary is due.</summary>
    void OnHouseUpdate(HouseUpdateEvent e)
    {
        _gameThreadId = Environment.CurrentManagedThreadId;
        _houseFrame++;
        var job = _job;
        var check = _check;
        if (check != null && _houseFrame >= check.CheckAt) Timed("move check", () => StepCheck(check));
        if (job != null) { if (_houseFrame >= job.CheckAt) Timed("squad step", () => StepStage(job)); }
        else if (_houseFrame % 300 == 0) Timed("box scan", LearnBoxSpot);
        if (!_refreshStatus && !_morningDue) return;
        if (++_frame % 30 != 0) return;               // about twice a second
        if (_planning || GameFlow.InterstitialCount > 0) return;   // wait for the morning screens to close
        _refreshStatus = false;
        Planned("refresh", (plan, snap) =>
        {
            Status(Report.Status(plan));
            if (_morningDue)
            {
                _morningDue = false;
                var text = Report.Morning(plan);
                if (_morning.Value && text.Length > 0) GameMessages.Show($"Pedigree · Day {snap.Day}", text);
            }
        }, onNoHouse: () => _refreshStatus = true);   // house not on screen yet: try again later
    }

    /// <summary>
    /// Reads the house on the game thread, plans on a worker thread (the search takes up to a few hundred ms, which
    /// would stall the game for that long), then runs <paramref name="then"/> back on the game thread.
    /// </summary>
    void Planned(string what, Action<Plan, GameSnapshot> then, Action? onNoHouse = null)
    {
        if (!_supported) return;
        if (_planning) { Status("Working..."); return; }
        if (_gameThreadId != 0 && Environment.CurrentManagedThreadId != _gameThreadId)
        {
            GameThread.Enqueue(() => Planned(what, then, onNoHouse));
            return;
        }
        _planning = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        GameSnapshot snap;
        try { snap = GameSnapshot.Take(Log); }
        catch (Exception ex) { _planning = false; Fail(what, ex); return; }
        long readMs = sw.ElapsedMilliseconds;
        if (snap.Rooms.Count == 0)
        {
            _planning = false;
            if (onNoHouse != null) onNoHouse(); else Status("Open the house screen first.");
            return;
        }
        var settings = new PlannerSettings { MaxDefectRisk = Math.Clamp(_maxRisk.Value, 2, 40) / 100.0 };
        System.Threading.Tasks.Task.Run(() =>
        {
            var planSw = System.Diagnostics.Stopwatch.StartNew();
            Plan plan;
            try { snap.DropGone(Log); }
            catch (Exception ex) { Log($"[Pedigree] checking for departed cats failed: {ex.Message}"); }
            try { LearnRooms(snap); }
            catch (Exception ex) { Log($"[Pedigree] learning room spots failed: {ex.Message}"); }
            try { plan = new Planner(snap.Cats, snap.Rooms, snap, settings).Build(snap.Day); }
            catch (Exception ex) { _planning = false; GameThread.Enqueue(() => Fail(what, ex)); return; }
            Log($"[Pedigree] timing {what}: read {readMs} ms on game thread, plan {planSw.ElapsedMilliseconds} ms on worker");
            GameThread.Enqueue(() =>
            {
                _planning = false;
                Timed(what, () => Safe(what, () => then(plan, snap)));
            });
        });
    }

    /// <summary>Runs game-thread work and logs it if it took long enough to show as a hitch.</summary>
    void Timed(string what, Action action)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        action();
        if (sw.ElapsedMilliseconds >= 20) Log($"[Pedigree] timing {what}: {sw.ElapsedMilliseconds} ms on game thread");
    }

    void Fail(string what, Exception ex)
    {
        _refreshStatus = false;
        _morningDue = false;
        Status($"Error in {what}; nothing changed. See mods_debug.log.");
        Log($"[Pedigree] {what} failed: {ex}");
    }

    // ---------------------------------------------------------------- buttons

    void ShowPlan() => Planned("show plan", (plan, snap) =>
    {
        Status(Report.Status(plan));
        GameMessages.Show($"Pedigree · Day {snap.Day}", Report.Full(plan));
        LogHouseLayout(snap);
    });

    /// <summary>
    /// Diagnostics for the adventure box: logs every container the game reports (not only breeding rooms) and every
    /// cat's position. Positions are read on a worker thread because the SDK refreshes them only off the game thread.
    /// </summary>
    void LogHouseLayout(GameSnapshot snap)
    {
        try
        {
            foreach (var r in GameBreeding.GetRooms())
                Log($"[Pedigree] layout: container {r.Name} breeding={r.IsBreedingRoom} cats={r.CatCount}");
        }
        catch (Exception ex) { Log($"[Pedigree] layout rooms failed: {ex.Message}"); }
        var chars = snap.Chars.Values.ToList();
        var rooms = snap.Cats.ToDictionary(c => c.Id, c => c.Room ?? (c.InAdventureBox ? "(box)" : "(none)"));
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                foreach (var ch in chars)
                {
                    var (x, y) = ch.Position;
                    Log($"[Pedigree] layout: cat {ch.Name} room={rooms.GetValueOrDefault(ch.CatId, "?")} pos=({x:0.00}, {y:0.00}) inBox={ch.IsInAdventureBox}");
                }
            }
            catch (Exception ex) { Log($"[Pedigree] layout positions failed: {ex.Message}"); }
        });
    }

    void Arrange()
    {
        if (Mew.IsInCombat) { Status("Not during a fight."); return; }
        if (Busy()) return;
        Planned("arrange", ArrangeWith);
    }

    void ArrangeWith(Plan plan, GameSnapshot snap)
    {
        if (plan.Moves.Count == 0) { Status("Rooms already match the plan."); return; }

        string backup = BackupSave(snap.Day);
        var check = new MoveCheck { What = "arrange", Note = backup.Length > 0 ? " Save backed up." : "" };
        var slots = new Dictionary<string, int>();
        foreach (var m in plan.Moves)
        {
            if (!snap.Chars.TryGetValue(m.Cat.Id, out var ch) || !ch.IsLiveAndValid) continue;
            // Remember only the first room of the day, so Undo goes back to the morning layout.
            if (!_dayStart.ContainsKey(m.Cat.Id) && m.From != null && snap.Rooms.Any(r => r.Name == m.From)) _dayStart[m.Cat.Id] = m.From;
            int slot = slots.GetValueOrDefault(m.To);
            slots[m.To] = slot + 1;
            PlaceInRoom(m.Cat.Id, m.To, slot);
            check.Items.Add((m.Cat.Id, m.Cat.Name, m.To, slot, $"{m.Cat.Name} {m.From ?? "outside"}->{m.To}"));
        }
        StartCheck(check);
    }

    bool Busy()
    {
        if (_job != null) { Status("Still moving the squad..."); return true; }
        if (_check != null) { Status("Still moving cats..."); return true; }
        return false;
    }

    /// <summary>
    /// Puts a cat in a room by position, at the room's learned middle (spread out by <paramref name="slot"/>), or with
    /// the SDK's room-center move for a room no cat has been seen in yet.
    /// </summary>
    void PlaceInRoom(long id, string room, int slot)
    {
        _lastMoveTick = Environment.TickCount64;
        if (Spots.Spot(room, slot) is { } spot) { Teleport(id, spot); return; }
        var ch = GameWorld.Current.GetCatById(id);
        if (ch is { IsLiveAndValid: true } cat) cat.MoveToRoomCenter(room);
    }

    void StartCheck(MoveCheck check)
    {
        if (check.Items.Count == 0) { Status("Nothing moved."); return; }
        check.CheckAt = _houseFrame + 30;   // half a second for the game to file the cats under their new rooms
        _check = check;
        Status("Moving cats...");
    }

    /// <summary>
    /// Half a second after a batch of room moves: cats not in their room get one more try at the room's exact middle,
    /// and anything still off after that is reported instead of counted as moved.
    /// </summary>
    void StepCheck(MoveCheck check) => Safe(check.What, () =>
    {
        try
        {
            var roomOf = RoomOfAll();
            var off = check.Items.Where(i => roomOf.GetValueOrDefault(i.id) != i.to).ToList();
            if (off.Count > 0 && !check.Retried)
            {
                check.Retried = true;
                foreach (var i in off)
                {
                    Log($"[Pedigree] {check.What}: {i.name} ended up in {roomOf.GetValueOrDefault(i.id) ?? "(none)"}, not {i.to}; trying again");
                    PlaceInRoom(i.id, i.to, 0);
                }
                check.CheckAt = _houseFrame + 30;
                return;
            }
            _check = null;
            var done = check.Items.Except(off).Select(i => i.label).ToList();
            var failed = off.Select(i => $"{i.name} (in {roomOf.GetValueOrDefault(i.id) ?? "none"}, meant for {i.to})").ToList();
            string verb = check.What == "undo" ? "Undid" : "Moved";
            Status($"{verb} {done.Count}." + (failed.Count > 0 ? $" {failed.Count} didn't stay (see log)." : "") + check.Note);
            Log($"[Pedigree] {check.What}: {(done.Count > 0 ? string.Join(", ", done) : "nothing")}" + (failed.Count > 0 ? $"; DIDN'T STAY: {string.Join(", ", failed)}" : ""));
            _refreshStatus = true;
        }
        catch { _check = null; throw; }
    });

    static Dictionary<long, string> RoomOfAll()
    {
        var map = new Dictionary<long, string>();
        foreach (var r in GameBreeding.GetRooms())
            foreach (var id in r.CatIds) map.TryAdd(id, r.Name);
        return map;
    }

    /// <summary>
    /// Records where each breeding room's cats stand (worker thread). Skipped for a few seconds after the mod moves
    /// cats, so a position the game hasn't caught up with can't be filed under the wrong room.
    /// </summary>
    void LearnRooms(GameSnapshot snap)
    {
        if (Environment.TickCount64 - _lastMoveTick < 3000 || _check != null || _job != null) return;
        foreach (var room in snap.Rooms)
        {
            var pts = snap.Cats.Where(c => c.Room == room.Name && snap.Chars.ContainsKey(c.Id))
                .Select(c => snap.Chars[c.Id].Position).ToList();
            Spots.Observe(room.Name, pts);
        }
        Spots.SaveIfChanged();
    }

    /// <summary>
    /// Moves the suggested squad into the adventure box. The game files a cat under whichever container its new
    /// position falls in, so the first cat is placed at a known box position (or, until one is known, tried at a short
    /// list of likely spots right of the house) and checked a few frames later. Once one cat lands in the box, the rest
    /// follow to the same spot. Any cat that doesn't land goes back to the room it came from.
    /// </summary>
    void StageSquad()
    {
        if (Mew.IsInCombat) { Status("Not during a fight."); return; }
        if (Busy()) return;
        Planned("stage squad", StageWith);
    }

    void StageWith(Plan plan, GameSnapshot snap)
    {
        var todo = plan.Squad.Where(x => !x.Cat.InAdventureBox).Select(x => x.Cat).ToList();
        if (todo.Count == 0) { Status(plan.Squad.Count > 0 ? "Squad already in the box." : "Nobody can go on a run."); return; }

        var job = new StageJob();
        foreach (var cat in todo)
            if (snap.Chars.TryGetValue(cat.Id, out var ch) && ch.IsLiveAndValid) job.Cats.Add((cat.Id, cat.Name, cat.Room));
        if (job.Cats.Count == 0) return;
        _boxSpot ??= LoadBoxSpot();
        if (_boxSpot is { } known) job.Spots.Add(known);
        foreach (var y in new[] { 1.9, 1.0, 0.4, 2.8 })
            foreach (var x in new[] { 38.5, 37.5, 39.5, 36.5, 40.5, 35.5, 41.5 })
                job.Spots.Add((x, y));
        job.Spots.Add((23.0, 1.9));   // the SDK's spot, right of the starter house

        foreach (var (id, _, from) in job.Cats)
            if (from != null) _dayStart.TryAdd(id, from);
        LogContainers("before staging");
        var first = job.Cats[0];
        Teleport(first.id, job.Spots[0]);
        job.CheckAt = _houseFrame + 20;
        _job = job;
        Status(_boxSpot != null ? "Moving squad to the box..." : "Finding the adventure box...");
    }

    /// <summary>One step of the squad move, on the game thread once the last move has had time to settle.</summary>
    void StepStage(StageJob job) => Safe("stage squad", () =>
    {
        try
        {
            if (!job.Found)
            {
                var (id, name, from) = job.Cats[0];
                if (InBox(id))
                {
                    job.Found = true;
                    job.Placed = 1;
                    var spot = job.Spots[job.SpotIdx];
                    Log($"[Pedigree] box: {name} landed in the box at ({spot.x:0.0}, {spot.y:0.0})");
                    SaveBoxSpot(spot);
                    double[] offsets = { 0.7, -0.7, 1.4, -1.4, 2.1 };
                    for (int i = 1; i < job.Cats.Count; i++)
                        Teleport(job.Cats[i].id, (spot.x + offsets[(i - 1) % offsets.Length], spot.y));
                    job.CheckAt = _houseFrame + 30;
                    if (job.Cats.Count == 1) Finish(job);
                    return;
                }
                if (++job.SpotIdx < job.Spots.Count)
                {
                    Teleport(id, job.Spots[job.SpotIdx]);
                    job.CheckAt = _houseFrame + 20;
                    return;
                }
                Log($"[Pedigree] box: no tried spot reached the box; moving {name} back to {from ?? "(none)"}");
                PutBack(id, from);
                job.Failed.AddRange(job.Cats.Select(c => c.name));
                Finish(job);
                return;
            }

            var spotFound = job.Spots[job.SpotIdx];
            var missing = job.Cats.Skip(1).Where(c => !InBox(c.id)).ToList();
            if (missing.Count > 0 && !job.Retried)
            {
                job.Retried = true;   // offsets may have landed outside the box: try the exact spot once
                foreach (var c in missing) Teleport(c.id, spotFound);
                job.CheckAt = _houseFrame + 30;
                return;
            }
            foreach (var c in job.Cats.Skip(1))
            {
                if (InBox(c.id)) { job.Placed++; Log($"[Pedigree] box: {c.name} is in the box"); continue; }
                Log($"[Pedigree] box: {c.name} didn't reach the box; moving it back to {c.from ?? "(none)"}");
                PutBack(c.id, c.from);
                job.Failed.Add(c.name);
            }
            Finish(job);
        }
        catch { _job = null; throw; }
    });

    void Finish(StageJob job)
    {
        _job = null;
        LogContainers("after staging");
        _refreshStatus = true;
        if (!job.Found)
            Status("Couldn't find the box. Drag one cat into it by hand once, then press this again.");
        else
            Status($"Box: {job.Placed} in." + (job.Failed.Count > 0 ? $" {job.Failed.Count} failed, back in their rooms." : ""));
    }

    static void Teleport(long id, (double x, double y) spot)
    {
        var ch = GameWorld.Current.GetCatById(id);
        if (ch is not { IsLiveAndValid: true } cat) return;
        cat.Position = spot;   // GameChar is a handle: the setter teleports the cat by id
    }

    static bool InBox(long id) => GameBreeding.GetRoom(GameSnapshot.BoxContainer)?.CatIds.Contains(id) == true;

    void PutBack(long id, string? room)
    {
        var ch = GameWorld.Current.GetCatById(id);
        if (room != null && ch.HasValue && ch.Value.IsLiveAndValid && GameBreeding.RoomOf(ch.Value)?.Name != room)
            PlaceInRoom(id, room, 0);
    }

    /// <summary>
    /// Every few seconds: if a cat is sitting in the adventure box (put there by hand or by the game), remember where,
    /// so the squad button knows a spot inside the box for this house.
    /// </summary>
    void LearnBoxSpot()
    {
        try
        {
            var box = GameBreeding.GetRoom(GameSnapshot.BoxContainer);
            if (box == null || box.CatCount == 0) return;
            if (GameWorld.Current.GetCatById(box.CatIds.First()) is not { IsLiveAndValid: true } cat) return;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var pos = cat.Position;
                    if (_boxSpot is { } old && Math.Abs(old.x - pos.Item1) < 0.05 && Math.Abs(old.y - pos.Item2) < 0.05) return;
                    Log($"[Pedigree] box: learned box spot ({pos.Item1:0.00}, {pos.Item2:0.00}) from {cat.Name}");
                    SaveBoxSpot(pos);
                }
                catch (Exception ex) { Log($"[Pedigree] box: reading box spot failed: {ex.Message}"); }
            });
        }
        catch (Exception ex) { Log($"[Pedigree] box: scan failed: {ex.Message}"); }
    }

    string BoxSpotFile => Path.Combine(GameAssets.ModFolder(Id), "box.txt");

    (double x, double y)? LoadBoxSpot()
    {
        try
        {
            if (!File.Exists(BoxSpotFile)) return null;
            var parts = File.ReadAllText(BoxSpotFile).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            return (double.Parse(parts[0], ci), double.Parse(parts[1], ci));
        }
        catch { return null; }
    }

    void SaveBoxSpot((double x, double y) spot)
    {
        _boxSpot = spot;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BoxSpotFile)!);
            File.WriteAllText(BoxSpotFile, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{spot.x:0.###} {spot.y:0.###}"));
        }
        catch (Exception ex) { Log($"[Pedigree] box: saving spot failed: {ex.Message}"); }
    }

    void LogContainers(string when)
    {
        try { Log($"[Pedigree] containers {when}: " + string.Join(", ", GameBreeding.GetRooms().Select(r => $"{r.Name}={r.CatCount}"))); }
        catch (Exception ex) { Log($"[Pedigree] containers {when} failed: {ex.Message}"); }
    }

    void Undo() => Safe("undo", () =>
    {
        if (Busy()) return;
        if (_dayStart.Count == 0) { Status("Nothing to undo today."); return; }
        var world = GameWorld.Current;
        var roomOf = RoomOfAll();
        var check = new MoveCheck { What = "undo" };
        var slots = new Dictionary<string, int>();
        foreach (var (id, room) in _dayStart)
        {
            var ch = world.GetCatById(id);
            if (!ch.HasValue || !ch.Value.IsLiveAndValid) continue;
            string? now = roomOf.GetValueOrDefault(id);
            if (now == room) continue;   // already where it started
            if (now == null) { Log($"[Pedigree] undo: skipped {ch.Value.Name}: not in the house any more"); continue; }   // donated, or away
            int slot = slots.GetValueOrDefault(room);
            slots[room] = slot + 1;
            PlaceInRoom(id, room, slot);
            check.Items.Add((id, ch.Value.Name ?? "", room, slot, $"{ch.Value.Name}->{room}"));
        }
        _dayStart.Clear();
        if (check.Items.Count == 0) { Status("Everyone is already back."); return; }
        StartCheck(check);
    });

    // ---------------------------------------------------------------- helpers

    /// <summary>Removes menu entries of older versions and refreshes the button labels in Mods\config\pedigree.cfg.</summary>
    void TidyMenu()
    {
        try
        {
            if (Config == null) return;
            foreach (var key in new[] { "stage_squad", "stage_squad__hint", "stage_squad__action" }) Config.Remove(key);
            foreach (var b in new[] { _show, _arrange, _stageButton, _undo })
            {
                Config.SetHint(b.Key, b.Label);
                Config.SetAction(b.Key, b.Label);
            }
            foreach (Setting st in new Setting[] { _morning, _hotkey, _maxRisk }) Config.SetHint(st.Key, st.Label);
        }
        catch (Exception ex) { Log($"[Pedigree] menu tidy failed: {ex.Message}"); }
    }

    /// <summary>Copies the newest save into the mod folder once per game day, before the first arrange. Keeps 10.</summary>
    string BackupSave(long day)
    {
        if (day == _lastBackupDay) return "";
        try
        {
            // Saves live in %APPDATA%\<studio>\<game>\<account>\saves; the game folder is named like its executable.
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Glaiel Games", Process.GetCurrentProcess().ProcessName);
            if (!Directory.Exists(root)) return "";
            var newest = Directory.EnumerateDirectories(root)
                .Select(d => Path.Combine(d, "saves"))
                .Where(Directory.Exists)
                .SelectMany(d => Directory.EnumerateFiles(d, "*.sav"))
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest == null) return "";
            string dir = Path.Combine(GameAssets.ModFolder(Id), "backups");
            Directory.CreateDirectory(dir);
            string dest = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(newest.Name)}_day{day}_{DateTime.Now:yyyyMMdd_HHmmss}.sav");
            File.Copy(newest.FullName, dest, overwrite: false);
            foreach (var old in new DirectoryInfo(dir).GetFiles("*.sav").OrderByDescending(f => f.CreationTimeUtc).Skip(10))
                old.Delete();
            _lastBackupDay = day;
            return dest;
        }
        catch (Exception ex)
        {
            Log($"[Pedigree] save backup failed: {ex.Message}");
            return "";
        }
    }

    void Status(string text)
    {
        try { Config?.SetStatus(text.Length > 190 ? text[..187] + "..." : text); } catch { }
    }

    void Safe(string what, Action action)
    {
        if (!_supported) return;
        try { action(); }
        catch (Exception ex)
        {
            _refreshStatus = false;
            _morningDue = false;
            Status($"Error in {what}; nothing changed. See mods_debug.log.");
            Log($"[Pedigree] {what} failed: {ex}");
        }
    }
}
