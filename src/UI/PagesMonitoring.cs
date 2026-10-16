using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using SignalWatchdogs.Core;
using SignalWatchdogs.Monitors;

namespace SignalWatchdogs.UI
{
    // ======================================================================
    public sealed class JournalPage : PageBase
    {
        public override string Title => "Journal des événements";
        public override string Subtitle => "Tout ce que Signal Watchdogs observe, en temps réel (également écrit sur disque)";

        readonly DataGridView _grid;
        readonly TextBox _search, _detail;
        readonly ComboBox _cat, _sev;
        readonly CheckBox _triggers, _pause;
        readonly Label _count;
        readonly List<WatchEvent> _all = new List<WatchEvent>();
        List<WatchEvent> _view = new List<WatchEvent>();
        long _lastSeq;

        public JournalPage(WatchdogService svc, MainForm main) : base(svc, main)
        {
            var (bar, left, right) = SplitToolbar();
            _search = new TextBox { Width = 200 };
            Theme.StyleInput(_search);
            Theme.SetCue(_search, "Rechercher…");
            _search.TextChanged += (s, e) => Rebuild();
            _cat = new ComboBox { Width = 140 };
            Theme.StyleInput(_cat);
            _cat.Items.Add("Toutes catégories");
            foreach (EventCategory c in Enum.GetValues(typeof(EventCategory))) _cat.Items.Add(Fr.Cat(c));
            _cat.SelectedIndex = 0;
            _cat.SelectedIndexChanged += (s, e) => Rebuild();
            _sev = new ComboBox { Width = 130 };
            Theme.StyleInput(_sev);
            _sev.Items.AddRange(new object[] { "Toutes gravités", "≥ Notice", "≥ Alerte", "≥ Erreur", "Critique" });
            _sev.SelectedIndex = 0;
            _sev.SelectedIndexChanged += (s, e) => Rebuild();
            _triggers = new CheckBox { Text = "Anomalies uniquement", ForeColor = Theme.Text, AutoSize = true, Padding = new Padding(8, 5, 0, 0) };
            _triggers.CheckedChanged += (s, e) => Rebuild();
            _pause = new CheckBox { Text = "Pause", ForeColor = Theme.Text, AutoSize = true, Padding = new Padding(8, 5, 0, 0) };
            var open = Theme.MakeButton("Dossier des journaux");
            open.Click += (s, e) => MainForm.OpenFolder(Svc.Log.MachineDirectory);
            var csv = Theme.MakeButton("Exporter (CSV)");
            csv.Click += (s, e) => ExportCsv();
            _count = Theme.MakeLabel("", Theme.Small, Theme.Muted);
            _count.Padding = new Padding(8, 8, 0, 0);
            left.Controls.AddRange(new Control[] { _search, _cat, _sev, _triggers, _pause, _count });
            right.Controls.AddRange(new Control[] { csv, open });

            var card = new Card("Événements");
            _grid = Theme.Grid();
            _grid.VirtualMode = true;
            _grid.Columns.Add(Theme.Col("Heure", 16, 100));
            _grid.Columns.Add(Theme.Col("Gravité", 12, 80));
            _grid.Columns.Add(Theme.Col("Catégorie", 12, 80));
            _grid.Columns.Add(Theme.Col("Événement", 55, 150));
            _grid.Columns.Add(Theme.Col("Détails", 70, 100));
            _grid.Columns.Add(Theme.Col("Source", 22, 80));
            _grid.CellValueNeeded += (s, e) =>
            {
                if (e.RowIndex >= _view.Count) return;
                var ev = _view[e.RowIndex];
                e.Value = e.ColumnIndex switch
                {
                    0 => ev.Time.ToString(ev.Time.Date == DateTime.Today ? "HH:mm:ss.fff" : "dd/MM HH:mm:ss"),
                    1 => Fr.Sev(ev.Severity),
                    2 => Fr.Cat(ev.Category),
                    3 => (ev.Trigger ? "⚑ " : "") + ev.Title,
                    4 => (ev.Details ?? "").Replace("\r", " ").Replace("\n", " "),
                    _ => ev.Source
                };
            };
            _grid.CellPainting += (s, e) =>
            {
                if (e.RowIndex < 0 || e.RowIndex >= _view.Count || (e.ColumnIndex != 1 && e.ColumnIndex != 2)) return;
                var ev = _view[e.RowIndex];
                e.PaintBackground(e.CellBounds, (e.State & DataGridViewElementStates.Selected) != 0);
                var color = e.ColumnIndex == 1 ? Theme.SevColor(ev.Severity) : Theme.CatColor(ev.Category);
                var r = e.CellBounds;
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(color))
                {
                    if (e.ColumnIndex == 1) e.Graphics.FillEllipse(b, r.Left + 8, r.Top + r.Height / 2 - 5, 10, 10);
                    else e.Graphics.FillRectangle(b, r.Left + 8, r.Top + 6, 3, r.Height - 12);
                }
                TextRenderer.DrawText(e.Graphics, Convert.ToString(e.FormattedValue), Theme.Base, new Rectangle(r.Left + 22, r.Top, r.Width - 24, r.Height), Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                e.Handled = true;
            };
            _grid.CellFormatting += (s, e) =>
            {
                if (e.RowIndex < _view.Count && _view[e.RowIndex].Trigger && e.ColumnIndex == 3) e.CellStyle.Font = Theme.Bold;
            };
            _grid.SelectionChanged += (s, e) => ShowSelected();
            card.Controls.Add(_grid);

            var cDetail = new Card("Détail") { Dock = DockStyle.Bottom, Height = 150 };
            _detail = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, BackColor = Theme.Surface, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, Font = Theme.Mono, ScrollBars = ScrollBars.Vertical };
            Theme.DarkScroll(_detail);
            cDetail.Controls.Add(_detail);

