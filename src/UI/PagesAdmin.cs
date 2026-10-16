using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using SignalWatchdogs.Core;
using SignalWatchdogs.Diagnostics;
using SignalWatchdogs.Monitors;
using SignalWatchdogs.Native;

namespace SignalWatchdogs.UI
{
    // ======================================================================
    public sealed class DevicesPage : PageBase
    {
        public override string Title => "USB & périphériques";
        public override string Subtitle => "Docks, hubs, contrôleurs USB-C (UCSI), écrans et périphériques en erreur";

        readonly DataGridView _problems, _tracked, _activity;
        readonly TextBox _filter;
        readonly Card _cTracked, _cProblems;
        string _probSig, _trackSig;
        long _lastSeq;

        public DevicesPage(WatchdogService svc, MainForm main) : base(svc, main)
        {
            var root = Table(2, 2);
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 60));

            _cProblems = new Card("Périphériques en erreur");
            _problems = Theme.Grid();
            _problems.Columns.Add(Theme.Col("Périphérique", 40, 100));
            _problems.Columns.Add(Theme.Col("Classe", 14, 60));
            _problems.Columns.Add(Theme.Col("Problème", 40, 100));
            _problems.Columns.Add(Theme.Col("ID d'instance", 40, 100));
            _cProblems.Controls.Add(_problems);

