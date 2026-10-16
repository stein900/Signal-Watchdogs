using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using SignalWatchdogs.Core;
using SignalWatchdogs.Monitors;

namespace SignalWatchdogs.UI
{
    public abstract class PageBase : UserControl
    {
        protected readonly WatchdogService Svc;
        protected readonly MainForm Main;
        public abstract string Title { get; }
        public virtual string Subtitle => "";

        protected PageBase(WatchdogService svc, MainForm main)
        {
            Svc = svc;
            Main = main;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Base;
            Dock = DockStyle.Fill;
            Padding = new Padding(10, 4, 10, 10);
            Theme.SetDoubleBuffered(this);
        }

        public abstract void RefreshData();
        public virtual void Select(object arg) { }

        protected static TableLayoutPanel Table(int cols, int rows)
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = cols, RowCount = rows, BackColor = Theme.Bg, Margin = new Padding(0), Padding = new Padding(0) };
            Theme.SetDoubleBuffered(t);
            return t;
        }

        protected static FlowLayoutPanel Toolbar()
        {
            return new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, BackColor = Theme.Bg, Padding = new Padding(4, 6, 4, 0), WrapContents = false, AutoScroll = false };
        }

        /// <summary>Barre d'outils avec une zone gauche (filtres) et une zone droite (actions).</summary>
        protected static (Panel bar, FlowLayoutPanel left, FlowLayoutPanel right) SplitToolbar()
        {
            var bar = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Theme.Bg };
            var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = Theme.Bg, Padding = new Padding(0, 6, 4, 0) };
            var left = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Theme.Bg, Padding = new Padding(4, 6, 4, 0) };
            bar.Controls.Add(left);
            bar.Controls.Add(right);
            return (bar, left, right);
        }

        /// <summary>Reconstruit un DataGridView uniquement si la signature des données a changé (évite le scintillement).</summary>
        protected static bool Changed(ref string signature, string newSignature)
        {
            if (signature == newSignature) return false;
            signature = newSignature;
            return true;
        }

        protected static void PaintSeverityCell(DataGridView grid, int colIndex)
        {
            grid.CellPainting += (s, e) =>
            {
                if (e.RowIndex < 0 || e.ColumnIndex != colIndex) return;
                e.PaintBackground(e.CellBounds, (e.State & DataGridViewElementStates.Selected) != 0);
                var tag = grid.Rows[e.RowIndex].Cells[colIndex].Tag;
                var color = tag is Color c ? c : Theme.Muted;
                var r = e.CellBounds;
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(color)) e.Graphics.FillEllipse(b, r.Left + 8, r.Top + r.Height / 2 - 5, 10, 10);
                TextRenderer.DrawText(e.Graphics, Convert.ToString(e.FormattedValue), Theme.Base, new Rectangle(r.Left + 24, r.Top, r.Width - 26, r.Height), Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                e.Handled = true;
            };
        }
    }

    // ======================================================================
    public sealed class DashboardPage : PageBase
    {
        public override string Title => "Tableau de bord";
        public override string Subtitle => "Vue temps réel des écrans, cartes graphiques, USB et incidents";

        readonly KpiTile _kScreens, _kInc, _kLost, _kGpu, _kUsb, _kSession;
        readonly MonitorLayoutControl _layout;
        readonly LineChart _chart;
        readonly TimelineControl _timeline;
        readonly DataGridView _incGrid;
        readonly Card _cChart, _cLayout, _cInc;
        string _incSig, _seriesSig;

        public DashboardPage(WatchdogService svc, MainForm main) : base(svc, main)
        {
            var root = Table(1, 3);
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 46));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 54));

            var kpis = Table(6, 1);
            for (int i = 0; i < 6; i++) kpis.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 6));
            kpis.Controls.Add(_kScreens = new KpiTile("Écrans actifs", "", Theme.Accent), 0, 0);
            kpis.Controls.Add(_kInc = new KpiTile("Incidents (24 h)", "", Theme.Ok), 1, 0);
            kpis.Controls.Add(_kLost = new KpiTile("Pertes écran (24 h)", "", Theme.Accent), 2, 0);
            kpis.Controls.Add(_kGpu = new KpiTile("Crashs GPU (30 j)", "", Theme.Purple), 3, 0);
            kpis.Controls.Add(_kUsb = new KpiTile("Erreurs USB (30 j)", "", Theme.Teal), 4, 0);
            kpis.Controls.Add(_kSession = new KpiTile("Session", "", Theme.Pink), 5, 0);
            root.Controls.Add(kpis, 0, 0);

            var mid = Table(2, 1);
            mid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            mid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            _cLayout = new Card("Disposition des écrans");
            _layout = new MonitorLayoutControl
            {
                Monitors = () => Svc.Topology?.Current,
                Lost = () => Svc.Topology?.LostNow,
                Stats = () => Svc.Topology?.Stats()
            };
            _cLayout.Controls.Add(_layout);
            _cChart = new Card("Charge GPU / CPU (10 min)");
            _chart = new LineChart();
            _cChart.Controls.Add(_chart);
            mid.Controls.Add(_cLayout, 0, 0);
            mid.Controls.Add(_cChart, 1, 0);
            root.Controls.Add(mid, 0, 1);

            var bottom = Table(2, 1);
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
            var cTimeline = new Card("Chronologie");
            _timeline = new TimelineControl
            {
                EventSource = () => Hub.Range(DateTime.Now - TimeSpan.FromHours(24), DateTime.Now),
                Incidents = () => Svc.AllIncidents()
            };
            _timeline.EventClicked += ev => { if (ev.IncidentId > 0) Main.Navigate("incidents", ev.IncidentId); else Main.Navigate("journal", ev.Seq); };
            cTimeline.Controls.Add(_timeline);
            _cInc = new Card("Derniers incidents");
            _incGrid = Theme.Grid();
            _incGrid.Columns.Add(Theme.Col("", 22, 64));
            _incGrid.Columns.Add(Theme.Col("Heure", 30, 70));
            _incGrid.Columns.Add(Theme.Col("Diagnostic", 100, 120));
            _incGrid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0 && _incGrid.Rows[e.RowIndex].Tag is Incident inc) Main.Navigate("incidents", inc.Id); };
            PaintSeverityCell(_incGrid, 0);
            _cInc.Controls.Add(_incGrid);
            bottom.Controls.Add(cTimeline, 0, 0);
            bottom.Controls.Add(_cInc, 1, 0);
            root.Controls.Add(bottom, 0, 2);
            Controls.Add(root);
        }

        void EnsureSeries()
        {
            var adapters = (Svc.Gpu?.Adapters ?? new List<GpuAdapterState>()).Where(a => a.HasLuid).ToList();
            var sig = string.Join("|", adapters.Select(a => a.Key));
            if (!Changed(ref _seriesSig, sig)) return;
            _chart.SeriesList.Clear();
            int i = 0;
            foreach (var a in adapters)
            {
                var ad = a;
                _chart.SeriesList.Add(new LineChart.Series { Name = MonitorLayoutControl.ShortGpu(a.Name), Color = Theme.SeriesColors[i++ % Theme.SeriesColors.Length], Data = () => ad.UtilHistory.Snapshot(TimeSpan.FromMinutes(10)) });
            }
            _chart.SeriesList.Add(new LineChart.Series { Name = "CPU", Color = Theme.Muted, Data = () => Svc.Gpu.Cpu.Snapshot(TimeSpan.FromMinutes(10)) });
        }

        public override void RefreshData()
        {
            var now = DateTime.Now;
            var mons = Svc.Topology?.Current ?? new List<MonitorInfo>();
            var lostNow = Svc.Topology?.LostNow?.Count ?? 0;
            _kScreens.Set(mons.Count.ToString(), lostNow > 0 ? $"{lostNow} écran(s) perdu(s) !" : string.Join(" · ", mons.GroupBy(m => m.Connector).Select(gr => $"{gr.Count()}× {gr.Key}")),
                lostNow > 0 ? Theme.Err : Theme.Accent);

            var incs = Svc.AllIncidents();
            var last24 = incs.Where(i => i.Start >= now.AddHours(-24)).ToList();
            int open = incs.Count(i => !i.Closed);
            var lastInc = incs.OrderByDescending(i => i.Start).FirstOrDefault();
            _kInc.Set(last24.Count.ToString(),
                open > 0 ? $"{open} en cours d'analyse" : lastInc != null ? "dernier " + Fr.Ago(lastInc.Start) : "aucun incident",
                open > 0 ? Theme.Crit : last24.Count > 0 ? Theme.Warn : Theme.Ok);

            int lost24 = Hub.Count(e => e.Code == Codes.DisplayLost && e.Time >= now.AddHours(-24));
            var down = TimeSpan.FromTicks((Svc.Topology?.Stats() ?? new List<MonitorStats>()).Sum(s => s.Downtime.Ticks));
            _kLost.Set(lost24.ToString(), lost24 > 0 ? $"noir cumulé {Fr.Duration(down)}" : "aucune perte de signal", lost24 > 0 ? Theme.Err : Theme.Ok);

            var since30 = now.AddDays(-30);
            int gpu = Svc.CombinedCount(e => Codes.GpuCrashCodes.Contains(e.Code), since30);
            _kGpu.Set(gpu.ToString(), Svc.History.Running ? "analyse de l'historique…" : "TDR · erreurs pilote · LiveKernel", gpu > 0 ? Theme.Err : Theme.Ok);
            int usb = Svc.CombinedCount(e => e.Code == Codes.UcsiError || e.Code == Codes.XhciError || e.Code == Codes.UsbError || e.Code == Codes.LiveKernelUsb, since30);
            _kUsb.Set(usb.ToString(), Svc.History.Running ? "analyse de l'historique…" : "contrôleurs xHCI · UCSI · hubs", usb > 0 ? Theme.Warn : Theme.Ok);

            var session = ProcessMonitor.SessionState();
            var apps = Svc.Processes?.ActiveApps ?? new List<string>();
            _kSession.Set(session == "Console locale" ? "Console" : session.Contains("RDP") ? "RDP" : "Hors console",
                apps.Count > 0 ? string.Join(", ", apps) : "aucune appli de streaming / RDP", session == "Console locale" ? Theme.Pink : Theme.Warn);

            EnsureSeries();
            _cChart.Subtitle = Svc.Gpu?.ContextSummary()?.Replace("GPU : ", "") ?? "";
            _cLayout.Subtitle = Svc.Topology != null && Svc.Topology.Accessible ? $"interrogée toutes les {Svc.Settings.TopologyPollMs} ms" : "topologie inaccessible (session hors console)";
            _layout.Invalidate();
            _chart.Invalidate();
            _timeline.Invalidate();

            var recent = incs.OrderByDescending(i => i.Start).Take(30).ToList();
            var sig = string.Join(";", recent.Select(i => $"{i.Id}{i.Closed}{i.Events.Count}{i.Diagnosis.Title}"));
            if (Changed(ref _incSig, sig))
            {
                _incGrid.Rows.Clear();
                foreach (var inc in recent)
                {
                    int r = _incGrid.Rows.Add(inc.Closed ? Fr.Sev(inc.Severity) : "En cours", inc.Start.ToString(inc.Start.Date == DateTime.Today ? "HH:mm:ss" : "dd/MM HH:mm"), inc.ShortTitle);
                    _incGrid.Rows[r].Cells[0].Tag = inc.Closed ? Theme.SevColor(inc.Severity) : Theme.Crit;
                    _incGrid.Rows[r].Tag = inc;
                    if (inc.Restored) _incGrid.Rows[r].DefaultCellStyle.ForeColor = Theme.Muted;
                }
                if (recent.Count == 0)
                {
                    int r = _incGrid.Rows.Add("OK", "", "Aucun incident — surveillance en cours");
                    _incGrid.Rows[r].Cells[0].Tag = Theme.Ok;
                    _incGrid.Rows[r].DefaultCellStyle.ForeColor = Theme.Muted;
                }
                _cInc.Subtitle = $"{incs.Count} au total (7 j)";
            }
        }
    }

    // ======================================================================
    public sealed class IncidentsPage : PageBase
    {
        public override string Title => "Incidents";
        public override string Subtitle => "Anomalies corrélées automatiquement, avec la cause probable et les actions à mener";

        readonly DataGridView _grid, _events;
        readonly RichTextBox _detail;
        readonly ComboBox _period;
        readonly CheckBox _minor;
        readonly Card _cList;
        string _sig;
        int _selectedId = -1;
        Incident _shown;
        string _shownSig;

        public IncidentsPage(WatchdogService svc, MainForm main) : base(svc, main)
        {
            var bar = Toolbar();
            _period = new ComboBox { Width = 190 };
            Theme.StyleInput(_period);
            _period.Items.AddRange(new object[] { "24 dernières heures", "7 derniers jours", "Tous" });
            _period.SelectedIndex = 1;
            _period.SelectedIndexChanged += (s, e) => { _sig = null; RefreshData(); };
            _minor = new CheckBox { Text = "Masquer les incidents mineurs", ForeColor = Theme.Text, AutoSize = true, Padding = new Padding(10, 5, 0, 0) };
            _minor.CheckedChanged += (s, e) => { _sig = null; RefreshData(); };
            var copy = Theme.MakeButton("Copier le diagnostic");
            copy.Click += (s, e) => { if (_shown != null) { try { Clipboard.SetText(DiagnosisText(_shown)); } catch { } } };
            var report = Theme.MakeButton("Signaler un écran noir maintenant", Theme.Err);
            report.Click += (s, e) => Main.ReportFromUi();
            bar.Controls.AddRange(new Control[] { _period, _minor, copy, report });

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, BackColor = Theme.Bg, SplitterWidth = 6 };
            split.Panel1.BackColor = split.Panel2.BackColor = Theme.Bg;
            _cList = new Card("Incidents");
            _grid = Theme.Grid();
            _grid.Columns.Add(Theme.Col("Gravité", 24, 90));
            _grid.Columns.Add(Theme.Col("Début", 26, 110));
            _grid.Columns.Add(Theme.Col("Durée", 16, 70));
            _grid.Columns.Add(Theme.Col("Diagnostic (cause probable)", 100, 200));
            _grid.Columns.Add(Theme.Col("Confiance", 16, 70));
            _grid.Columns.Add(Theme.Col("Écran(s)", 40, 90));
            _grid.Columns.Add(Theme.Col("Applications actives", 40, 90));
            PaintSeverityCell(_grid, 0);
            _grid.SelectionChanged += (s, e) =>
            {
                if (_grid.SelectedRows.Count > 0 && _grid.SelectedRows[0].Tag is Incident inc) { _selectedId = inc.Id; ShowDetail(inc); }
            };
            _cList.Controls.Add(_grid);
            split.Panel1.Controls.Add(_cList);

            var bottom = Table(2, 1);
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            var cDetail = new Card("Diagnostic");
            _detail = new RichTextBox { Dock = DockStyle.Fill, BackColor = Theme.Surface, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, ReadOnly = true, Font = Theme.Base, DetectUrls = false };
            Theme.DarkScroll(_detail);
            cDetail.Controls.Add(_detail);
            var cEvents = new Card("Chronologie de l'incident");
            _events = Theme.Grid();
            _events.Columns.Add(Theme.Col("Heure", 22, 90));
            _events.Columns.Add(Theme.Col("Gravité", 16, 80));
            _events.Columns.Add(Theme.Col("Catégorie", 16, 80));
            _events.Columns.Add(Theme.Col("Événement", 60, 150));
            _events.Columns.Add(Theme.Col("Détails", 70, 100));
            PaintSeverityCell(_events, 1);
            _events.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0 && _events.Rows[e.RowIndex].Tag is WatchEvent ev) MainForm.ShowEventDetails(this, ev); };
            cEvents.Controls.Add(_events);
            bottom.Controls.Add(cDetail, 0, 0);
            bottom.Controls.Add(cEvents, 1, 0);
            split.Panel2.Controls.Add(bottom);

            Controls.Add(split);
            Controls.Add(bar);
            Load += (s, e) => { try { split.SplitterDistance = (int)(Height * 0.42); } catch { } };
        }

        public override void Select(object arg)
        {
            if (arg is int id) { _selectedId = id; _sig = null; _period.SelectedIndex = 2; RefreshData(); }
        }

        List<Incident> Filtered()
        {
            var all = Svc.AllIncidents().AsEnumerable();
            if (_period.SelectedIndex == 0) all = all.Where(i => i.Start >= DateTime.Now.AddHours(-24));
            else if (_period.SelectedIndex == 1) all = all.Where(i => i.Start >= DateTime.Now.AddDays(-7));
            if (_minor.Checked) all = all.Where(i => i.Severity >= Severity.Warning);
            return all.OrderByDescending(i => i.Start).ToList();
        }

        public override void RefreshData()
        {
            var list = Filtered();
            var sig = string.Join(";", list.Select(i => $"{i.Id}{i.Closed}{i.Events.Count}{i.Diagnosis.Title}"));
            if (Changed(ref _sig, sig))
            {
                _grid.SuspendLayout();
                _grid.Rows.Clear();
                foreach (var inc in list)
                {
                    var dur = inc.Diagnosis.Factors.FirstOrDefault(f => f.StartsWith("Durée du noir"))?.Replace("Durée du noir mesurée : ", "") ?? (inc.Duration.TotalSeconds >= 1 ? Fr.Duration(inc.Duration) : "—");
                    int r = _grid.Rows.Add(inc.Closed ? Fr.Sev(inc.Severity) : "En cours", inc.Start.ToString("dd/MM HH:mm:ss"), dur, inc.ShortTitle, inc.Diagnosis.Confidence,
                        string.Join(", ", inc.AffectedMonitors), string.Join(", ", inc.ActiveApps));
                    _grid.Rows[r].Cells[0].Tag = inc.Closed ? Theme.SevColor(inc.Severity) : Theme.Crit;
                    _grid.Rows[r].Tag = inc;
                    if (inc.Restored) _grid.Rows[r].DefaultCellStyle.ForeColor = Theme.Muted;
                }
                _grid.ResumeLayout();
                _cList.Subtitle = $"{list.Count} incident(s) — double-cliquer un événement pour le détail";
                // Les lignes sont sélectionnées automatiquement pendant l'ajout, avant que leur Tag soit renseigné :
                // on choisit donc la ligne explicitement puis on affiche son diagnostic.
                var target = _grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(row => row.Tag is Incident i && i.Id == _selectedId)
                             ?? (_grid.Rows.Count > 0 ? _grid.Rows[0] : null);
                if (target != null)
                {
                    _grid.ClearSelection();
                    target.Selected = true;
                    try { _grid.CurrentCell = target.Cells[1]; } catch { }
                    if (target.Index > 0) try { _grid.FirstDisplayedScrollingRowIndex = target.Index; } catch { }
                    var inc = (Incident)target.Tag;
                    _selectedId = inc.Id;
                    ShowDetail(inc);
                }
                else { _detail.Clear(); _events.Rows.Clear(); _shown = null; WriteEmpty(); }
            }
            if (_shown != null && $"{_shown.Events.Count}{_shown.Closed}{_shown.Diagnosis.Title}" != _shownSig) ShowDetail(_shown);
        }

        void WriteEmpty()
        {
            _detail.Clear();
            Append("Aucun incident sur la période.\n\n", Theme.H2, Theme.Ok);
            Append("Signal Watchdogs surveille en continu les écrans, la carte graphique, l'USB/USB-C, les sessions RDP et les applications de streaming. " +
                   "Chaque anomalie (écran perdu, plantage du pilote, dock qui décroche…) crée ici un incident avec sa cause probable.\n\n", Theme.Base, Theme.Muted);
            Append($"Si un écran devient noir sans qu'aucun incident n'apparaisse, appuyez sur {Svc.Settings.Hotkey} : le signalement sera corrélé avec tout ce qui s'est passé juste avant.", Theme.Base, Theme.Text);
        }

        void Append(string text, Font font, Color color)
        {
            _detail.SelectionStart = _detail.TextLength;
            _detail.SelectionLength = 0;
            _detail.SelectionFont = font;
            _detail.SelectionColor = color;
            _detail.AppendText(text);
        }

        void ShowDetail(Incident inc)
        {
            _shown = inc;
            _shownSig = $"{inc.Events.Count}{inc.Closed}{inc.Diagnosis.Title}";
            var d = inc.Diagnosis;
            _detail.SuspendLayout();
            _detail.Clear();
            Append(inc.ShortTitle + "\n", Theme.H1, Theme.SevColor(inc.Severity));
            Append($"Confiance : {d.Confidence}   ·   {inc.Start:dd/MM/yyyy HH:mm:ss}   ·   {(inc.Closed ? "clos" : "en cours d'analyse")}" + (inc.Restored ? "   ·   archivé" : "") + "\n\n", Theme.Small, Theme.Muted);
            Append(d.Explanation + "\n\n", Theme.Base, Theme.Text);
            if (d.Factors.Count > 0)
            {
                Append("Éléments relevés\n", Theme.H2, Theme.Text);
                foreach (var f in d.Factors) Append("  •  " + f + "\n", Theme.Base, Theme.Blend(Theme.Muted, Theme.Text, 0.5));
                Append("\n", Theme.Small, Theme.Text);
            }
            if (d.Recommendations.Count > 0)
            {
                Append("Que faire ?\n", Theme.H2, Theme.Accent);
                int n = 1;
                foreach (var r in d.Recommendations) Append($"  {n++}.  {r}\n", Theme.Base, Theme.Text);
                Append("\n", Theme.Small, Theme.Text);
            }
            if (!string.IsNullOrEmpty(inc.Context)) Append("Contexte : " + inc.Context + "\n", Theme.Small, Theme.Muted);
            _detail.SelectionStart = 0;
            _detail.ScrollToCaret();
            _detail.ResumeLayout();

            _events.SuspendLayout();
            _events.Rows.Clear();
            foreach (var ev in inc.EventList().OrderBy(x => x.Time))
            {
                int r = _events.Rows.Add(ev.Time.ToString("HH:mm:ss.fff"), Fr.Sev(ev.Severity), Fr.Cat(ev.Category), (ev.Trigger ? "⚑ " : "") + ev.Title, Flat(ev.Details));
                _events.Rows[r].Cells[1].Tag = Theme.SevColor(ev.Severity);
                _events.Rows[r].Tag = ev;
                if (ev.Trigger) _events.Rows[r].DefaultCellStyle.Font = Theme.Bold;
            }
            _events.ResumeLayout();
        }

        static string Flat(string s) => (s ?? "").Replace("\r", " ").Replace("\n", " ");

        public static string DiagnosisText(Incident inc)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[Signal Watchdogs] {Environment.MachineName} — incident du {inc.Start:dd/MM/yyyy HH:mm:ss}");
            sb.AppendLine($"Diagnostic : {inc.Diagnosis.Title} (confiance {inc.Diagnosis.Confidence})");
            sb.AppendLine(inc.Diagnosis.Explanation);
            foreach (var f in inc.Diagnosis.Factors) sb.AppendLine(" - " + f);
            sb.AppendLine("Actions recommandées :");
            int n = 1;
            foreach (var r in inc.Diagnosis.Recommendations) sb.AppendLine($" {n++}. {r}");
            if (!string.IsNullOrEmpty(inc.Context)) sb.AppendLine("Contexte : " + inc.Context);
            sb.AppendLine("Événements :");
            foreach (var e in inc.EventList().OrderBy(x => x.Time)) sb.AppendLine($"  {e.Time:HH:mm:ss.fff} [{Fr.Sev(e.Severity)}] [{Fr.Cat(e.Category)}] {e.Title}");
            return sb.ToString();
        }
    }
}