            Controls.Add(card);
            Controls.Add(cDetail);
            Controls.Add(bar);
        }

        bool Match(WatchEvent e)
        {
            if (_triggers.Checked && !e.Trigger) return false;
            if (_cat.SelectedIndex > 0 && Fr.Cat(e.Category) != (string)_cat.SelectedItem) return false;
            var minSev = _sev.SelectedIndex switch { 1 => Severity.Notice, 2 => Severity.Warning, 3 => Severity.Error, 4 => Severity.Critical, _ => Severity.Info };
            if (e.Severity < minSev) return false;
            var q = _search.Text.Trim();
            if (q.Length > 0)
            {
                var hay = e.Title + " " + e.Details + " " + e.Source + " " + e.Code;
                if (hay.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) return false;
            }
            return true;
        }

        void Rebuild()
        {
            var sel = SelectedEvent();
            _view = _all.Where(Match).Reverse().ToList();
            _grid.RowCount = _view.Count;
            _grid.Invalidate();
            Reselect(sel?.Seq ?? -1);
            _count.Text = $"{_view.Count} / {_all.Count} événements";
        }

        WatchEvent SelectedEvent() => _grid.SelectedRows.Count > 0 && _grid.SelectedRows[0].Index < _view.Count ? _view[_grid.SelectedRows[0].Index] : null;

        void Reselect(long seq)
        {
            if (seq < 0) return;
            int idx = _view.FindIndex(e => e.Seq == seq);
            if (idx < 0) return;
            _grid.ClearSelection();
            _grid.Rows[idx].Selected = true;
            try { if (idx < _grid.FirstDisplayedScrollingRowIndex || idx > _grid.FirstDisplayedScrollingRowIndex + _grid.DisplayedRowCount(false) - 1) _grid.FirstDisplayedScrollingRowIndex = Math.Max(0, idx - 3); } catch { }
        }

        void ShowSelected()
        {
            var ev = SelectedEvent();
            if (ev == null) { _detail.Text = ""; return; }
            _detail.Text = FormatEvent(ev);
        }