            var cAct = new Card("Activité USB / USB-C / périphériques");
            _activity = Theme.Grid();
            _activity.Columns.Add(Theme.Col("Heure", 20, 80));
            _activity.Columns.Add(Theme.Col("Gravité", 14, 70));
            _activity.Columns.Add(Theme.Col("Événement", 66, 120));
            PaintSeverityCell(_activity, 1);
            _activity.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0 && _activity.Rows[e.RowIndex].Tag is WatchEvent ev) MainForm.ShowEventDetails(this, ev); };
            cAct.Controls.Add(_activity);

            _cTracked = new Card("Périphériques suivis (USB, USB-C, Thunderbolt, écrans, cartes graphiques)");
            var inner = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface };
            _filter = new TextBox { Dock = DockStyle.Top };
            Theme.StyleInput(_filter);
            _filter.TextChanged += (s, e) => { _trackSig = null; RefreshData(); };
            _tracked = Theme.Grid();
            foreach (var (h, w) in new[] { ("Nom", 34f), ("Classe", 12f), ("État", 14f), ("Fabricant", 16f), ("Emplacement", 16f), ("ID d'instance", 34f), ("Vu depuis", 12f) })
                _tracked.Columns.Add(Theme.Col(h, w, 60));
            inner.Controls.Add(_tracked);
            inner.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6, BackColor = Theme.Surface });
            inner.Controls.Add(_filter);
            _cTracked.Controls.Add(inner);

            root.Controls.Add(_cProblems, 0, 0);
            root.Controls.Add(cAct, 1, 0);
            root.Controls.Add(_cTracked, 0, 1);
            root.SetColumnSpan(_cTracked, 2);
            Controls.Add(root);
        }

        public override void RefreshData()
        {
            var probs = Svc.Devices?.Problems ?? new List<PnpDevice>();
            if (Changed(ref _probSig, string.Join("|", probs.Select(p => p.InstanceId + p.Problem))))
            {
                _problems.Rows.Clear();
                foreach (var p in probs)
                {
                    int r = _problems.Rows.Add(p.DisplayName, p.Class, CfgMgr.ProblemText(p.Problem), p.InstanceId);
                    if (p.Problem == 22) _problems.Rows[r].DefaultCellStyle.ForeColor = Theme.Muted;
                    else _problems.Rows[r].Cells[2].Style.ForeColor = Theme.Warn;
                }
                _cProblems.Subtitle = probs.Count == 0 ? "aucun" : $"{probs.Count(p => p.Problem != 22)} en erreur, {probs.Count(p => p.Problem == 22)} désactivé(s)";
            }
            var tracked = Svc.Devices?.Tracked ?? new List<PnpDevice>();
            var q = _filter.Text.Trim();
            var shown = q.Length == 0 ? tracked : tracked.Where(d => (d.DisplayName + d.InstanceId + d.Class + d.Manufacturer).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (Changed(ref _trackSig, string.Join("|", shown.Select(d => d.InstanceId + d.Status + d.Problem))))
            {
                _tracked.Rows.Clear();
                foreach (var d in shown)
                {
                    int r = _tracked.Rows.Add(d.DisplayName, d.Class, d.StateText, d.Manufacturer, d.Location, d.InstanceId, d.FirstSeen.ToString("dd/MM HH:mm"));
                    if (d.HasProblem) _tracked.Rows[r].Cells[2].Style.ForeColor = Theme.Warn;
                    if (DeviceMonitor.IsHub(d)) _tracked.Rows[r].Cells[0].Style.ForeColor = Theme.Teal;
                }
                _cTracked.Subtitle = $"{tracked.Count} suivis sur {Svc.Devices?.TotalDevices} présents · {tracked.Count(DeviceMonitor.IsHub)} hub(s)/dock(s) · dernier scan {Svc.Devices?.LastScan:HH:mm:ss}";
            }
            var fresh = Hub.Since(_lastSeq);
            if (fresh.Count > 0)
            {
                _lastSeq = fresh[fresh.Count - 1].Seq;
                foreach (var ev in fresh.Where(e => e.Category == EventCategory.Usb || e.Category == EventCategory.UsbC || e.Category == EventCategory.Device))
                {
                    _activity.Rows.Insert(0, ev.Time.ToString("dd/MM HH:mm:ss"), Fr.Sev(ev.Severity), (ev.Trigger ? "⚑ " : "") + ev.Title);
                    _activity.Rows[0].Cells[1].Tag = Theme.SevColor(ev.Severity);
                    _activity.Rows[0].Tag = ev;
                }
                while (_activity.Rows.Count > 400) _activity.Rows.RemoveAt(_activity.Rows.Count - 1);
            }
        }
    }

    // ======================================================================
    public sealed class HistoryPage : PageBase
    {
        public override string Title => "Historique Windows";
        public override string Subtitle => "Analyse rétroactive des journaux Windows : ce qui s'est passé avant l'installation de Signal Watchdogs";

        readonly KpiTile _kGpu, _kUsb, _kSys, _kDwm, _kRdp, _kInc;
        readonly StackedBarChart _bars;
        readonly DataGridView _incGrid, _evGrid;
        readonly Label _status;
        readonly ComboBox _cat;
        readonly Card _cEvents, _cBars;
        DateTime? _renderedScan;
        Incident _selected;

        public HistoryPage(WatchdogService svc, MainForm main) : base(svc, main)
        {
            var bar = Toolbar();
            var rescan = Theme.MakeButton("Relancer l'analyse", Theme.Accent);
            rescan.Click += (s, e) => Svc.RunHistoryScanAsync();
            _cat = new ComboBox { Width = 170 };
            Theme.StyleInput(_cat);
            _cat.Items.AddRange(new object[] { "Événements importants", "Tous les événements", "GPU", "Écran", "USB / USB-C", "Système", "Session", "Alimentation" });
            _cat.SelectedIndex = 0;
            _cat.SelectedIndexChanged += (s, e) => { _selected = null; FillEvents(); };
            _status = Theme.MakeLabel("", Theme.Small, Theme.Muted);
            _status.Padding = new Padding(10, 8, 0, 0);
            bar.Controls.AddRange(new Control[] { rescan, _cat, _status });

            var root = Table(1, 3);
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
            var kpis = Table(6, 1);
            for (int i = 0; i < 6; i++) kpis.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 6));
            kpis.Controls.Add(_kGpu = new KpiTile("Crashs pilote GPU","", Theme.Purple), 0, 0);
            kpis.Controls.Add(_kUsb = new KpiTile("Erreurs USB/USB-C","", Theme.Teal), 1, 0);
            kpis.Controls.Add(_kSys = new KpiTile("Plantages système", "", Theme.Err), 2, 0);
            kpis.Controls.Add(_kDwm = new KpiTile("Crashs DWM/Explorer","", Theme.Accent), 3, 0);
            kpis.Controls.Add(_kRdp = new KpiTile("Sessions RDP","", Theme.Pink), 4, 0);
            kpis.Controls.Add(_kInc = new KpiTile("Incidents","", Theme.Warn), 5, 0);
            root.Controls.Add(kpis, 0, 0);

            _cBars = new Card("Événements par jour");
            _bars = new StackedBarChart();
            _cBars.Controls.Add(_bars);
            root.Controls.Add(_cBars, 0, 1);

            var bottom = Table(2, 1);
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            var cInc = new Card("Incidents reconstitués (cliquer pour filtrer)");
            _incGrid = Theme.Grid();
            _incGrid.Columns.Add(Theme.Col("Gravité", 18, 80));
            _incGrid.Columns.Add(Theme.Col("Date", 22, 100));
            _incGrid.Columns.Add(Theme.Col("Cause probable", 70, 150));
            _incGrid.Columns.Add(Theme.Col("Év.", 8, 40));
            PaintSeverityCell(_incGrid, 0);
            _incGrid.CellClick += (s, e) => { if (e.RowIndex >= 0 && _incGrid.Rows[e.RowIndex].Tag is Incident inc) { _selected = inc; FillEvents(); } };
            _incGrid.CellDoubleClick += (s, e) =>
            {
                if (e.RowIndex >= 0 && _incGrid.Rows[e.RowIndex].Tag is Incident inc)
                    MessageBox.Show(this, IncidentsPage.DiagnosisText(inc), "Incident reconstitué", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            cInc.Controls.Add(_incGrid);
            _cEvents = new Card("Événements");
            _evGrid = Theme.Grid();
            _evGrid.Columns.Add(Theme.Col("Date", 20, 100));
            _evGrid.Columns.Add(Theme.Col("Gravité", 13, 70));
            _evGrid.Columns.Add(Theme.Col("Catégorie", 13, 70));
            _evGrid.Columns.Add(Theme.Col("Événement", 60, 150));
            PaintSeverityCell(_evGrid, 1);
            _evGrid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0 && _evGrid.Rows[e.RowIndex].Tag is WatchEvent ev) MainForm.ShowEventDetails(this, ev); };
            _cEvents.Controls.Add(_evGrid);
            bottom.Controls.Add(cInc, 0, 0);
            bottom.Controls.Add(_cEvents, 1, 0);
            root.Controls.Add(bottom, 0, 2);

            Controls.Add(root);
            Controls.Add(bar);
        }

        public override void RefreshData()
        {
            var h = Svc.History;
            _status.Text = h.Running ? "Analyse en cours… " + h.Progress :
                h.ScannedAt.HasValue ? $"{h.Events.Count} événements analysés sur {h.Days} jours — analyse du {h.ScannedAt:dd/MM HH:mm}" : "Pas encore analysé";
            if (h.ScannedAt == _renderedScan) return;
            _renderedScan = h.ScannedAt;
            var evs = h.Events;
            int gpu = evs.Count(e => Codes.GpuCrashCodes.Contains(e.Code));
            int usb = evs.Count(e => e.Code == Codes.UcsiError || e.Code == Codes.XhciError || e.Code == Codes.UsbError || e.Code == Codes.LiveKernelUsb);
            int sys = evs.Count(e => e.Code == Codes.KernelPower41 || e.Code == Codes.Bsod);
            int dwm = evs.Count(e => e.Code == Codes.DwmCrash || e.Code == Codes.ExplorerRestart);
            int rdp = evs.Count(e => e.Code == Codes.RdpInConnect);
            _kGpu.Set(gpu.ToString(), "TDR · nvlddmkm · LiveKernelEvent", gpu > 0 ? Theme.Err : Theme.Ok);
            _kUsb.Set(usb.ToString(), "xHCI · UCSI · hubs", usb > 0 ? Theme.Warn : Theme.Ok);
            _kSys.Set(sys.ToString(), "Kernel-Power 41 · écrans bleus", sys > 0 ? Theme.Err : Theme.Ok);
            _kDwm.Set(dwm.ToString(), "compositeur / barre des tâches", dwm > 0 ? Theme.Warn : Theme.Ok);
            _kRdp.Set(rdp.ToString(), "connexions entrantes", Theme.Pink);
            _kInc.Set(h.Incidents.Count.ToString(), $"reconstitués sur {h.Days} jours", h.Incidents.Count > 0 ? Theme.Warn : Theme.Ok);

            int days = Math.Max(1, h.Days);
            var dayList = Enumerable.Range(0, days + 1).Select(i => DateTime.Today.AddDays(-days + i)).ToList();
            int[] Count(Func<WatchEvent, bool> pred) => dayList.Select(d => evs.Count(e => e.Time.Date == d && e.Severity >= Severity.Notice && pred(e))).ToArray();
            _bars.Days = dayList;
            _bars.Stacks = new List<StackedBarChart.Stack>
            {
                new StackedBarChart.Stack { Name = "GPU", Color = Theme.Purple, Values = Count(e => e.Category == EventCategory.Gpu) },
                new StackedBarChart.Stack { Name = "Écran", Color = Theme.Accent, Values = Count(e => e.Category == EventCategory.Display) },
                new StackedBarChart.Stack { Name = "USB / USB-C", Color = Theme.Teal, Values = Count(e => e.Category == EventCategory.Usb || e.Category == EventCategory.UsbC || e.Category == EventCategory.Device) },
                new StackedBarChart.Stack { Name = "Système", Color = Theme.Err, Values = Count(e => e.Category == EventCategory.System) },
                new StackedBarChart.Stack { Name = "Sessions", Color = Theme.Pink, Values = Count(e => e.Category == EventCategory.Session) },
            };
            _bars.Invalidate();

            _incGrid.Rows.Clear();
            foreach (var inc in h.Incidents.OrderByDescending(i => i.Start))
            {
                int r = _incGrid.Rows.Add(Fr.Sev(inc.Severity), inc.Start.ToString("dd/MM HH:mm:ss"), inc.ShortTitle, inc.Events.Count);
                _incGrid.Rows[r].Cells[0].Tag = Theme.SevColor(inc.Severity);
                _incGrid.Rows[r].Tag = inc;
            }
            _selected = null;
            FillEvents();
        }

        void FillEvents()
        {
            IEnumerable<WatchEvent> evs;
            if (_selected != null)
            {
                evs = _selected.Events;
                _cEvents.Title = $"Événements de l'incident du {_selected.Start:dd/MM HH:mm}";
            }
            else
            {
                var all = Svc.History.Events.AsEnumerable();
                evs = _cat.SelectedIndex switch
                {
                    0 => all.Where(e => e.Trigger || e.Severity >= Severity.Warning),
                    2 => all.Where(e => e.Category == EventCategory.Gpu),
                    3 => all.Where(e => e.Category == EventCategory.Display),
                    4 => all.Where(e => e.Category == EventCategory.Usb || e.Category == EventCategory.UsbC || e.Category == EventCategory.Device),
                    5 => all.Where(e => e.Category == EventCategory.System),
                    6 => all.Where(e => e.Category == EventCategory.Session),
                    7 => all.Where(e => e.Category == EventCategory.Power),
                    _ => all
                };
                _cEvents.Title = "Événements";
            }
            var list = evs.OrderByDescending(e => e.Time).Take(3000).ToList();
            _evGrid.SuspendLayout();
            _evGrid.Rows.Clear();
            foreach (var e in list)
            {
                int r = _evGrid.Rows.Add(e.Time.ToString("dd/MM HH:mm:ss"), Fr.Sev(e.Severity), Fr.Cat(e.Category), (e.Trigger ? "⚑ " : "") + e.Title);
                _evGrid.Rows[r].Cells[1].Tag = Theme.SevColor(e.Severity);
                _evGrid.Rows[r].Tag = e;
            }
            _evGrid.ResumeLayout();
            _cEvents.Subtitle = $"{list.Count} événement(s)";
        }
    }

    // ======================================================================
    public sealed class DiagnosticPage : PageBase
    {
        public override string Title => "Diagnostic de configuration";
        public override string Subtitle => "Réglages connus pour provoquer écrans noirs et scintillements + état des sondes";

        readonly DataGridView _checks, _sources;
        readonly Card _cChecks;
        List<CheckResult> _rendered;
        string _srcSig;

        public DiagnosticPage(WatchdogService svc, MainForm main) : base(svc, main)
        {
            var bar = Toolbar();
            var rerun = Theme.MakeButton("Relancer l'audit", Theme.Accent);
            rerun.Click += (s, e) => { Svc.Gpu?.RequestInventory(); Svc.RunDiagnosticsAsync(1500); };
            bar.Controls.Add(rerun);
            if (!Svc.IsAdmin)
            {
                var admin = Theme.MakeButton("Relancer en administrateur", Theme.Warn);
                admin.Click += (s, e) => Main.RestartElevated();
                bar.Controls.Add(admin);
            }
            foreach (var (label, target) in new[] { ("Paramètres d'affichage", "ms-settings:display"), ("Graphiques (HAGS)", "ms-settings:display-advancedgraphics"),
                                                    ("Gestionnaire de périphériques", "devmgmt.msc"), ("Observateur d'événements", "eventvwr.msc"), ("Options d'alimentation", "powercfg.cpl") })
            {
                var b = Theme.MakeButton(label);
                var t = target;
                b.Click += (s, e) => { try { Process.Start(new ProcessStartInfo(t) { UseShellExecute = true }); } catch { } };
                bar.Controls.Add(b);
            }

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, BackColor = Theme.Bg, SplitterWidth = 6 };
            split.Panel1.BackColor = split.Panel2.BackColor = Theme.Bg;
            _cChecks = new Card("Audit de configuration");
            _checks = Theme.Grid();
            _checks.Columns.Add(Theme.Col("Statut", 11, 90));
            _checks.Columns.Add(Theme.Col("Domaine", 10, 70));
            _checks.Columns.Add(Theme.Col("Vérification", 26, 120, wrap: true));
            _checks.Columns.Add(Theme.Col("Constat", 30, 120, wrap: true));
            _checks.Columns.Add(Theme.Col("Recommandation", 55, 150, wrap: true));
            _checks.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
            PaintSeverityCell(_checks, 0);
            _cChecks.Controls.Add(_checks);
            split.Panel1.Controls.Add(_cChecks);

            var cSrc = new Card("Sources de surveillance actives");
            _sources = Theme.Grid();
            _sources.Columns.Add(Theme.Col("État", 8, 70));
            _sources.Columns.Add(Theme.Col("Source", 30, 150));
            _sources.Columns.Add(Theme.Col("Détail", 62, 150));
            PaintSeverityCell(_sources, 0);
            cSrc.Controls.Add(_sources);
            split.Panel2.Controls.Add(cSrc);

            Controls.Add(split);
            Controls.Add(bar);
            Load += (s, e) => { try { split.SplitterDistance = (int)(Height * 0.6); } catch { } };
        }

        public override void RefreshData()
        {
            var checks = Svc.Checks;
            if (!ReferenceEquals(checks, _rendered))
            {
                _rendered = checks;
                _checks.Rows.Clear();
                foreach (var c in checks.OrderByDescending(c => c.Status))
                {
                    int r = _checks.Rows.Add(Theme.StatusText(c.Status), c.Area, c.Title, c.Detail, c.Recommendation ?? "");
                    _checks.Rows[r].Cells[0].Tag = Theme.StatusColor(c.Status);
                    if (c.Status >= CheckStatus.Warning) _checks.Rows[r].Cells[2].Style.Font = Theme.Bold;
                }
                _cChecks.Subtitle = $"{checks.Count(c => c.Status == CheckStatus.Problem)} problème(s) · {checks.Count(c => c.Status == CheckStatus.Warning)} point(s) d'attention";
            }
            var sources = Hub.Sources();
            if (Changed(ref _srcSig, string.Join("|", sources.Select(s => s.Name + s.Ok + s.Detail))))
            {
                _sources.Rows.Clear();
                foreach (var s in sources)
                {
                    int r = _sources.Rows.Add(s.Ok ? "Actif" : "Inactif", s.Name, s.Detail);
                    _sources.Rows[r].Cells[0].Tag = s.Ok ? Theme.Ok : Theme.Err;
                }
            }
        }
    }

    // ======================================================================
    public sealed class FleetPage : PageBase
    {
        public override string Title => "Vue parc (multi-postes)";
        public override string Subtitle => "Tous les postes qui écrivent dans le même dossier de journaux (partage réseau)";

        readonly DataGridView _grid;
        readonly Label _path;
        readonly Card _card;
        DateTime _last = DateTime.MinValue;

        public FleetPage(WatchdogService svc, MainForm main) : base(svc, main)
        {
            var bar = Toolbar();
            var refresh = Theme.MakeButton("Actualiser", Theme.Accent);
            refresh.Click += (s, e) => { _last = DateTime.MinValue; RefreshData(); };
            var open = Theme.MakeButton("Ouvrir le dossier");
            open.Click += (s, e) => MainForm.OpenFolder(Svc.Log.RootDirectory);
            _path = Theme.MakeLabel("", Theme.Small, Theme.Muted);
            _path.Padding = new Padding(10, 8, 0, 0);
            bar.Controls.AddRange(new Control[] { refresh, open, _path });

            var help = new Card("Comment ça marche") { Dock = DockStyle.Bottom, Height = 104 };
            help.Controls.Add(new Label
            {
                Dock = DockStyle.Fill, ForeColor = Theme.Muted, BackColor = Theme.Surface, Font = Theme.Small,
                Text = "Chaque poste écrit un fichier status.json (mis à jour chaque minute) dans <dossier des journaux>\\<NOM DU POSTE>. " +
                       "Lancez Signal Watchdogs sur chaque poste avec --logdir \\\\serveur\\wd-logs$ (partage dédié aux journaux, distinct de celui de l'exe, qui doit rester en lecture seule) : " +
                       "cette page affiche alors l'état de tout le parc. Les données des autres postes sont traitées comme non fiables (taille bornée, aucun chemin exécuté)."
            });

            _card = new Card("Postes");
            _grid = Theme.Grid();
            foreach (var (h, w) in new[] { ("État", 9f), ("Poste", 13f), ("Dernière remontée", 12f), ("Écrans", 6f), ("Incidents 24 h", 8f), ("Incidents 7 j", 8f), ("Plantages GPU 30 j", 9f),
                                           ("Erreurs USB 30 j", 8f), ("Signalements 7 j", 8f), ("Dernier incident", 26f), ("Cause n°1 (7 j)", 24f), ("Alertes config", 8f), ("GPU", 26f), ("Version", 6f) })
                _grid.Columns.Add(Theme.Col(h, w, 50));
            PaintSeverityCell(_grid, 0);
            _grid.CellDoubleClick += (s, e) =>
            {
                // On ouvre le dossier où le fichier a réellement été lu, jamais un chemin issu de son contenu
                // (un status.json piégé pourrait sinon pointer vers \\serveur-pirate\… ou ..\..).
                if (e.RowIndex >= 0 && _grid.Rows[e.RowIndex].Tag is Dictionary<string, object> d && d.TryGetValue("_folder", out var f) && f is string folder
                    && string.Equals(System.IO.Path.GetDirectoryName(folder.TrimEnd('\\')), Svc.Log.RootDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    MainForm.OpenFolder(folder, create: false);
            };
            _card.Controls.Add(_grid);
            Controls.Add(_card);
            Controls.Add(help);
            Controls.Add(bar);
        }

        static string S(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : "";
        static int I(Dictionary<string, object> d, string k) => int.TryParse(S(d, k), out var i) ? i : 0;

        public override void RefreshData()
        {
            if ((DateTime.Now - _last).TotalSeconds < 30) return;
            _last = DateTime.Now;
            _path.Text = Svc.Log.RootDirectory;
            var root = Svc.Log.RootDirectory;
            System.Threading.Tasks.Task.Run(() => WatchdogService.ReadFleet(root)).ContinueWith(t =>
            {
                if (IsDisposed || t.Result == null) return;
                BeginInvoke(new Action(() => Fill(t.Result)));
            });
        }

        void Fill(List<Dictionary<string, object>> fleet)
        {
            _grid.Rows.Clear();
            foreach (var d in fleet.OrderByDescending(x => I(x, "incidents24h")).ThenByDescending(x => I(x, "incidents7d")).ThenBy(x => S(x, "machine")))
            {
                DateTime.TryParse(S(d, "updated"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var upd);
                bool stale = (DateTime.Now - upd).TotalMinutes > 10;
                int i24 = I(d, "incidents24h"), gpu = I(d, "gpuCrashes30d");
                var state = stale ? "Hors ligne" : i24 > 0 || gpu > 0 ? "À surveiller" : "OK";
                DateTime.TryParse(S(d, "lastIncidentTime"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lastT);
                var gpus = d.TryGetValue("gpus", out var g) && g is System.Collections.IEnumerable list && !(g is string) ? string.Join(" ; ", list.Cast<object>()) : "";
                // Le nom affiché est celui du dossier (réel), pas celui déclaré dans le fichier (falsifiable).
                var folderName = System.IO.Path.GetFileName(S(d, "_folder").TrimEnd('\\'));
                bool mismatch = !string.Equals(folderName, S(d, "machine"), StringComparison.OrdinalIgnoreCase);
                var posteText = mismatch ? $"{folderName} ⚠ déclare « {S(d, "machine")} »" : folderName;
                int r = _grid.Rows.Add(state, posteText, upd == default ? "?" : Fr.Ago(upd), I(d, "monitorsCount"), i24, I(d, "incidents7d"), gpu, I(d, "usbErrors30d"), I(d, "userReports7d"),
                    lastT == default ? "—" : $"{lastT:dd/MM HH:mm} · {S(d, "lastIncident")}", S(d, "topCause7d"), I(d, "warnings"), gpus, S(d, "version"));
                _grid.Rows[r].Cells[0].Tag = stale ? Theme.Faint : i24 > 0 || gpu > 0 ? Theme.Warn : Theme.Ok;
                _grid.Rows[r].Tag = d;
                if (mismatch) _grid.Rows[r].Cells[1].Style.ForeColor = Theme.Err;
                else if (folderName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) _grid.Rows[r].Cells[1].Style.Font = Theme.Bold;
            }
            _card.Subtitle = $"{fleet.Count} poste(s) — actualisé à {DateTime.Now:HH:mm:ss}";
        }
    }

    // ======================================================================
    public sealed class SettingsPage : PageBase
    {
        public override string Title => "Paramètres";
        public override string Subtitle => Settings.IsInstalled
            ? "Version installée : réglages machine (Program Files) + préférences de l'utilisateur"
            : "Réglages enregistrés dans SignalWatchdogs.ini à côté de l'exécutable (portable)";

        readonly TextBox _logDir, _hotkey, _extra;
        readonly NumericUpDown _retention, _history, _topo, _gpu, _dev, _stall, _temp;
        readonly CheckBox _notif, _minimized, _nvsmi, _info, _autostart;
        readonly Label _msg;

        public SettingsPage(WatchdogService svc, MainForm main) : base(svc, main)
        {
            var s = svc.Settings;
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg };
            Theme.DarkScroll(scroll);
            var card = new Card("Configuration") { Dock = DockStyle.Top, Height = 730 };
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, BackColor = Theme.Surface, AutoSize = false };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 420));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            int row = 0;
            void AddRow(string label, Control input, string help = null)
            {
                t.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
                var l = Theme.MakeLabel(label, Theme.Base, Theme.Text);
                l.Anchor = AnchorStyles.Left;
                t.Controls.Add(l, 0, row);
                input.Anchor = AnchorStyles.Left | AnchorStyles.Right;
                if (input is CheckBox) input.Anchor = AnchorStyles.Left;
                t.Controls.Add(input, 1, row);
                if (help != null)
                {
                    var h = Theme.MakeLabel(help, Theme.Small, Theme.Muted);
                    h.Anchor = AnchorStyles.Left;
                    t.Controls.Add(h, 2, row);
                }
                row++;
            }
            NumericUpDown Num(int value, int min, int max)
            {
                var n = new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, value)), Width = 120 };
                Theme.StyleInput(n);
                return n;
            }
            CheckBox Chk(string text, bool value) => new CheckBox { Text = text, Checked = value, ForeColor = Theme.Text, AutoSize = true };

            var dirPanel = new FlowLayoutPanel { Height = 30, WrapContents = false, BackColor = Theme.Surface, Margin = new Padding(0) };
            _logDir = new TextBox { Text = s.LogDirectory, Width = 320 };
            Theme.StyleInput(_logDir);
            var browse = Theme.MakeButton("…", null, 40);
            browse.Height = 26;
            browse.Click += (o, e) =>
            {
                using var dlg = new FolderBrowserDialog { Description = "Dossier des journaux Signal Watchdogs (local ou partage réseau)" };
                if (dlg.ShowDialog(this) == DialogResult.OK) _logDir.Text = dlg.SelectedPath;
            };
            dirPanel.Controls.Add(_logDir);
            dirPanel.Controls.Add(browse);
            AddRow("Dossier des journaux", dirPanel, "Vide = SignalWatchdogs_Logs à côté de l'exe. Un partage réseau active la vue Parc.");
            AddRow("Conservation des journaux (jours)", _retention = Num(s.RetentionDays, 1, 3650));
            AddRow("Historique Windows analysé (jours)", _history = Num(s.HistoryDays, 1, 365));
            AddRow("Interrogation de la topologie (ms)", _topo = Num(s.TopologyPollMs, 100, 5000), "250 ms détecte les coupures très brèves.");
            AddRow("Échantillonnage GPU (ms)", _gpu = Num(s.GpuSampleMs, 500, 60000));
            AddRow("Scan périodique des périphériques (s)", _dev = Num(s.DevicePollSeconds, 2, 600), "En plus des notifications temps réel.");
            AddRow("Seuil de gel système (ms)", _stall = Num(s.StallThresholdMs, 500, 30000));
            AddRow("Alerte température GPU (°C)", _temp = Num(s.GpuTempWarning, 50, 120));
            _hotkey = new TextBox { Text = s.Hotkey };
            Theme.StyleInput(_hotkey);
            AddRow("Raccourci « écran noir »", _hotkey, "Ex. Ctrl+Alt+Shift+B. À utiliser juste après un écran noir.");
            _extra = new TextBox { Text = s.ExtraWatchedApps };
            Theme.StyleInput(_extra);
            AddRow("Applications à surveiller en plus", _extra, "Noms d'exécutables séparés par des virgules (ex. citrix.exe, vlc.exe)");
            AddRow("Notifications", _notif = Chk("Afficher une notification à chaque incident", s.Notifications));
            AddRow("Démarrage", _minimized = Chk("Démarrer réduit dans la zone de notification", s.StartMinimized));
            if (Settings.MachineAutoStart)
            {
                AddRow("Démarrer avec Windows", _autostart = Chk("Lancé pour tous les utilisateurs (configuré par l'installation)", true),
                    "Se modifie en relançant le setup (tâche « démarrage automatique »).");
                _autostart.Enabled = false;
            }
            else
                AddRow("Démarrer avec Windows", _autostart = Chk("Lancer Signal Watchdogs à l'ouverture de session (utilisateur courant)", Settings.AutoStartEnabled),
                    "Utilise le chemin actuel de l'exe : à éviter depuis une clé USB.");
            AddRow("Télémétrie NVIDIA", _nvsmi = Chk("Utiliser nvidia-smi (température, PCIe, puissance)", s.UseNvidiaSmi));
            AddRow("Verbosité", _info = Chk("Journaliser aussi les événements « Info »", s.LogInfoEvents));

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Theme.Surface, Margin = new Padding(0) };
            var save = Theme.MakeButton("Enregistrer", Theme.Accent);
            save.Click += (o, e) => Save();
            var openIni = Theme.MakeButton("Ouvrir le fichier de configuration");
            openIni.Click += (o, e) => { try { Process.Start(new ProcessStartInfo(Svc.Settings.FilePath) { UseShellExecute = true }); } catch { } };
            var openLogs = Theme.MakeButton("Ouvrir le dossier des journaux");
            openLogs.Click += (o, e) => MainForm.OpenFolder(Svc.Log.MachineDirectory);
            buttons.Controls.AddRange(new Control[] { save, openIni, openLogs });
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            t.Controls.Add(buttons, 0, row);
            t.SetColumnSpan(buttons, 3);
            row++;
            _msg = Theme.MakeLabel("", Theme.Small, Theme.Ok);
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            t.Controls.Add(_msg, 0, row);
            t.SetColumnSpan(_msg, 3);
            card.Controls.Add(t);

            var about = new Card("À propos") { Dock = DockStyle.Top, Height = 150 };
            about.Controls.Add(new Label
            {
                Dock = DockStyle.Fill, BackColor = Theme.Surface, ForeColor = Theme.Muted, Font = Theme.Small,
                Text = $"Signal Watchdogs {WatchdogService.Version} — surveillance des écrans, cartes graphiques, USB/USB-C et sessions distantes.\n" +
                       $"Exécutable : {Settings.ExePath}\nConfiguration : {s.FilePath}\nJournaux : {Svc.Log.MachineDirectory}\n" +
                       $"Droits : {(Svc.IsAdmin ? "administrateur" : "standard")} · .NET Framework {Environment.Version} · {SystemDiagnostics.OsVersion()}\n" +
                       "Ligne de commande : --minimized · --report <fichier.html> · --logdir <dossier>"
            });
            scroll.Controls.Add(about);
            scroll.Controls.Add(card);
            Controls.Add(scroll);
        }

        void Save()
        {
            var s = Svc.Settings;
            if (!string.IsNullOrWhiteSpace(_hotkey.Text) && !SystemMessageWindow.TryParseHotkey(_hotkey.Text, out _, out _))
            {
                _msg.ForeColor = Theme.Err;
                _msg.Text = "Raccourci invalide (ex. Ctrl+Alt+Shift+B).";
                return;
            }
            bool restart = s.LogDirectory != _logDir.Text.Trim() || s.TopologyPollMs != (int)_topo.Value || s.GpuSampleMs != (int)_gpu.Value
                           || s.DevicePollSeconds != (int)_dev.Value || s.StallThresholdMs != (int)_stall.Value || s.ExtraWatchedApps != _extra.Text.Trim() || s.UseNvidiaSmi != _nvsmi.Checked;
            s.LogDirectory = _logDir.Text.Trim();
            s.RetentionDays = (int)_retention.Value;
            s.HistoryDays = (int)_history.Value;
            s.TopologyPollMs = (int)_topo.Value;
            s.GpuSampleMs = (int)_gpu.Value;
            s.DevicePollSeconds = (int)_dev.Value;
            s.StallThresholdMs = (int)_stall.Value;
            s.GpuTempWarning = (int)_temp.Value;
            s.Hotkey = _hotkey.Text.Trim();
            s.ExtraWatchedApps = _extra.Text.Trim();
            s.Notifications = _notif.Checked;
            s.StartMinimized = _minimized.Checked;
            s.UseNvidiaSmi = _nvsmi.Checked;
            s.LogInfoEvents = _info.Checked;
            if (_autostart.Enabled) try { Settings.SetAutoStart(_autostart.Checked); } catch { }
            if (!s.Save(out var err))
            {
                _msg.ForeColor = Theme.Err;
                _msg.Text = "Échec de l'enregistrement : " + err;
                return;
            }
            Svc.Messages?.RegisterHotkey(s.Hotkey);
            _msg.ForeColor = Theme.Ok;
            _msg.Text = $"Enregistré dans {s.FilePath}." + (restart ? " Certains réglages seront appliqués au prochain démarrage de Signal Watchdogs." : "");
        }

        public override void RefreshData() { }
    }
}
