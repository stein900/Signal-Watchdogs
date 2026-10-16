using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace SignalWatchdogs.Core
{
    /// <summary>
    /// Configuration portable : fichier SignalWatchdogs.ini à côté de l'exécutable
    /// (repli sur %LOCALAPPDATA%\Signal Watchdogs si le dossier n'est pas accessible en écriture).
    /// </summary>
    public sealed class Settings
    {
        public string LogDirectory = "";            // vide = <dossier exe>\SignalWatchdogs_Logs
        public int RetentionDays = 60;
        public int TopologyPollMs = 250;
        public int GpuSampleMs = 2000;
        public int DevicePollSeconds = 10;
        public int StallThresholdMs = 1500;
        public int HistoryDays = 30;
        public string Hotkey = "Ctrl+Alt+Shift+B";
        public bool Notifications = true;
        public bool NotifyOnlyIncidents = true;
        public bool StartMinimized = false;
        public bool UseNvidiaSmi = true;
        public bool LogInfoEvents = true;
        public string ExtraWatchedApps = "";
        public int GpuTempWarning = 85;

        public static string ExeDirectory => AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        public static string ExePath => System.Reflection.Assembly.GetEntryAssembly()?.Location ?? Path.Combine(ExeDirectory, "SignalWatchdogs.exe");

        public string FilePath { get; private set; }

        /// <summary>Vrai si l'exe a été installé par le setup (dans Program Files, non modifiable par les utilisateurs).</summary>
        public static bool IsInstalled
        {
            get
            {
                foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
                    if (!string.IsNullOrEmpty(pf) && ExeDirectory.StartsWith(pf.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
        }

        /// <summary>Dossier des journaux commun à tous les utilisateurs du poste (version installée).</summary>
        public static string MachineLogRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SignalWatchdogs", "Logs");

        public string EffectiveLogRoot
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(LogDirectory)) return Environment.ExpandEnvironmentVariables(LogDirectory.Trim());
                return IsInstalled ? MachineLogRoot : Path.Combine(ExeDirectory, "SignalWatchdogs_Logs");
            }
        }

        static string UserIni => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SignalWatchdogs", "SignalWatchdogs.ini");

        public static Settings Load()
        {
            var s = new Settings();
            var machine = Path.Combine(ExeDirectory, "SignalWatchdogs.ini");
            var files = new System.Collections.Generic.List<string>();
            if (SecurityGuard.IsElevated)
            {
                // En administrateur, on ignore le fichier de %LOCALAPPDATA% : il est modifiable par les
                // processus non élevés de l'utilisateur et pourrait rediriger les écritures du processus admin.
                s.FilePath = machine;
                files.Add(machine);
            }
            else if (IsInstalled)
            {
                // Version installée : réglages machine (posés par le setup / l'IT) puis préférences de l'utilisateur.
                s.FilePath = UserIni;
                files.Add(machine);
                files.Add(UserIni);
            }
            else
            {
                s.FilePath = File.Exists(machine) || CanWrite(ExeDirectory) ? machine : UserIni;
                if (!File.Exists(s.FilePath) && File.Exists(UserIni)) s.FilePath = UserIni;
                files.Add(s.FilePath);
            }
            foreach (var f in files) s.ReadFile(f);
            s.Sanitize();
            return s;
        }

        void ReadFile(string path)
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024) return;
                foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";") || line.StartsWith("[")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    Apply(line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim());
                }
            }
            catch { }
        }

        void Apply(string key, string val)
        {
            var f = GetType().GetField(key);
            if (f == null) return;
            try
            {
                if (f.FieldType == typeof(int) && int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) f.SetValue(this, i);
                else if (f.FieldType == typeof(bool)) f.SetValue(this, val.Equals("true", StringComparison.OrdinalIgnoreCase) || val == "1" || val.Equals("oui", StringComparison.OrdinalIgnoreCase));
                else if (f.FieldType == typeof(string)) f.SetValue(this, val);
            }
            catch { }
        }

        void Sanitize()
        {
            TopologyPollMs = Clamp(TopologyPollMs, 100, 5000);
            GpuSampleMs = Clamp(GpuSampleMs, 500, 60000);
            DevicePollSeconds = Clamp(DevicePollSeconds, 2, 600);
            StallThresholdMs = Clamp(StallThresholdMs, 500, 30000);
            HistoryDays = Clamp(HistoryDays, 1, 365);
            RetentionDays = Clamp(RetentionDays, 1, 3650);
            GpuTempWarning = Clamp(GpuTempWarning, 50, 120);
        }

        static int Clamp(int v, int min, int max) => Math.Max(min, Math.Min(max, v));

        public bool Save(out string error)
        {
            error = null;
            Sanitize();
            var sb = new StringBuilder();
            sb.AppendLine("# Signal Watchdogs - configuration (modifiable à la main)");
            sb.AppendLine("# LogDirectory : vide = dossier par défaut. Un partage réseau (\\\\serveur\\wd-logs$) permet la vue Parc.");
            // Version installée, utilisateur standard : on n'écrit que les préférences qui diffèrent des réglages machine.
            Settings baseline = null;
            if (IsInstalled && !SecurityGuard.IsElevated && FilePath == UserIni)
            {
                baseline = new Settings();
                baseline.ReadFile(Path.Combine(ExeDirectory, "SignalWatchdogs.ini"));
                baseline.Sanitize();
            }
            foreach (var f in GetType().GetFields().Where(f => !f.IsStatic))
            {
                var v = Convert.ToString(f.GetValue(this), CultureInfo.InvariantCulture);
                if (baseline != null && v == Convert.ToString(f.GetValue(baseline), CultureInfo.InvariantCulture)) continue;
                sb.AppendLine(f.Name + "=" + v);
            }
            var targets = SecurityGuard.IsElevated
                ? new[] { FilePath }
                : new[] { FilePath, UserIni };
            foreach (var path in targets)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                    FilePath = path;
                    return true;
                }
                catch (Exception ex) { error = ex.Message; }
            }
            return false;
        }

        public IEnumerable<string> ExtraApps() =>
            (ExtraWatchedApps ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(a => a.Trim()).Where(a => a.Length > 0);

        static bool CanWrite(string dir)
        {
            try
            {
                var probe = Path.Combine(dir, ".wd_write_test");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        // ---- Démarrage automatique (HKCU\...\Run) ----
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        /// <summary>Démarrage automatique configuré par le setup pour tous les utilisateurs (HKLM\...\Run).</summary>
        public static bool MachineAutoStart
        {
            get
            {
                try
                {
                    using var k = Registry.LocalMachine.OpenSubKey(RunKey);
                    return !string.IsNullOrEmpty(k?.GetValue("SignalWatchdogs") as string);
                }
                catch { return false; }
            }
        }

        public static bool AutoStartEnabled
        {
            get
            {
                try
                {
                    using var k = Registry.CurrentUser.OpenSubKey(RunKey);
                    var v = k?.GetValue("SignalWatchdogs") as string;
                    return !string.IsNullOrEmpty(v);
                }
                catch { return false; }
            }
        }

        public static void SetAutoStart(bool enable)
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enable) k.SetValue("SignalWatchdogs", "\"" + ExePath + "\" --minimized");
            else k.DeleteValue("SignalWatchdogs", false);
        }
    }
}