        public static string FormatEvent(WatchEvent ev)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{ev.Time:dd/MM/yyyy HH:mm:ss.fff}  [{Fr.Sev(ev.Severity)}]  [{Fr.Cat(ev.Category)}]  {ev.Code}{(ev.Trigger ? "  ⚑ déclencheur" : "")}{(ev.IncidentId > 0 ? $"  → incident #{ev.IncidentId}" : "")}");
            sb.AppendLine(ev.Title);
            if (!string.IsNullOrEmpty(ev.Details)) sb.AppendLine(ev.Details.Replace("\n", "\r\n"));
            sb.AppendLine("Source : " + ev.Source);
            if (ev.Data != null) foreach (var kv in ev.Data) sb.AppendLine($"  {kv.Key} = {kv.Value}");
            return sb.ToString();
        }

        public override void Select(object arg)
        {
            if (arg is long seq)
            {
                _search.Text = "";
                _cat.SelectedIndex = 0;
                _sev.SelectedIndex = 0;
                _triggers.Checked = false;
                RefreshData();
                Rebuild();
                Reselect(seq);
            }
        }

        public override void RefreshData()
        {
            if (_pause.Checked) return;
            var fresh = Hub.Since(_lastSeq);
            if (fresh.Count == 0) return;
            _lastSeq = fresh[fresh.Count - 1].Seq;
            _all.AddRange(fresh);
            if (_all.Count > 26000) _all.RemoveRange(0, _all.Count - 25000);
            var sel = SelectedEvent();
            var add = fresh.Where(Match).Reverse().ToList();
            if (add.Count == 0 && _view.Count <= _all.Count) { _count.Text = $"{_view.Count} / {_all.Count} événements"; return; }
            int firstVisible = _grid.FirstDisplayedScrollingRowIndex;
            _view.InsertRange(0, add);
            _grid.RowCount = _view.Count;
            if (firstVisible > 0) try { _grid.FirstDisplayedScrollingRowIndex = firstVisible + add.Count; } catch { }
            _grid.Invalidate();
            if (sel != null) Reselect(sel.Seq);
            _count.Text = $"{_view.Count} / {_all.Count} événements";
        }

        void ExportCsv()
        {
            using var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"SignalWatchdogs_{Environment.MachineName}_{DateTime.Now:yyyyMMdd_HHmm}.csv" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            var sb = new StringBuilder("Heure;Gravité;Catégorie;Code;Événement;Détails;Source;Déclencheur;Incident\r\n");
            foreach (var e in _view.AsEnumerable().Reverse())
                sb.Append($"{e.Time:yyyy-MM-dd HH:mm:ss.fff};{Fr.Sev(e.Severity)};{Fr.Cat(e.Category)};{e.Code};{Csv(e.Title)};{Csv(e.Details)};{Csv(e.Source)};{(e.Trigger ? "oui" : "")};{(e.IncidentId > 0 ? e.IncidentId.ToString() : "")}\r\n");
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
        }

        static string Csv(string s)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ");
            // Anti-injection de formules Excel (=, +, -, @) : un nom de périphérique USB ou un message
            // de journal peut être contrôlé par un tiers.
            if (s.Length > 0 && "=+-@\t".IndexOf(s[0]) >= 0) s = "'" + s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }

    // ======================================================================
    public sealed class ScreensPage : PageBase
    {
        public override string Title => "Écrans";
        public override string Subtitle => "Topologie interrogée en continu : chaque perte de signal est mesurée à la milliseconde";

        readonly MonitorLayoutControl _layout;
        readonly DataGridView _details, _stats, _events;
        string _detSig, _statSig;
        long _lastSeq;

        public ScreensPage(WatchdogService svc, MainForm main) : base(svc, main)
        {
            var root = Table(1, 2);
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
            var top = Table(2, 1);
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
            var bottom = Table(2, 1);
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            root.Controls.Add(top, 0, 0);
            root.Controls.Add(bottom, 0, 1);

            var cLayout = new Card("Disposition");
            _layout = new MonitorLayoutControl { Monitors = () => Svc.Topology?.Current, Lost = () => Svc.Topology?.LostNow, Stats = () => Svc.Topology?.Stats() };
            cLayout.Controls.Add(_layout);

            var cDetails = new Card("Écrans connectés");
            _details = Theme.Grid();
            foreach (var (h, w) in new[] { ("Écran", 18f), ("Modèle", 22f), ("N° de série", 16f), ("Connectique", 18f), ("Mode", 22f), ("Position", 14f), ("Carte graphique", 28f) })
                _details.Columns.Add(Theme.Col(h, w, 60));
            cDetails.Controls.Add(_details);

            var cStats = new Card("Fiabilité par écran (depuis le lancement)");
            _stats = Theme.Grid();
            foreach (var (h, w) in new[] { ("État", 13f), ("Écran", 30f), ("Connectique", 16f), ("Pertes", 9f), ("Noir cumulé", 13f), ("Chgts mode", 11f), ("Dernière perte", 17f) })
                _stats.Columns.Add(Theme.Col(h, w, 50));
            PaintSeverityCell(_stats, 0);
            cStats.Controls.Add(_stats);

            var cEvents = new Card("Événements d'affichage");
            _events = Theme.Grid();
            _events.Columns.Add(Theme.Col("Heure", 18, 80));
            _events.Columns.Add(Theme.Col("Gravité", 12, 70));
            _events.Columns.Add(Theme.Col("Événement", 50, 120));
            _events.Columns.Add(Theme.Col("Détails", 50, 80));
            PaintSeverityCell(_events, 1);
            _events.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0 && _events.Rows[e.RowIndex].Tag is WatchEvent ev) MainForm.ShowEventDetails(this, ev); };
            cEvents.Controls.Add(_events);

            top.Controls.Add(cLayout, 0, 0);
            top.Controls.Add(cDetails, 1, 0);
            bottom.Controls.Add(cStats, 0, 0);
            bottom.Controls.Add(cEvents, 1, 0);
            Controls.Add(root);
        }

        public override void RefreshData()
        {
            _layout.Invalidate();
            var mons = Svc.Topology?.Current ?? new List<MonitorInfo>();
            if (Changed(ref _detSig, string.Join("|", mons.Select(m => m.Key + m.Signature))))
            {
                _details.Rows.Clear();
                foreach (var m in mons)
                    _details.Rows.Add((m.Primary ? "★ " : "") + m.ShortGdi, m.Name + (m.IsVirtual ? " (virtuel)" : ""), (m.EdidMfg + " " + m.Serial).Trim(),
                        m.Connector, m.ModeText, $"{m.X}, {m.Y}", MonitorLayoutControl.ShortGpu(m.AdapterName));
            }
            var stats = Svc.Topology?.Stats() ?? new List<MonitorStats>();
            if (Changed(ref _statSig, string.Join("|", stats.Select(s => $"{s.Key}{s.Disconnects}{s.ModeChanges}{s.CurrentlyLost}{(int)s.Downtime.TotalMilliseconds}"))))
            {
                _stats.Rows.Clear();
                foreach (var s in stats.OrderByDescending(s => s.Disconnects).ThenBy(s => s.Label))
                {
                    int r = _stats.Rows.Add(s.CurrentlyLost ? "PERDU" : s.Disconnects > 0 ? "Instable" : "OK", s.Label, s.Connector, s.Disconnects, Fr.Duration(s.Downtime), s.ModeChanges,
                        s.LastLost?.ToString("dd/MM HH:mm:ss") ?? "—");
                    _stats.Rows[r].Cells[0].Tag = s.CurrentlyLost ? Theme.Err : s.Disconnects > 0 ? Theme.Warn : Theme.Ok;
                }
            }
            var fresh = Hub.Since(_lastSeq);
            if (fresh.Count > 0)
            {
                _lastSeq = fresh[fresh.Count - 1].Seq;
                foreach (var ev in fresh.Where(e => e.Category == EventCategory.Display))
                {
                    _events.Rows.Insert(0, ev.Time.ToString("dd/MM HH:mm:ss.fff"), Fr.Sev(ev.Severity), (ev.Trigger ? "⚑ " : "") + ev.Title, (ev.Details ?? "").Replace("\n", " "));
                    _events.Rows[0].Cells[1].Tag = Theme.SevColor(ev.Severity);
                    _events.Rows[0].Tag = ev;
                }
                while (_events.Rows.Count > 400) _events.Rows.RemoveAt(_events.Rows.Count - 1);
            }
        }
    }

    // ======================================================================
    public sealed class GpuCard : PaintedControl
    {
        public GpuAdapterState Adapter;
        public Func<int> MonitorCount;

        public GpuCard() { BackColor = Theme.Bg; Margin = new Padding(6); Size = new Size(380, 178); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Hq(g);
            g.Clear(Theme.Bg);
            var a = Adapter;
            if (a == null) return;
            bool err = a.ConfigError != 0 && a.ConfigError != 22;
            using (var p = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 10))
            {
                using (var b = new SolidBrush(Theme.Surface)) g.FillPath(b, p);
                using (var pen = new Pen(err ? Theme.Err : Theme.Border, err ? 2 : 1)) g.DrawPath(pen, p);
            }
            var vc = a.Vendor == "NVIDIA" ? Color.FromArgb(0x76, 0xB9, 0x00) : a.Vendor == "AMD" ? Color.FromArgb(0xED, 0x1C, 0x24) : a.Vendor == "Intel" ? Color.FromArgb(0x00, 0x71, 0xC5) : Theme.Faint;
            var badge = new RectangleF(14, 14, TextRenderer.MeasureText(a.Vendor ?? "?", Theme.SmallBold).Width + 12, 20);
            using (var p = Theme.RoundRect(badge, 6)) using (var b = new SolidBrush(vc)) g.FillPath(b, p);
            TextRenderer.DrawText(g, a.Vendor, Theme.SmallBold, Rectangle.Round(badge), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, a.Name, Theme.H2, new Rectangle((int)badge.Right + 8, 14, Width - (int)badge.Right - 20, 22), Theme.Text, TextFormatFlags.EndEllipsis);

            int y = 44;
            void Line(string label, string value, Color? c = null)
            {
                TextRenderer.DrawText(g, label, Theme.Small, new Rectangle(14, y, 92, 18), Theme.Muted);
                TextRenderer.DrawText(g, value, Theme.Small, new Rectangle(106, y, Width - 118, 18), c ?? Theme.Text, TextFormatFlags.EndEllipsis);
                y += 19;
            }
            int age = a.DriverAgeDays;
            Line("Pilote", $"{a.DriverVersion}  ·  {a.DriverDate:dd/MM/yyyy}" + (age >= 0 ? $"  ({age} j)" : ""), age > 540 ? Theme.Warn : (Color?)null);
            if (a.IsVirtual) Line("Type", "Adaptateur d'affichage virtuel", Theme.Warn);
            else
            {
                Line("Charge", $"{a.Util:0} %   ·   3D {a.Util3D:0} %   ·   copie {a.UtilCopy:0} %");
                Line("Vidéo", $"encodeur {a.UtilEncode:0} %   ·   décodeur {a.UtilDecode:0} %", a.UtilEncode > 3 ? Theme.Teal : (Color?)null);
                Line("Mémoire", a.VramTotal > 0 ? $"{Fr.Bytes(a.VramUsed)} / {Fr.Bytes(a.VramTotal)} dédiée  ·  {Fr.Bytes(a.SharedUsed)} partagée" : $"{Fr.Bytes(a.SharedUsed)} partagée");
                if (a.Nv != null)
                    Line("Télémétrie", $"{a.Nv.Temp:0} °C  ·  {a.Nv.Power:0} W  ·  PCIe Gen{a.Nv.PcieGen:0} x{a.Nv.PcieWidth:0}/{a.Nv.PcieWidthMax:0}  ·  {a.Nv.PState}  ·  ventil. {a.Nv.Fan:0} %",
                        a.Nv.Temp >= 85 ? Theme.Err : (Color?)null);
            }
            Line("État", err ? Native.CfgMgr.ProblemText((uint)a.ConfigError) : (a.Status ?? "OK") + (MonitorCount != null ? $"  ·  {MonitorCount()} écran(s) branché(s)" : ""), err ? Theme.Err : Theme.Ok);
        }
    }

    public sealed class GpuPage : PageBase
    {
        public override string Title => "Cartes graphiques";
        public override string Subtitle => "Charge par moteur, mémoire vidéo, encodeur (Teams / Parsec / streaming), télémétrie NVIDIA";

        readonly FlowLayoutPanel _cards;
        readonly LineChart _util, _vram, _enc, _temp;
        readonly Card _cTemp;
        string _sig;

        public GpuPage(WatchdogService svc, MainForm main) : base(svc, main)
        {
            _cards = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 192, BackColor = Theme.Bg, AutoScroll = false, WrapContents = false };
            _cards.Resize += (s, e) => LayoutCards();
            var charts = Table(2, 2);
            charts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            charts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            charts.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            charts.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            var c1 = new Card("Charge GPU (10 min)"); _util = new LineChart(); c1.Controls.Add(_util);
            var c2 = new Card("Mémoire vidéo dédiée utilisée"); _vram = new LineChart(); c2.Controls.Add(_vram);
            var c3 = new Card("Encodeur vidéo (partage d'écran / streaming)"); _enc = new LineChart(); c3.Controls.Add(_enc);
            _cTemp = new Card("Température GPU (NVIDIA)"); _temp = new LineChart { FixedMax = 100, Unit = "°C", EmptyText = "Aucune télémétrie de température (nvidia-smi absent)" }; _cTemp.Controls.Add(_temp);
            charts.Controls.Add(c1, 0, 0);
            charts.Controls.Add(c2, 1, 0);
            charts.Controls.Add(c3, 0, 1);
            charts.Controls.Add(_cTemp, 1, 1);
            Controls.Add(charts);
            Controls.Add(_cards);
        }

        void LayoutCards()
        {
            var cards = _cards.Controls.Cast<Control>().ToList();
            if (cards.Count == 0) return;
            // Les adaptateurs virtuels prennent moins de place que les vraies cartes.
            float units = cards.Sum(c => ((GpuCard)c).Adapter.IsVirtual ? 0.7f : 1f);
            float avail = _cards.ClientSize.Width - cards.Count * 12 - 4;
            foreach (GpuCard c in cards)
                c.Width = Math.Max(240, (int)(avail * (c.Adapter.IsVirtual ? 0.7f : 1f) / units));
        }

        public override void RefreshData()
        {
            var adapters = Svc.Gpu?.Adapters ?? new List<GpuAdapterState>();
            if (Changed(ref _sig, string.Join("|", adapters.Select(a => a.Key))))
            {
                _cards.Controls.Clear();
                foreach (var a in adapters)
                {
                    var ad = a;
                    _cards.Controls.Add(new GpuCard
                    {
                        Adapter = a,
                        Height = a.IsVirtual ? 110 : 178,
                        MonitorCount = () => (Svc.Topology?.Current ?? new List<MonitorInfo>()).Count(m => m.AdapterName == ad.Name)
                    });
                }
                LayoutCards();
                var real = adapters.Where(a => a.HasLuid).ToList();
                _util.SeriesList.Clear(); _vram.SeriesList.Clear(); _enc.SeriesList.Clear(); _temp.SeriesList.Clear();
                int i = 0;
                foreach (var a in real)
                {
                    var ad = a;
                    var col = Theme.SeriesColors[i++ % Theme.SeriesColors.Length];
                    var name = MonitorLayoutControl.ShortGpu(a.Name);
                    _util.SeriesList.Add(new LineChart.Series { Name = name, Color = col, Data = () => ad.UtilHistory.Snapshot(TimeSpan.FromMinutes(10)) });
                    _vram.SeriesList.Add(new LineChart.Series { Name = name, Color = col, Data = () => ad.VramHistory.Snapshot(TimeSpan.FromMinutes(10)) });
                    _enc.SeriesList.Add(new LineChart.Series { Name = name, Color = col, Data = () => ad.EncodeHistory.Snapshot(TimeSpan.FromMinutes(10)) });
                    if (a.Vendor == "NVIDIA") _temp.SeriesList.Add(new LineChart.Series { Name = name, Color = col, Data = () => ad.TempHistory.Snapshot(TimeSpan.FromMinutes(10)) });
                }
                _util.SeriesList.Add(new LineChart.Series { Name = "CPU", Color = Theme.Muted, Data = () => Svc.Gpu.Cpu.Snapshot(TimeSpan.FromMinutes(10)) });
                _vram.SeriesList.Add(new LineChart.Series { Name = "RAM système", Color = Theme.Muted, Data = () => Svc.Gpu.Ram.Snapshot(TimeSpan.FromMinutes(10)) });
            }
            foreach (Control c in _cards.Controls) c.Invalidate();
            _util.Invalidate(); _vram.Invalidate(); _enc.Invalidate(); _temp.Invalidate();
        }
    }
}
