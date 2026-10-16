using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using SignalWatchdogs.Core;
using SignalWatchdogs.Diagnostics;
using SignalWatchdogs.Monitors;

namespace SignalWatchdogs
{
    public sealed class HistoryStore
    {
        public volatile List<WatchEvent> Events = new List<WatchEvent>();
        public volatile List<Incident> Incidents = new List<Incident>();
        public DateTime? ScannedAt;
        public volatile bool Running;
        public volatile string Progress = "";
        public int Days;
        public event Action Updated;
        internal void RaiseUpdated() { try { Updated?.Invoke(); } catch { } }
    }

    /// <summary>Orchestrateur : démarre toutes les sondes et expose leur état à l'interface et au rapport.</summary>
    public sealed class WatchdogService : IDisposable
    {
        public static readonly string Version = typeof(WatchdogService).Assembly.GetName().Version.ToString(3);

        public readonly Settings Settings;
        public LogWriter Log;
        public IncidentEngine Incidents;
        public DisplayTopologyMonitor Topology;
        public GpuMonitor Gpu;
        public DeviceMonitor Devices;
        public ProcessMonitor Processes;
        public StallMonitor Stall;
        public EventLogMonitor EventLogs;
        public SystemMessageWindow Messages;
        public readonly HistoryStore History = new HistoryStore();
        public volatile List<CheckResult> Checks = new List<CheckResult>();
        public readonly bool IsAdmin;
        /// <summary>Non nul si le processus est administrateur alors que son exe est modifiable par des utilisateurs.</summary>
        public string UnprotectedLocation;
        public readonly DateTime StartedAt = DateTime.Now;
        public event Action ChecksUpdated;
        Timer _statusTimer;

        public WatchdogService(Settings settings)
        {
            Settings = settings;
            try { IsAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); } catch { }
        }

        public void Start()
        {
            Hub.Settings = Settings;
            Log = new LogWriter(Settings);
            Hub.Log = Log;
            Incidents = new IncidentEngine { ContextProvider = BuildContext };
            Hub.Incidents = Incidents;
            try { Incidents.Restore(Log.LoadRecentIncidents(7)); } catch { }

            Hub.Publish(Severity.Info, EventCategory.Watchdog, Codes.WatchdogStart, $"Signal Watchdogs {Version} démarré",
                $"Poste {Environment.MachineName}, utilisateur {Environment.UserName}, {(IsAdmin ? "administrateur" : "droits standard")}, journaux : {Log.MachineDirectory}" +
                (Log.UsingFallback ? " (dossier de repli : le dossier configuré n'est pas accessible en écriture)" : ""), source: "Signal Watchdogs");
            Hub.SetSource("Journalisation disque", !Log.UsingFallback && !Log.Disabled,
                Log.Disabled ? "désactivée : " + Log.LastError : Log.MachineDirectory + (Log.UsingFallback ? " (repli : " + Log.LastError + ")" : ""));
            if (IsAdmin && (UnprotectedLocation = SecurityGuard.WhyExecutableNotProtected(Settings.ExePath)) != null)
                Hub.Publish(Severity.Warning, EventCategory.Watchdog, Codes.WatchdogInfo, "Exécution en administrateur depuis un emplacement non protégé",
                    UnprotectedLocation + ". Un utilisateur pourrait remplacer l'exe ou y déposer un fichier piégé : placer SignalWatchdogs.exe dans C:\\Program Files\\Signal Watchdogs.", source: "Signal Watchdogs");

            Topology = new DisplayTopologyMonitor(Settings.TopologyPollMs);
            Gpu = new GpuMonitor(Settings);
            Devices = new DeviceMonitor(Settings.DevicePollSeconds);
            Processes = new ProcessMonitor(Settings);
            Stall = new StallMonitor(Settings.StallThresholdMs);
            EventLogs = new EventLogMonitor { NameResolver = id => Devices?.ResolveName(id) };

            Topology.Start();
            Gpu.Start();
            Devices.Start();
            Processes.Start();
            Stall.Start();
            Task.Run(() => { try { EventLogs.Start(); } catch (Exception ex) { Hub.SetSource("Journaux Windows", false, ex.Message); } });

            _statusTimer = new Timer(_ => WriteStatus(), null, 20000, 60000);
            RunHistoryScanAsync();
            RunDiagnosticsAsync(6000);
        }

