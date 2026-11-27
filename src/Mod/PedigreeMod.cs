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
    public override string Version => "0.1.7";
    public override string Description =>
        "Room plan, run squad, recruits and donations. Moves cats only when you press a button.";
    public override string Category => "Quality of Life";
    public override bool AutoEnable => true;

    // ---- F10 menu (the SDK registers these fields when the mod loads) ----
    readonly ButtonSetting _show;
    readonly ButtonSetting _arrange;
    readonly ButtonSetting _stage;
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
    /// <summary>Cats sent to the box, waiting for the delayed check: id -> (name, room they came from).</summary>
    readonly Dictionary<long, (string name, string? from)> _pendingStage = new();
    int _houseFrame, _stageCheckFrame;

    long _lastBackupDay = -1;

    public PedigreeMod()
    {
        _show = new ButtonSetting("show_plan", "Show plan", ShowPlan);
        _arrange = new ButtonSetting("arrange", "Auto-arrange rooms", Arrange);
        _stage = new ButtonSetting("stage_box", "Squad to adventure box", StageSquad);
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

    void OnDayAdvanced(DayAdvancedEvent e) { _morningDue = true; _refreshStatus = true; _dayStart.Clear(); _pendingStage.Clear(); }
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
        _houseFrame++;
        if (_pendingStage.Count > 0 && _houseFrame >= _stageCheckFrame) CheckStaged();
        if (!_refreshStatus && !_morningDue) return;
        if (++_frame % 30 != 0) return;               // about twice a second
        if (GameFlow.InterstitialCount > 0) return;   // wait for the morning screens to close
        Safe("refresh", () =>
        {
            var (plan, snap) = Compute();
            if (snap.Rooms.Count == 0) return;        // house not on screen yet
            Status(Report.Status(plan));
            _refreshStatus = false;
            if (_morningDue)
            {
                _morningDue = false;
                var text = Report.Morning(plan);
                if (_morning.Value && text.Length > 0) GameMessages.Show($"Pedigree · Day {snap.Day}", text);
            }
        });
    }

    // ---------------------------------------------------------------- buttons

    void ShowPlan() => Safe("show plan", () =>
    {
        var (plan, snap) = Compute();
        if (snap.Rooms.Count == 0) { Status("Open the house screen first."); return; }
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

    void Arrange() => Safe("arrange", () =>
    {
        if (Mew.IsInCombat) { Status("Not during a fight."); return; }
        var (plan, snap) = Compute();
        if (snap.Rooms.Count == 0) { Status("Open the house screen first."); return; }
        if (plan.Moves.Count == 0) { Status("Rooms already match the plan."); return; }

        string backup = BackupSave(snap.Day);
        var failed = new List<string>();
        var done = new List<string>();
        foreach (var m in plan.Moves)
        {
            bool ok = snap.Chars.TryGetValue(m.Cat.Id, out var ch) && ch.IsLiveAndValid && ch.MoveToRoomCenter(m.To);
            if (ok)
            {
                // Remember only the first room of the day, so Undo goes back to the morning layout.
                if (!_dayStart.ContainsKey(m.Cat.Id) && m.From != null && snap.Rooms.Any(r => r.Name == m.From)) _dayStart[m.Cat.Id] = m.From;
                done.Add($"{m.Cat.Name} {m.From ?? "outside"}->{m.To}");
            }
            else failed.Add(m.Cat.Name);
        }
        Status($"Moved {done.Count}." + (failed.Count > 0 ? $" {failed.Count} failed (see log)." : "") + (backup.Length > 0 ? " Save backed up." : ""));
        Log($"[Pedigree] arranged: {(done.Count > 0 ? string.Join(", ", done) : "nothing")}" + (failed.Count > 0 ? $"; FAILED: {string.Join(", ", failed)}" : ""));
    });

    /// <summary>
    /// Moves the suggested squad into the adventure box container. The game may apply a move on a later frame, so each
    /// cat is checked about a second later (see <see cref="CheckStaged"/>): any cat not in the box by then goes back
    /// to its room, so no cat is left misplaced.
    /// </summary>
    void StageSquad() => Safe("stage squad", () =>
    {
        if (Mew.IsInCombat) { Status("Not during a fight."); return; }
        if (_pendingStage.Count > 0) { CheckStaged(); return; }   // a second press finishes the last check now
        var (plan, snap) = Compute();
        if (snap.Rooms.Count == 0) { Status("Open the house screen first."); return; }
        var todo = plan.Squad.Where(x => !x.Cat.InAdventureBox).Select(x => x.Cat).ToList();
        if (todo.Count == 0) { Status(plan.Squad.Count > 0 ? "Squad already in the box." : "Nobody can go on a run."); return; }

        LogContainers("before staging");
        foreach (var cat in todo)
        {
            if (!snap.Chars.TryGetValue(cat.Id, out var ch) || !ch.IsLiveAndValid) continue;
            bool accepted = ch.MoveToRoomCenter(GameSnapshot.BoxContainer);
            string? now = GameBreeding.RoomOf(ch)?.Name;
            Log($"[Pedigree] box: {cat.Name} from {cat.Room ?? "outside"}: move returned {accepted}, room right after = {now ?? "(none)"}");
            _pendingStage[cat.Id] = (cat.Name, cat.Room);
        }
        _stageCheckFrame = _houseFrame + 60;   // about a second at 60 fps
        Status("Moving squad to the box...");
    });

    /// <summary>Second half of staging, about a second later: keeps cats that reached the box, puts the rest back.</summary>
    void CheckStaged() => Safe("check staging", () =>
    {
        var world = GameWorld.Current;
        int placed = 0, failed = 0;
        foreach (var (id, (name, from)) in _pendingStage)
        {
            var ch = world.GetCatById(id);
            if (!ch.HasValue || !ch.Value.IsLiveAndValid) { failed++; continue; }
            string? now = GameBreeding.RoomOf(ch.Value)?.Name;
            if (now == GameSnapshot.BoxContainer) { placed++; Log($"[Pedigree] box: {name} is in the box"); continue; }
            failed++;
            Log($"[Pedigree] box: {name} is in {now ?? "(none)"} a second later, not the box; moving it back to {from ?? "(none)"}");
            if (from != null && now != from) ch.Value.MoveToRoomCenter(from);
        }
        _pendingStage.Clear();
        LogContainers("after staging");
        _refreshStatus = true;
        Status($"Box: {placed} in." + (failed > 0 ? $" {failed} failed, left in rooms (see log)." : ""));
    });

    void LogContainers(string when)
    {
        try { Log($"[Pedigree] containers {when}: " + string.Join(", ", GameBreeding.GetRooms().Select(r => $"{r.Name}={r.CatCount}"))); }
        catch (Exception ex) { Log($"[Pedigree] containers {when} failed: {ex.Message}"); }
    }

    void Undo() => Safe("undo", () =>
    {
        if (_dayStart.Count == 0) { Status("Nothing to undo today."); return; }
        var world = GameWorld.Current;
        var back = new List<string>(); var failed = new List<string>();
        foreach (var (id, room) in _dayStart)
        {
            var ch = world.GetCatById(id);
            if (!ch.HasValue || !ch.Value.IsLiveAndValid) continue;
            if (GameBreeding.RoomOf(ch.Value)?.Name == room) continue;   // already where it started
            if (ch.Value.MoveToRoomCenter(room)) back.Add($"{ch.Value.Name}->{room}"); else failed.Add(ch.Value.Name);
        }
        _dayStart.Clear();
        _refreshStatus = true;
        Status($"Undid {back.Count} move{(back.Count == 1 ? "" : "s")}." + (failed.Count > 0 ? $" {failed.Count} failed (see log)." : ""));
        Log($"[Pedigree] undo: {(back.Count > 0 ? string.Join(", ", back) : "nothing")}" + (failed.Count > 0 ? $"; FAILED: {string.Join(", ", failed)}" : ""));
    });

    // ---------------------------------------------------------------- helpers

    /// <summary>Removes menu entries of older versions and refreshes the button labels in Mods\config\pedigree.cfg.</summary>
    void TidyMenu()
    {
        try
        {
            if (Config == null) return;
            foreach (var key in new[] { "stage_squad", "stage_squad__hint", "stage_squad__action" }) Config.Remove(key);
            foreach (var b in new[] { _show, _arrange, _stage, _undo })
            {
                Config.SetHint(b.Key, b.Label);
                Config.SetAction(b.Key, b.Label);
            }
            foreach (Setting st in new Setting[] { _morning, _hotkey, _maxRisk }) Config.SetHint(st.Key, st.Label);
        }
        catch (Exception ex) { Log($"[Pedigree] menu tidy failed: {ex.Message}"); }
    }

    (Plan plan, GameSnapshot snap) Compute()
    {
        var snap = GameSnapshot.Take(Log);
        var settings = new PlannerSettings { MaxDefectRisk = Math.Clamp(_maxRisk.Value, 2, 40) / 100.0 };
        var plan = new Planner(snap.Cats, snap.Rooms, snap, settings).Build(snap.Day);
        return (plan, snap);
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
