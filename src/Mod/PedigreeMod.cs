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
    public override string Version => "0.1.1";
    public override string Description =>
        "Breeding and roster advice. Suggests where every cat should sleep (one click to arrange), the next run squad, " +
        "which strays to take in and which cats to donate. Moves cats only when you press a button; never donates anything.";
    public override string Category => "Quality of Life";
    public override bool AutoEnable => true;

    // ---- F10 menu (the SDK registers these fields when the mod loads) ----
    readonly ButtonSetting _show;
    readonly ButtonSetting _arrange;
    readonly ButtonSetting _stage;
    readonly ButtonSetting _undo;
    readonly BoolSetting _morning = new("morning_popup", "Morning summary popup (new kittens, strays, problems)", true);
    readonly ChoiceSetting _hotkey = new("hotkey", "Hotkey for the full plan", "F9",
        new[] { ("none", "None"), ("F6", "F6"), ("F7", "F7"), ("F8", "F8"), ("F9", "F9"), ("F11", "F11") });
    readonly IntSetting _maxRisk = new("max_risk", "Highest birth-defect risk allowed in a pair (%)", 5, 2, 40);

    bool _supported;
    bool _subscribed;
    bool _refreshStatus = true;
    bool _morningDue;
    int _frame;
    List<(long id, string room)> _undoMoves = new();
    long _lastBackupDay = -1;

    public PedigreeMod()
    {
        _show = new ButtonSetting("show_plan", "Show plan", ShowPlan);
        _arrange = new ButtonSetting("arrange", "Auto-arrange rooms", Arrange);
        _stage = new ButtonSetting("stage_squad", "Put run squad in the adventure box", StageSquad);
        _undo = new ButtonSetting("undo", "Undo last arrange", Undo);
    }

    protected override void OnEnable()
    {
        _supported = GameBuild.IsSupported(GameBase);
        if (!_supported)
        {
            Status($"Off: this game build is not {GameBuild.Supported}, the version the mod framework supports. Your game was not touched.");
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
        _refreshStatus = true;
        Status("Ready. Open the house and press Show plan or Auto-arrange rooms.");
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

    void OnDayAdvanced(DayAdvancedEvent e) { _morningDue = true; _refreshStatus = true; _undoMoves.Clear(); }
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
    });

    void Arrange() => Safe("arrange", () =>
    {
        if (Mew.IsInCombat) { Status("Not during a fight."); return; }
        var (plan, snap) = Compute();
        if (snap.Rooms.Count == 0) { Status("Open the house screen first."); return; }
        if (plan.Moves.Count == 0) { Status("Rooms already match the plan. " + Report.Status(plan)); return; }

        string backup = BackupSave(snap.Day);
        var undo = new List<(long, string)>();
        var failed = new List<string>();
        foreach (var m in plan.Moves)
        {
            if (!snap.Chars.TryGetValue(m.Cat.Id, out var ch) || !ch.IsLiveAndValid) { failed.Add(m.Cat.Name); continue; }
            if (ch.MoveToRoomCenter(m.To))
            {
                if (m.From != null && snap.Rooms.Any(r => r.Name == m.From)) undo.Add((m.Cat.Id, m.From));
            }
            else failed.Add(m.Cat.Name);
        }
        _undoMoves = undo;
        int moved = plan.Moves.Count - failed.Count;
        Status($"Moved {moved} cat{(moved == 1 ? "" : "s")}." +
               (failed.Count > 0 ? $" Could not move {string.Join(", ", failed)}." : "") +
               (backup.Length > 0 ? " Save backed up." : ""));
        Log($"[Pedigree] arranged: {string.Join(", ", plan.Moves.Select(x => $"{x.Cat.Name}->{x.To}"))}");
    });

    void StageSquad() => Safe("stage squad", () =>
    {
        var (plan, snap) = Compute();
        if (snap.Rooms.Count == 0) { Status("Open the house screen first."); return; }
        var todo = plan.Squad.Where(s => !s.Cat.InAdventureBox).ToList();
        if (todo.Count == 0) { Status(plan.Squad.Count > 0 ? "The squad is already in the box." : "Nobody can go on a run right now."); return; }
        var placed = new List<string>(); var failed = new List<string>();
        foreach (var s in todo)
            if (snap.Chars.TryGetValue(s.Cat.Id, out var ch) && ch.MoveToAdventureBox()) placed.Add(s.Cat.Name);
            else failed.Add(s.Cat.Name);
        Status((placed.Count > 0 ? $"In the box: {string.Join(", ", placed)}." : "") +
               (failed.Count > 0 ? $" No room in the box for {string.Join(", ", failed)}." : ""));
    });

    void Undo() => Safe("undo", () =>
    {
        if (_undoMoves.Count == 0) { Status("Nothing to undo."); return; }
        var world = GameWorld.Current;
        int back = 0;
        foreach (var (id, room) in _undoMoves)
        {
            var ch = world.GetCatById(id);
            if (ch.HasValue && ch.Value.IsLiveAndValid && ch.Value.MoveToRoomCenter(room)) back++;
        }
        _undoMoves = new();
        _refreshStatus = true;
        Status($"Put {back} cat{(back == 1 ? "" : "s")} back.");
    });

    // ---------------------------------------------------------------- helpers

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
            Status($"Something went wrong during {what}; nothing else was changed. Details in mods_debug.log.");
            Log($"[Pedigree] {what} failed: {ex}");
        }
    }
}