        /// <summary>À appeler depuis le thread de l'interface (boucle de messages).</summary>
        public void AttachMessageWindow()
        {
            Messages = new SystemMessageWindow();
            Messages.DisplayChanged += () => { Topology?.Poke(); Gpu?.RequestInventory(); };
            Messages.DeviceInterfaceChanged += (g, inst, arrival) => Devices?.OnInterfaceNotification(g, inst, arrival);
            Messages.DevNodesChanged += () => Devices?.RequestRescan();
            Messages.Suspending += () => Stall?.OnSuspend();
            Messages.Resumed += () => Stall?.OnResume();
            Messages.RegisterHotkey(Settings.Hotkey);
            var msgs = Messages;
            if (Topology != null) Topology.QuietPeriod = () => msgs.InDisplayQuietPeriod;
        }

        public WatchEvent ReportUserIncident(string note = null, string scope = null)
        {
            var mon = Topology?.Current ?? new List<MonitorInfo>();
            var e = new WatchEvent(Severity.Error, EventCategory.User, Codes.UserReport, "Signalement utilisateur : écran noir / saut d'affichage",
                    (string.IsNullOrWhiteSpace(note) ? "Signalé via le raccourci clavier ou le bouton." : note) + " — Écrans au moment du signalement : " + DisplayTopologyMonitor.Describe(mon), true)
                .With("monitors_active", mon.Count).With("scope", scope);
            Hub.Publish(e, "Utilisateur");
            return e;
        }

        public ContextSnapshot BuildContext()
        {
            var mon = Topology?.Current ?? new List<MonitorInfo>();
            return new ContextSnapshot
            {
                Apps = new List<string>(Processes?.ActiveApps ?? new List<string>()),
                Session = ProcessMonitor.SessionState(),
                Gpu = Gpu?.ContextSummary() ?? "",
                Display = $"{mon.Count} écran(s) actif(s)",
                EncoderActive = Gpu?.EncoderActive ?? false,
            };
        }

        public void RunHistoryScanAsync()
        {
            if (History.Running) return;
            History.Running = true;
            History.Progress = "Démarrage de l'analyse…";
            History.RaiseUpdated();
            Task.Run(() =>
            {
                try
                {
                    var days = Settings.HistoryDays;
                    var evs = EventLogs.ScanHistory(days, p => History.Progress = p);
                    var incs = IncidentEngine.BuildFromHistory(evs);
                    History.Events = evs;
                    History.Incidents = incs;
                    History.Days = days;
                    History.ScannedAt = DateTime.Now;
                    Hub.SetSource("Historique des journaux", true, $"{evs.Count} événements, {incs.Count} incident(s) reconstitué(s) sur {days} jours");
                    Hub.Publish(Severity.Info, EventCategory.Watchdog, Codes.WatchdogInfo, $"Historique Windows analysé : {incs.Count} incident(s) sur {days} jours",
                        $"{evs.Count(e => Codes.GpuCrashCodes.Contains(e.Code))} plantage(s) pilote GPU, {evs.Count(e => e.Category == EventCategory.Usb || e.Category == EventCategory.UsbC)} événement(s) USB/USB-C", source: "Historique");
                }
                catch (Exception ex) { Hub.SetSource("Historique des journaux", false, ex.Message); }
                finally
                {
                    History.Running = false;
                    History.Progress = "";
                    History.RaiseUpdated();
                    RunDiagnosticsAsync(0);
                }
            });
        }

        public void RunDiagnosticsAsync(int delayMs)
        {
            Task.Run(async () =>
            {
                if (delayMs > 0) await Task.Delay(delayMs);
                try
                {
                    Checks = SystemDiagnostics.Run(this);
                    try { ChecksUpdated?.Invoke(); } catch { }
                }
                catch (Exception ex) { Hub.SetSource("Audit de configuration", false, ex.Message); }
            });
        }

        // ------------------------------------------------------------------
        // Statistiques (tableau de bord, status.json, rapport)
        // ------------------------------------------------------------------
        public List<Incident> AllIncidents() => Incidents?.Snapshot() ?? new List<Incident>();

        public int IncidentsSince(DateTime since) => AllIncidents().Count(i => i.Start >= since);

        public int LiveCount(Func<WatchEvent, bool> pred) => Hub.Count(pred);

        /// <summary>Combine l'historique Windows et les événements en direct (sans doublon).</summary>
        public int CombinedCount(Func<WatchEvent, bool> pred, DateTime since)
        {
            var cutoff = History.ScannedAt ?? StartedAt;
            int hist = History.Events.Count(e => e.Time >= since && pred(e));
            int live = Hub.Count(e => e.Time >= since && e.Time > cutoff && e.Source != null && e.Source.StartsWith("Journal") && pred(e));
            int liveOther = Hub.Count(e => e.Time >= since && (e.Source == null || !e.Source.StartsWith("Journal")) && pred(e));
            return hist + live + liveOther;
        }

