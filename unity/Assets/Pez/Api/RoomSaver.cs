using System;
using System.Collections.Generic;
using System.IO;
using Pez.Sim;

namespace Pez.Api
{
    /// <summary>
    /// Saves a host's running game to disk (periodically, soon after anyone joins, on request and at shutdown) and
    /// resumes it when the host starts again, so swapping in a new build or restarting the app doesn't end anyone's game.
    /// One file per host port: ~/.config/pezz/rooms/game-&lt;port&gt;.json, written atomically (temp file + rename), mode 0600
    /// (it holds the players' tokens).
    /// </summary>
    public class RoomSaver
    {
        public readonly string File;
        /// <summary>Game seconds between periodic saves.</summary>
        public float IntervalSeconds = 30f;
        public Action<string> Log = _ => { };
        World savedWorld;
        float lastSaveTime;
        int lastTokens = -1;
        DateTime lastSaveReal = DateTime.MinValue;

        public RoomSaver(string file) { File = file; }

        public static string DefaultFile(int port)
        {
            var home = Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrEmpty(home)) home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".config", "pezz", "rooms", $"game-{port}.json");
        }

        /// <summary>
        /// At startup: restore the saved game into `game` (and its tokens into `api`) if there is one and resuming is
        /// wanted. explicitResume (-resume / --resume) resumes any saved game; an open-arena launch resumes a saved open
        /// arena by default; fresh (-fresh / --fresh) discards the snapshot. Returns true if the game was resumed.
        /// </summary>
        public bool TryResume(Game game, ApiServer api, bool explicitResume, bool openLaunch, bool fresh)
        {
            if (fresh) { Delete(); Log("Starting a fresh game (saved game discarded)."); return false; }
            if (!System.IO.File.Exists(File)) { if (explicitResume) Log($"No saved game at {File}; starting a new one."); return false; }
            if (!explicitResume && !openLaunch) return false;
            Dictionary<string, object> root;
            try { root = Snapshot.Parse(System.IO.File.ReadAllText(File)); }
            catch (Exception ex) { SetAside(ex); return false; }
            var cfg = root.Obj("config");
            bool savedOpen = cfg != null && cfg.TryGetValue("open", out var o) && o is bool b && b;
            if (!explicitResume && !savedOpen) { Log("The saved game isn't an open arena; start with -resume to resume it. Starting a new one."); return false; }
            try
            {
                game.Restore(root);
                int tokens = api?.RestoreTokens(game.World, root.Obj("api")?.Arr("tokens")) ?? 0;
                var w = game.World;
                var savedAt = DateTime.TryParse(game.ResumedFrom, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t.ToUniversalTime() : DateTime.UtcNow;
                int away = (int)Math.Max(0, (DateTime.UtcNow - savedAt).TotalSeconds);
                w.Emit("chat", -1, text: $"Back online: your game has been resumed (game time {(int)w.Time / 60}:{(int)w.Time % 60:00}, saved {away}s ago). " +
                                         "Same seats, bases, units and tokens; carry on. Anything new in this build is announced here as 🆕.");
                savedWorld = w; lastSaveTime = w.Time; lastTokens = api?.TokensVersion ?? -1;
                Log($"Resumed the saved game from {File}: tick {w.Tick}, {w.Teams.Count} seats, {w.ActivePlayers} playing, {tokens} tokens (saved {game.ResumedFrom}, schema {root.In("schema")}).");
                return true;
            }
            catch (Exception ex) { SetAside(ex); return false; }
        }

        /// <summary>Call every frame / loop: saves every IntervalSeconds of game time, and a moment after a join.</summary>
        public void Tick(Game game, ApiServer api)
        {
            var w = game.World;
            if (w != savedWorld) { savedWorld = w; lastSaveTime = w.Time; } // a new game: count from its start
            bool due = w.Time - lastSaveTime >= IntervalSeconds;
            bool joined = api != null && api.TokensVersion != lastTokens && (DateTime.UtcNow - lastSaveReal).TotalSeconds >= 2;
            if (due || joined) Save(game, api, null);
        }

        /// <summary>Write the snapshot now. reason (if any) is logged. Returns the result as JSON for /api/admin/save.</summary>
        public JObj Save(Game game, ApiServer api, string reason)
        {
            var w = game.World;
            savedWorld = w; lastSaveTime = w.Time; lastSaveReal = DateTime.UtcNow; lastTokens = api?.TokensVersion ?? -1;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var root = Snapshot.Write(game);
                if (api != null) root.Set("api", new JObj().Set("tokens", api.SaveTokens(w)));
                var json = Json.Write(root);
                WriteAtomically(json);
                game.LastSaved = root["saved_at"] as string;
                if (reason != null) Log($"Game saved ({reason}): {File}, {json.Length / 1024} KB, tick {w.Tick}, {sw.ElapsedMilliseconds} ms.");
                return new JObj().Set("ok", true).Set("file", File).Set("tick", w.Tick).Set("bytes", json.Length).Set("saved_at", game.LastSaved).Set("ms", sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                Log($"Couldn't save the game to {File}: {ex.GetType().Name}: {ex.Message}");
                return new JObj().Set("ok", false).Set("error", ex.Message);
            }
        }

        /// <summary>A fresh game replaced the saved one (admin restart, new game from the menu): forget it.</summary>
        public void Forget(Game game)
        {
            Delete();
            game.ResumedFrom = null;
            game.LastSaved = null;
            savedWorld = game.World; lastSaveTime = game.World.Time;
        }

        public void Delete()
        {
            try { if (System.IO.File.Exists(File)) System.IO.File.Delete(File); } catch (Exception ex) { Log("Couldn't delete the saved game: " + ex.Message); }
        }

        /// <summary>A snapshot that can't be loaded is kept for diagnosis but moved out of the way.</summary>
        void SetAside(Exception ex)
        {
            var bad = File + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            try { System.IO.File.Move(File, bad); } catch { }
            Log($"Couldn't resume the saved game ({ex.GetType().Name}: {ex.Message}); kept it as {bad} and starting a new game.");
        }

        void WriteAtomically(string json)
        {
            var dir = Path.GetDirectoryName(File);
            Directory.CreateDirectory(dir);
            Chmod(dir, 0x1C0); // 0700
            var tmp = $"{File}.tmp-{System.Diagnostics.Process.GetCurrentProcess().Id}";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                Chmod(tmp, 0x180); // 0600, before any secret is written
                var bytes = new System.Text.UTF8Encoding(false).GetBytes(json);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            try
            {
                if (System.IO.File.Exists(File)) System.IO.File.Replace(tmp, File, null); // rename(2): readers see old or new, never half
                else System.IO.File.Move(tmp, File);
            }
            catch (Exception) when (System.IO.File.Exists(tmp))
            {
                System.IO.File.Copy(tmp, File, true); // a runtime without Replace: not atomic, but never leaves no file
                System.IO.File.Delete(tmp);
            }
        }

        static void Chmod(string path, int mode)
        {
#if NET7_0_OR_GREATER
            if (!OperatingSystem.IsWindows()) System.IO.File.SetUnixFileMode(path, (UnixFileMode)mode);
#else
            try { chmod(path, mode); } catch { } // Unity (Mono): libc directly; best effort
#endif
        }

#if !NET7_0_OR_GREATER
        [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
        static extern int chmod(string path, int mode);
#endif
    }
}
