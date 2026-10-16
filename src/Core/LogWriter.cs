using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace SignalWatchdogs.Core
{
    /// <summary>
    /// Écriture asynchrone des journaux :
    ///  - events-AAAA-MM-JJ.jsonl     (1 événement JSON par ligne, exploitable par script)
    ///  - incidents-AAAA-MM-JJ.jsonl  (incidents corrélés + diagnostic)
    ///  - signalwatchdogs-AAAA-MM-JJ.log    (lisible par un humain)
    ///  - metrics-AAAA-MM-JJ.csv      (charge GPU/CPU/RAM, températures)
    ///  - status.json                 (résumé du poste, lu par la vue Parc)
    /// </summary>
    public sealed class LogWriter : IDisposable
    {
        enum Kind { Event, Incident, Metrics, Status }
        static readonly System.Text.RegularExpressions.Regex OwnLogFile =
            new System.Text.RegularExpressions.Regex(@"^(events|incidents|signalwatchdogs|metrics)-\d{4}-\d{2}-\d{2}\.(jsonl|log|csv)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        const long MaxReadBytes = 64L * 1024 * 1024;
        readonly BlockingCollection<(Kind kind, DateTime time, object payload)> _queue = new BlockingCollection<(Kind, DateTime, object)>(20000);
        readonly Thread _thread;
        readonly int _retentionDays;
        readonly bool _logInfo;
        DateTime _lastCleanup = DateTime.MinValue;
        string _metricsHeader;

        public string MachineDirectory { get; private set; }
        public string RootDirectory { get; private set; }
        public bool UsingFallback { get; private set; }
        public bool Disabled { get; private set; }
        public string LastError { get; private set; }
        const long MaxFileBytes = 256L * 1024 * 1024; // plafond par fichier journalier (anti-saturation du disque / du partage)

        public LogWriter(Settings settings)
        {
            _retentionDays = settings.RetentionDays;
            _logInfo = settings.LogInfoEvents;
            RootDirectory = settings.EffectiveLogRoot;
            if (SecurityGuard.IsElevated && !RootDirectory.StartsWith(@"\\") && SecurityGuard.WhyDirectoryNotProtected(RootDirectory) is string why)
            {
                // Un processus administrateur n'écrit jamais dans un dossier local modifiable par les utilisateurs
                // (ex. C:\ProgramData\Signal Watchdogs\Logs) : un lien ou une jonction y redirigerait ses écritures.
                UsingFallback = true;
                LastError = "journaux administrateur séparés : " + why;
                RootDirectory = Path.Combine(Settings.ExeDirectory, "SignalWatchdogs_Logs");
            }
            MachineDirectory = Path.Combine(RootDirectory, Environment.MachineName);
            if (!TryPrepare(MachineDirectory))
            {
                UsingFallback = true;
                // En administrateur, pas de repli dans le profil utilisateur (modifiable par ses processus non élevés).
                RootDirectory = SecurityGuard.IsElevated
                    ? Path.Combine(Settings.ExeDirectory, "SignalWatchdogs_Logs")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SignalWatchdogs", "Logs");
                MachineDirectory = Path.Combine(RootDirectory, Environment.MachineName);
                Disabled = !TryPrepare(MachineDirectory);
            }
            _thread = new Thread(Run) { IsBackground = true, Name = "SignalWatchdogs.LogWriter", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        bool TryPrepare(string dir)
        {
            try
            {
                if (SecurityGuard.IsElevated && SecurityGuard.HasReparsePoint(dir))
                {
                    // Une jonction pourrait rediriger les écritures du processus administrateur vers un dossier système.
                    LastError = "dossier refusé en administrateur : il passe par une jonction ou un lien symbolique";
                    return false;
                }
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, ".probe");
                File.WriteAllText(probe, DateTime.Now.ToString("o"));
                File.Delete(probe);
                return true;
            }
            catch (Exception ex) { LastError = ex.Message; return false; }
        }

        public void WriteEvent(WatchEvent e)
        {
            if (e.Historical) return;
            if (!_logInfo && e.Severity == Severity.Info) return;
            _queue.TryAdd((Kind.Event, e.Time, e));
        }

        public void WriteIncident(Incident inc) => _queue.TryAdd((Kind.Incident, inc.Start, inc.ToDict()));

        public void WriteMetrics(string header, string line)
        {
            _metricsHeader = header;
            _queue.TryAdd((Kind.Metrics, DateTime.Now, line));
        }

        public void WriteStatus(Dictionary<string, object> status) => _queue.TryAdd((Kind.Status, DateTime.Now, status));

        void Run()
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var buffers = new Dictionary<string, StringBuilder>();
            foreach (var first in _queue.GetConsumingEnumerable())
            {
                buffers.Clear();
                var item = first;
                int n = 0;
                do
                {
                    try { Append(json, buffers, item); } catch (Exception ex) { LastError = ex.Message; }
                    n++;
                } while (n < 500 && _queue.TryTake(out item));

                if (Disabled) continue;
                foreach (var kv in buffers)
                {
                    try
                    {
                        bool isNew = !File.Exists(kv.Key);
                        if (!isNew && new FileInfo(kv.Key).Length > MaxFileBytes) { LastError = "fichier journal plafonné : " + Path.GetFileName(kv.Key); continue; }
                        using var fs = new FileStream(kv.Key, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                        using var w = new StreamWriter(fs, new UTF8Encoding(false));
                        if (isNew && kv.Key.EndsWith(".csv") && _metricsHeader != null) w.WriteLine(_metricsHeader);
                        w.Write(kv.Value.ToString());
                    }
                    catch (Exception ex) { LastError = ex.Message; }
                }

                if ((DateTime.Now - _lastCleanup).TotalHours > 6) Cleanup();
            }
        }

        void Append(JavaScriptSerializer json, Dictionary<string, StringBuilder> buffers, (Kind kind, DateTime time, object payload) item)
        {
            string day = item.time.ToString("yyyy-MM-dd");
            switch (item.kind)
            {
                case Kind.Event:
                    var e = (WatchEvent)item.payload;
                    Buf(buffers, $"events-{day}.jsonl").Append(json.Serialize(e.ToDict())).Append('\n');
                    var sb = Buf(buffers, $"signalwatchdogs-{day}.log");
                    sb.Append(e.Time.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(" [").Append(Fr.Sev(e.Severity).ToUpperInvariant().PadRight(8))
                      .Append("] [").Append(Fr.Cat(e.Category)).Append("] ").Append(SecurityGuard.Clean(e.Title));
                    // Titres et détails peuvent contenir du texte tiers (noms USB, serveur RDP, journaux) : pas de faux sauts de ligne.
                    if (!string.IsNullOrEmpty(e.Details)) sb.Append(" — ").Append(SecurityGuard.Clean(e.Details.Replace("\r\n", " | ").Replace("\n", " | ")));
                    if (e.Trigger) sb.Append("  ⚑");
                    sb.Append("\r\n");
                    break;
                case Kind.Incident:
                    Buf(buffers, $"incidents-{day}.jsonl").Append(json.Serialize(item.payload)).Append('\n');
                    break;
                case Kind.Metrics:
                    Buf(buffers, $"metrics-{day}.csv").Append((string)item.payload).Append("\r\n");
                    break;
                case Kind.Status:
                    if (Disabled) break;
                    try
                    {
                        var path = Path.Combine(MachineDirectory, "status.json");
                        var tmp = path + ".tmp";
                        File.WriteAllText(tmp, json.Serialize(item.payload), new UTF8Encoding(false));
                        if (File.Exists(path)) File.Replace(tmp, path, null, true);
                        else File.Move(tmp, path);
                    }
                    catch (Exception ex) { LastError = ex.Message; }
                    break;
            }
        }

        StringBuilder Buf(Dictionary<string, StringBuilder> buffers, string name)
        {
            var path = Path.Combine(MachineDirectory, name);
            if (!buffers.TryGetValue(path, out var sb)) buffers[path] = sb = new StringBuilder();
            return sb;
        }

        void Cleanup()
        {
            _lastCleanup = DateTime.Now;
            try
            {
                var limit = DateTime.Now.AddDays(-_retentionDays);
                foreach (var f in Directory.GetFiles(MachineDirectory))
                {
                    // Uniquement les fichiers créés par Signal Watchdogs (nom exact) : le dossier est configurable,
                    // il ne faut jamais supprimer d'autres fichiers qui s'y trouveraient.
                    if (OwnLogFile.IsMatch(Path.GetFileName(f)) && File.GetLastWriteTime(f) < limit)
                        File.Delete(f);
                }
            }
            catch { }
        }

        /// <summary>Recharge les incidents des derniers jours (persistance entre redémarrages).</summary>
        public List<Incident> LoadRecentIncidents(int days)
        {
            var res = new List<Incident>();
            var json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
            for (int d = days; d >= 0; d--)
            {
                var path = Path.Combine(MachineDirectory, $"incidents-{DateTime.Today.AddDays(-d):yyyy-MM-dd}.jsonl");
                if (!File.Exists(path) || new FileInfo(path).Length > MaxReadBytes) continue;
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var r = new StreamReader(fs, Encoding.UTF8);
                    string line;
                    while ((line = r.ReadLine()) != null)
                    {
                        if (line.Length < 2) continue;
                        try
                        {
                            if (json.DeserializeObject(line) is Dictionary<string, object> dict) res.Add(Incident.FromDict(dict));
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return res;
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(3000);
        }
    }
}
