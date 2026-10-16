using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using SignalWatchdogs.Core;
using SignalWatchdogs.Native;

namespace SignalWatchdogs.Monitors
{
    /// <summary>
    /// Suit les applications qui sollicitent l'affichage / le GPU (RDP, Teams, Parsec, Zoom, AnyDesk…)
    /// et détecte les redémarrages de dwm.exe (compositeur) et explorer.exe dans la session.
    /// </summary>
    public sealed class ProcessMonitor : IDisposable
    {
        const string Src = "Applications";
        static readonly Dictionary<string, string> BuiltIn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["mstsc"] = "Bureau à distance (client RDP)",
            ["msrdc"] = "Bureau à distance (MSRDC / AVD)",
            ["msrdcw"] = "Bureau à distance (Windows App)",
            ["windows365"] = "Windows 365",
            ["ms-teams"] = "Microsoft Teams",
            ["teams"] = "Microsoft Teams (classique)",
            ["parsecd"] = "Parsec",
            ["zoom"] = "Zoom",
            ["anydesk"] = "AnyDesk",
            ["teamviewer"] = "TeamViewer",
            ["rustdesk"] = "RustDesk",
            ["obs64"] = "OBS Studio",
            ["discord"] = "Discord",
            ["webexhost"] = "Webex",
            ["ciscocollabhost"] = "Webex",
            ["atmgr"] = "Webex",
            ["g2mcomm"] = "GoTo Meeting",
            ["vmconnect"] = "Hyper-V (connexion VM)",
            ["vmware"] = "VMware Workstation",
            ["vncviewer"] = "VNC (client)",
            ["winvnc"] = "VNC (serveur)",
            ["tvnserver"] = "TightVNC (serveur)",
            ["srserver"] = "Splashtop (serveur)",
            ["strwinclt"] = "Splashtop (client)",
            ["remoting_host"] = "Chrome Remote Desktop",
            ["displaylinkuseragent"] = "DisplayLink",
            ["spacedeskservice"] = "spacedesk",
            ["sunshine"] = "Sunshine (streaming)",
            ["moonlight"] = "Moonlight (streaming)",
        };

        readonly Dictionary<string, string> _watch;
        readonly Thread _thread;
        volatile bool _stop;
        readonly HashSet<string> _running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int _dwmPid = -1, _explorerPid = -1;
        readonly int _session = Process.GetCurrentProcess().SessionId;

        public volatile List<string> ActiveApps = new List<string>();
        public int ProcessCount { get; private set; }

        public ProcessMonitor(Settings settings)
        {
            _watch = new Dictionary<string, string>(BuiltIn, StringComparer.OrdinalIgnoreCase);
            foreach (var extra in settings.ExtraApps())
            {
                var n = extra.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? extra.Substring(0, extra.Length - 4) : extra;
                _watch[n] = n;
            }
            _thread = new Thread(Run) { IsBackground = true, Name = "SignalWatchdogs.Processes", Priority = ThreadPriority.BelowNormal };
        }

        public IEnumerable<string> WatchedNames => _watch.Values.Distinct().OrderBy(s => s);

        public void Start() => _thread.Start();

        void Run()
        {
            bool first = true;
            Hub.SetSource(Src, true, $"{_watch.Count} applications suivies + dwm.exe / explorer.exe");
            while (!_stop)
            {
                try { Poll(first); first = false; }
                catch (Exception ex) { Hub.SetSource(Src, false, ex.Message); }
                Thread.Sleep(2000);
            }
        }