        public Dictionary<string, object> BuildStatus()
        {
            var incs = AllIncidents();
            var last = incs.OrderByDescending(i => i.Start).FirstOrDefault();
            var mon = Topology?.Current ?? new List<MonitorInfo>();
            var week = incs.Where(i => i.Start >= DateTime.Now.AddDays(-7)).ToList();
            var topCause = week.GroupBy(i => i.Diagnosis.Title).OrderByDescending(g => g.Count()).FirstOrDefault();
            var since30 = DateTime.Now.AddDays(-30);
            return new Dictionary<string, object>
            {
                ["machine"] = Environment.MachineName,
                ["user"] = Environment.UserName,
                ["updated"] = DateTime.Now.ToString("o"),
                ["version"] = Version,
                ["os"] = SystemDiagnostics.OsVersion(),
                ["admin"] = IsAdmin,
                ["session"] = ProcessMonitor.SessionState(),
                ["monitorsCount"] = mon.Count,
                ["monitors"] = mon.Select(m => $"{m.Name} · {m.Connector} · {m.ModeText} · {m.AdapterName}").ToList(),
                ["gpus"] = (Gpu?.Adapters ?? new List<GpuAdapterState>()).Select(a => $"{a.Name} (pilote {a.DriverVersion}{(a.DriverDate.HasValue ? " du " + a.DriverDate.Value.ToString("dd/MM/yyyy") : "")})").ToList(),
                ["incidents24h"] = incs.Count(i => i.Start >= DateTime.Now.AddHours(-24)),
                ["incidents7d"] = week.Count,
                ["openIncidents"] = incs.Count(i => !i.Closed),
                ["lastIncidentTime"] = last?.Start.ToString("o"),
                ["lastIncident"] = last?.Diagnosis.Title,
                ["topCause7d"] = topCause?.Key,
                ["gpuCrashes30d"] = CombinedCount(e => Codes.GpuCrashCodes.Contains(e.Code), since30),
                ["usbErrors30d"] = CombinedCount(e => e.Code == Codes.UcsiError || e.Code == Codes.XhciError || e.Code == Codes.UsbError || e.Code == Codes.LiveKernelUsb, since30),
                ["displayLost24h"] = Hub.Count(e => e.Code == Codes.DisplayLost && e.Time >= DateTime.Now.AddHours(-24)),
                ["userReports7d"] = Hub.Count(e => e.Code == Codes.UserReport) + incs.Count(i => i.Restored && i.Start >= DateTime.Now.AddDays(-7) && i.EventList().Any(e => e.Code == Codes.UserReport)),
                ["warnings"] = Checks.Count(c => c.Status == CheckStatus.Warning || c.Status == CheckStatus.Problem),
                ["checks"] = Checks.Where(c => c.Status == CheckStatus.Warning || c.Status == CheckStatus.Problem).Select(c => c.Title).ToList(),
                ["uptimeSince"] = StartedAt.ToString("o"),
            };
        }

        void WriteStatus()
        {
            try { Log?.WriteStatus(BuildStatus()); } catch { }
        }

        public static List<Dictionary<string, object>> ReadFleet(string root)
        {
            var res = new List<Dictionary<string, object>>();
            try
            {
                var json = new JavaScriptSerializer();
                foreach (var dir in Directory.EnumerateDirectories(root).Take(5000))
                {
                    var f = Path.Combine(dir, "status.json");
                    // Les fichiers des autres postes sont des données non fiables : taille bornée,
                    // désérialisation sans résolution de type, et le dossier réel est mémorisé à part.
                    if (!File.Exists(f) || new FileInfo(f).Length > 1024 * 1024) continue;
                    try
                    {
                        using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var r = new StreamReader(fs);
                        if (json.DeserializeObject(r.ReadToEnd()) is Dictionary<string, object> d)
                        {
                            d["_folder"] = dir;
                            res.Add(d);
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return res;
        }

        public void Dispose()
        {
            try { Hub.Publish(Severity.Info, EventCategory.Watchdog, Codes.WatchdogStop, "Signal Watchdogs arrêté", source: "Signal Watchdogs"); } catch { }
            WriteStatus();
            _statusTimer?.Dispose();
            Messages?.Dispose();
            EventLogs?.Dispose();
            Topology?.Dispose();
            Gpu?.Dispose();
            Devices?.Dispose();
            Processes?.Dispose();
            Stall?.Dispose();
            Incidents?.Dispose();
            Log?.Dispose();
        }
    }
}