        void Poll(bool first)
        {
            var procs = Process.GetProcesses();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dwmPids = new Dictionary<int, bool>();
            var explorerPids = new HashSet<int>();
            try
            {
                foreach (var p in procs)
                {
                    string n;
                    try { n = p.ProcessName; } catch { continue; }
                    if (_watch.ContainsKey(n)) names.Add(n);
                    int sid;
                    try { sid = p.SessionId; } catch { continue; }
                    if (sid != _session) continue;
                    if (n.Equals("dwm", StringComparison.OrdinalIgnoreCase))
                    {
                        // Le vrai DWM tourne sous un compte système : un processus utilisateur ne peut pas l'ouvrir.
                        bool system;
                        try { _ = p.StartTime; system = false; } catch { system = true; }
                        dwmPids[p.Id] = system;
                    }
                    else if (n.Equals("explorer", StringComparison.OrdinalIgnoreCase)) explorerPids.Add(p.Id);
                }
            }
            finally { foreach (var p in procs) p.Dispose(); }
            ProcessCount = procs.Length;

            var labelsNow = new HashSet<string>(names.Select(n => _watch[n]));
            var labelsBefore = new HashSet<string>(_running.Select(n => _watch[n]));
            if (!first)
            {
                foreach (var l in labelsNow.Where(l => !labelsBefore.Contains(l)))
                    Hub.Publish(new WatchEvent(Severity.Info, EventCategory.Process, Codes.AppStart, $"Application démarrée : {l}").With("app", l), Src);
                foreach (var l in labelsBefore.Where(l => !labelsNow.Contains(l)))
                    Hub.Publish(new WatchEvent(Severity.Info, EventCategory.Process, Codes.AppStop, $"Application fermée : {l}").With("app", l), Src);
            }
            else if (labelsNow.Count > 0)
            {
                Hub.Publish(Severity.Info, EventCategory.Process, Codes.WatchdogInfo, "Applications sensibles déjà ouvertes : " + string.Join(", ", labelsNow.OrderBy(x => x)), source: Src);
            }
            _running.Clear();
            foreach (var n in names) _running.Add(n);
            ActiveApps = labelsNow.OrderBy(x => x).ToList();

            // Un redémarrage n'est signalé que si le processus suivi DISPARAÎT : lancer un faux « dwm.exe »
            // dans la session ne suffit pas à déclencher une alerte.
            if (dwmPids.Count > 0)
            {
                int pick = dwmPids.Where(kv => kv.Value).Select(kv => kv.Key).DefaultIfEmpty(dwmPids.Keys.Min()).Min();
                if (_dwmPid != -1 && !dwmPids.ContainsKey(_dwmPid))
                    Hub.Publish(new WatchEvent(Severity.Critical, EventCategory.Display, Codes.DwmRestart, "Le compositeur d'affichage (dwm.exe) a redémarré",
                        $"PID {_dwmPid} → {pick} : tous les écrans ont clignoté / sont passés au noir.", true), Src);
                if (_dwmPid == -1 || !dwmPids.ContainsKey(_dwmPid)) _dwmPid = pick;
            }
            if (explorerPids.Count > 0)
            {
                if (_explorerPid != -1 && !explorerPids.Contains(_explorerPid))
                    Hub.Publish(new WatchEvent(Severity.Warning, EventCategory.Display, Codes.ExplorerRestart, "L'Explorateur Windows (explorer.exe) a redémarré",
                        $"PID {_explorerPid} → {explorerPids.Min()}", true), Src);
                if (_explorerPid == -1 || !explorerPids.Contains(_explorerPid)) _explorerPid = explorerPids.Min();
            }
        }

        public static string SessionState()
        {
            try
            {
                if (NativeMethods.IsRemoteSession) return "Session distante (RDP)";
                uint console = NativeMethods.WTSGetActiveConsoleSessionId();
                int own = Process.GetCurrentProcess().SessionId;
                if (console != own) return "Session non attachée à la console";
                return "Console locale";
            }
            catch { return "?"; }
        }

        public void Dispose() => _stop = true;
    }

    /// <summary>
    /// Détecteur de gel système : un thread haute priorité vérifie qu'il est réveillé toutes les 100 ms.
    /// Un retard important signifie que tout le système s'est figé (pilote bloqué, TDR, saturation).
    /// </summary>
    public sealed class StallMonitor : IDisposable
    {
        readonly int _thresholdMs;
        readonly Thread _thread;
        volatile bool _stop;
        DateTime _ignoreUntil = DateTime.MinValue;

        public TimeSpan LongestStall { get; private set; }
        public int StallCount { get; private set; }

        public StallMonitor(int thresholdMs)
        {
            _thresholdMs = thresholdMs;
            _thread = new Thread(Run) { IsBackground = true, Name = "SignalWatchdogs.Stall", Priority = ThreadPriority.Highest };
        }

        public void Start()
        {
            _thread.Start();
            Hub.SetSource("Détection de gel système", true, $"Seuil {_thresholdMs} ms");
        }

        // Fenêtre bornée : un faux message de mise en veille ne peut pas désactiver la détection durablement.
        public void OnSuspend() => _ignoreUntil = DateTime.Now.AddMinutes(5);
        public void OnResume() => _ignoreUntil = DateTime.Now.AddSeconds(30);

        void Run()
        {
            var sw = Stopwatch.StartNew();
            while (!_stop)
            {
                sw.Restart();
                Thread.Sleep(100);
                var elapsed = sw.ElapsedMilliseconds;
                if (elapsed < _thresholdMs) continue;
                if (DateTime.Now < _ignoreUntil || elapsed > 120000) continue; // veille / hibernation
                var d = TimeSpan.FromMilliseconds(elapsed - 100);
                StallCount++;
                if (d > LongestStall) LongestStall = d;
                Hub.Publish(new WatchEvent(d.TotalSeconds >= 5 ? Severity.Error : Severity.Warning, EventCategory.System, Codes.Stall,
                    $"Gel du système pendant {Fr.Duration(d)}", "Aucun thread n'a pu s'exécuter : pilote bloqué, TDR en cours ou saturation CPU/disque.", true)
                    .With("duration", Fr.Duration(d)).With("duration_ms", (long)d.TotalMilliseconds), "Détection de gel");
            }
        }

        public void Dispose() => _stop = true;
    }
}
