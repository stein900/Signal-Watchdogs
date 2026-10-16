using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using SignalWatchdogs.Core;
using SignalWatchdogs.Monitors;

namespace SignalWatchdogs.UI
{
    public abstract class PaintedControl : Control
    {
        protected PaintedControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
            Font = Theme.Base;
        }

        protected static void Hq(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        }
    }

    // ------------------------------------------------------------------
    public sealed class NavButton : PaintedControl
    {
        public string Glyph = "";
        public string Badge;
        public Color BadgeColor = Theme.Err;
        bool _selected, _hover;

        public bool Selected { get => _selected; set { _selected = value; Invalidate(); } }

        public NavButton()
        {
            Height = 40;
            Cursor = Cursors.Hand;
            BackColor = Theme.Sidebar;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Hq(g);
            g.Clear(Theme.Sidebar);
            var r = new RectangleF(8, 3, Width - 16, Height - 6);
            if (_selected || _hover)
                using (var p = Theme.RoundRect(r, 6))
                using (var b = new SolidBrush(_selected ? Theme.Surface2 : Theme.Surface))
                    g.FillPath(b, p);
            if (_selected)
                using (var b = new SolidBrush(Theme.Accent))
                    g.FillRectangle(b, 8, Height / 2f - 9, 3, 18);
            TextRenderer.DrawText(g, Glyph, Theme.Icons, new Rectangle(18, 0, 28, Height), _selected ? Theme.Accent : Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(g, Text, _selected ? Theme.Bold : Theme.Base, new Rectangle(52, 0, Width - 100, Height), _selected ? Theme.Text : Theme.Blend(Theme.Muted, Theme.Text, 0.35), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (!string.IsNullOrEmpty(Badge))
            {
                var sz = TextRenderer.MeasureText(Badge, Theme.SmallBold);
                var br = new RectangleF(Width - 18 - sz.Width - 6, Height / 2f - 9, sz.Width + 6, 18);
                using (var p = Theme.RoundRect(br, 9))
                using (var b = new SolidBrush(BadgeColor))
                    g.FillPath(b, p);
                TextRenderer.DrawText(g, Badge, Theme.SmallBold, Rectangle.Round(br), Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            }
        }
    }

    // ------------------------------------------------------------------
    public class Card : Panel
    {
        string _title = "", _subtitle = "";
        public string Title { get => _title; set { _title = value; Invalidate(); } }
        public string Subtitle { get => _subtitle; set { if (_subtitle != value) { _subtitle = value; Invalidate(new Rectangle(Width / 2, 0, Width / 2, 38)); } } }

        public Card(string title = "")
        {
            _title = title;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            Padding = new Padding(14, 40, 14, 12);
            Margin = new Padding(6);
            Dock = DockStyle.Fill;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Bg);
            using (var p = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 10))
            {
                using (var b = new SolidBrush(Theme.Surface)) g.FillPath(b, p);
                using (var pen = new Pen(Theme.Border)) g.DrawPath(pen, p);
            }
            TextRenderer.DrawText(g, _title, Theme.H2, new Point(14, 11), Theme.Text, TextFormatFlags.NoPrefix);
            if (!string.IsNullOrEmpty(_subtitle))
                TextRenderer.DrawText(g, _subtitle, Theme.Small, new Rectangle(Width / 2, 12, Width / 2 - 14, 20), Theme.Muted, TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
        }
    }

    // ------------------------------------------------------------------
    public sealed class KpiTile : PaintedControl
    {
        string _title = "", _value = "", _sub = "";
        Color _accent = Theme.Accent;
        public string Glyph = "";

        public KpiTile(string title, string glyph, Color accent)
        {
            _title = title;
            Glyph = glyph;
            _accent = accent;
            BackColor = Theme.Bg;
            Margin = new Padding(6);
            Dock = DockStyle.Fill;
            MinimumSize = new Size(150, 92);
        }

        public void Set(string value, string sub, Color? accent = null)
        {
            var a = accent ?? _accent;
            if (value == _value && sub == _sub && a == _accent) return;
            _value = value; _sub = sub; _accent = a;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Hq(g);
            g.Clear(Theme.Bg);
            using (var p = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 10))
            {
                using (var b = new SolidBrush(Theme.Surface)) g.FillPath(b, p);
                using (var pen = new Pen(Theme.Border)) g.DrawPath(pen, p);
            }
            using (var b = new SolidBrush(_accent)) g.FillRectangle(b, 0, 14, 3, Height - 28);
            var circle = new RectangleF(Width - 36, 10, 26, 26);
            using (var b = new SolidBrush(Color.FromArgb(40, _accent))) g.FillEllipse(b, circle);
            TextRenderer.DrawText(g, Glyph, Theme.IconsSmall, Rectangle.Round(circle), _accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, _title, Theme.Small, new Rectangle(14, 12, Width - 52, 18), Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, _value, Theme.Kpi, new Rectangle(12, 28, Width - 20, 40), Theme.Text, TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, _sub, Theme.Small, new Rectangle(14, Height - 26, Width - 22, 18), Theme.Muted, TextFormatFlags.EndEllipsis);
        }
    }

    // ------------------------------------------------------------------
    public sealed class StatusPill : PaintedControl
    {
        Color _color = Theme.Ok;
        public StatusPill() { BackColor = Theme.Bg; Height = 30; }

        public void Set(string text, Color color)
        {
            if (Text == text && _color == color) return;
            Text = text; _color = color;
            Width = TextRenderer.MeasureText(text, Theme.Bold).Width + 40;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Hq(g);
            g.Clear(Parent?.BackColor ?? Theme.Bg);
            using (var p = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Height / 2f - 1))
            {
                using (var b = new SolidBrush(Color.FromArgb(36, _color))) g.FillPath(b, p);
                using (var pen = new Pen(Color.FromArgb(110, _color))) g.DrawPath(pen, p);
            }
            using (var b = new SolidBrush(_color)) g.FillEllipse(b, 12, Height / 2f - 4, 8, 8);
            TextRenderer.DrawText(g, Text, Theme.Bold, new Rectangle(26, 0, Width - 30, Height), Theme.Text, TextFormatFlags.VerticalCenter);
        }
    }

    // ------------------------------------------------------------------
    public sealed class LineChart : PaintedControl
    {
        public sealed class Series
        {
            public string Name;
            public Color Color;
            public Func<List<(DateTime t, double v)>> Data;
            public string Format = "0";
        }

        public readonly List<Series> SeriesList = new List<Series>();
        public TimeSpan Window = TimeSpan.FromMinutes(10);
        public double? FixedMax = 100;
        public string Unit = "%";
        public string EmptyText = "En attente de données…";

        public LineChart() { Dock = DockStyle.Fill; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Hq(g);
            g.Clear(Theme.Surface);
            var plot = new RectangleF(40, 28, Width - 52, Height - 50);
            if (plot.Width < 40 || plot.Height < 30) return;

            var data = SeriesList.Select(s => (s, pts: s.Data?.Invoke() ?? new List<(DateTime, double)>())).ToList();
            double max = FixedMax ?? Math.Max(1, NiceCeiling(data.SelectMany(d => d.pts).Select(p => p.v).DefaultIfEmpty(0).Max() * 1.15));

            using (var gridPen = new Pen(Theme.Border) { DashStyle = DashStyle.Dot })
            {
                for (int i = 0; i <= 4; i++)
                {
                    float y = plot.Bottom - plot.Height * i / 4f;
                    g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                    var lbl = (max * i / 4).ToString(max >= 10 ? "0" : "0.#", Fr.Culture) + (i == 4 ? " " + Unit : "");
                    TextRenderer.DrawText(g, lbl, Theme.Small, new Rectangle(0, (int)y - 8, 38, 16), Theme.Muted, TextFormatFlags.Right);
                }
            }
            var now = DateTime.Now;
            var start = now - Window;
            for (int i = 0; i <= 2; i++)
            {
                float x = plot.Left + plot.Width * i / 2f;
                var minutes = Window.TotalMinutes * (2 - i) / 2;
                var txt = i == 2 ? "maintenant" : minutes >= 60 ? $"-{minutes / 60:0.#} h" : $"-{minutes:0} min";
                var flags = i == 0 ? TextFormatFlags.Left : i == 2 ? TextFormatFlags.Right : TextFormatFlags.HorizontalCenter;
                var rx = i == 0 ? (int)x : i == 2 ? (int)x - 100 : (int)x - 50;
                TextRenderer.DrawText(g, txt, Theme.Small, new Rectangle(rx, (int)plot.Bottom + 4, 100, 16), Theme.Muted, flags);
            }

            bool any = false;
            foreach (var (s, pts) in data)
            {
                if (pts.Count < 2) continue;
                any = true;
                var points = pts.Select(p => new PointF(
                    plot.Left + (float)((p.t - start).TotalMilliseconds / Window.TotalMilliseconds) * plot.Width,
                    plot.Bottom - (float)(Math.Min(max, Math.Max(0, p.v)) / max) * plot.Height)).Where(p => p.X >= plot.Left - 1).ToArray();
                if (points.Length < 2) continue;
                using (var path = new GraphicsPath())
                {
                    path.AddLines(points);
                    path.AddLine(points[points.Length - 1], new PointF(points[points.Length - 1].X, plot.Bottom));
                    path.AddLine(new PointF(points[points.Length - 1].X, plot.Bottom), new PointF(points[0].X, plot.Bottom));
                    path.CloseFigure();
                    using var fill = new LinearGradientBrush(new PointF(0, plot.Top), new PointF(0, plot.Bottom), Color.FromArgb(70, s.Color), Color.FromArgb(4, s.Color));
                    g.FillPath(fill, path);
                }
                using var pen = new Pen(s.Color, 1.8f) { LineJoin = LineJoin.Round };
                g.DrawLines(pen, points);
            }
            if (!any) TextRenderer.DrawText(g, EmptyText, Theme.Small, Rectangle.Round(plot), Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            int lx = (int)plot.Left;
            foreach (var (s, pts) in data)
            {
                var last = pts.Count > 0 ? pts[pts.Count - 1].v : double.NaN;
                var txt = s.Name + (double.IsNaN(last) ? "" : "  " + last.ToString(s.Format, Fr.Culture) + " " + Unit);
                using (var b = new SolidBrush(s.Color)) g.FillRectangle(b, lx, 9, 10, 10);
                TextRenderer.DrawText(g, txt, Theme.Small, new Point(lx + 14, 6), Theme.Text);
                lx += TextRenderer.MeasureText(txt, Theme.Small).Width + 30;
            }
        }

        static double NiceCeiling(double v)
        {
            if (v <= 0) return 1;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
            foreach (var m in new[] { 1, 2, 2.5, 5, 10 }) if (m * mag >= v) return m * mag;
            return 10 * mag;
        }
    }

    // ------------------------------------------------------------------
    public sealed class StackedBarChart : PaintedControl
    {
        public sealed class Stack { public string Name; public Color Color; public int[] Values; }
        public List<DateTime> Days = new List<DateTime>();
        public List<Stack> Stacks = new List<Stack>();
        readonly ToolTip _tip = new ToolTip();
        int _hoverIdx = -1;

        public StackedBarChart() { Dock = DockStyle.Fill; }

        RectangleF Plot => new RectangleF(34, 30, Width - 46, Height - 52);

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var p = Plot;
            int idx = Days.Count == 0 ? -1 : (int)((e.X - p.Left) / (p.Width / Days.Count));
            if (idx < 0 || idx >= Days.Count) idx = -1;
            if (idx == _hoverIdx) return;
            _hoverIdx = idx;
            Invalidate();
            if (idx >= 0)
                _tip.SetToolTip(this, Days[idx].ToString("dddd dd/MM", Fr.Culture) + "\n" + string.Join("\n", Stacks.Where(s => s.Values[idx] > 0).Select(s => $"{s.Name} : {s.Values[idx]}")));
            else _tip.SetToolTip(this, null);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Hq(g);
            g.Clear(Theme.Surface);
            var plot = Plot;
            if (plot.Width < 40 || plot.Height < 30 || Days.Count == 0) return;
            int max = Math.Max(1, Enumerable.Range(0, Days.Count).Select(i => Stacks.Sum(s => s.Values[i])).DefaultIfEmpty(0).Max());
            int step = max <= 5 ? 1 : max <= 10 ? 2 : (int)Math.Ceiling(max / 5.0);
            int top = ((max + step - 1) / step) * step;
            using (var gridPen = new Pen(Theme.Border) { DashStyle = DashStyle.Dot })
                for (int v = 0; v <= top; v += step)
                {
                    float y = plot.Bottom - plot.Height * v / top;
                    g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                    TextRenderer.DrawText(g, v.ToString(), Theme.Small, new Rectangle(0, (int)y - 8, 30, 16), Theme.Muted, TextFormatFlags.Right);
                }
            float slot = plot.Width / Days.Count;
            float bw = Math.Max(2, slot * 0.68f);
            int labelEvery = Days.Count > 45 ? 10 : Days.Count > 20 ? 5 : Days.Count > 10 ? 2 : 1;
            for (int i = 0; i < Days.Count; i++)
            {
                float x = plot.Left + i * slot + (slot - bw) / 2;
                float y = plot.Bottom;
                if (i == _hoverIdx) using (var hb = new SolidBrush(Theme.Surface2)) g.FillRectangle(hb, plot.Left + i * slot, plot.Top, slot, plot.Height);
                foreach (var s in Stacks)
                {
                    int v = s.Values[i];
                    if (v <= 0) continue;
                    float h = plot.Height * v / top;
                    using var b = new SolidBrush(s.Color);
                    g.FillRectangle(b, x, y - h, bw, h);
                    y -= h;
                }
                if (i % labelEvery == 0 || i == Days.Count - 1)
                    TextRenderer.DrawText(g, Days[i].ToString("dd/MM"), Theme.Small, new Rectangle((int)(plot.Left + i * slot + slot / 2 - 25), (int)plot.Bottom + 4, 50, 16), Theme.Muted, TextFormatFlags.HorizontalCenter);
            }
            int lx = (int)plot.Left;
            foreach (var s in Stacks)
            {
                using (var b = new SolidBrush(s.Color)) g.FillRectangle(b, lx, 9, 10, 10);
                var txt = $"{s.Name} ({s.Values.Sum()})";
                TextRenderer.DrawText(g, txt, Theme.Small, new Point(lx + 14, 6), Theme.Text);
                lx += TextRenderer.MeasureText(txt, Theme.Small).Width + 26;
            }
        }
    }

    // ------------------------------------------------------------------
    /// <summary>Frise chronologique : une ligne par famille d'événements + bandes d'incidents.</summary>
    public sealed class TimelineControl : PaintedControl
    {
        static readonly (string name, EventCategory[] cats)[] Rows =
        {
            ("Écrans", new[] { EventCategory.Display }),
            ("GPU", new[] { EventCategory.Gpu }),
            ("USB / USB-C", new[] { EventCategory.Usb, EventCategory.UsbC, EventCategory.Device }),
            ("Sessions", new[] { EventCategory.Session }),
            ("Système / alim.", new[] { EventCategory.System, EventCategory.Power }),
            ("Applications", new[] { EventCategory.Process }),
            ("Signalements", new[] { EventCategory.User }),
        };
        static readonly TimeSpan[] Windows = { TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(6), TimeSpan.FromHours(24) };

        public Func<List<WatchEvent>> EventSource;
        public Func<List<Incident>> Incidents;
        public TimeSpan Window = TimeSpan.FromHours(1);
        readonly ToolTip _tip = new ToolTip { InitialDelay = 100, ReshowDelay = 50 };
        List<(RectangleF r, WatchEvent e)> _hits = new List<(RectangleF, WatchEvent)>();
        List<(Rectangle r, TimeSpan w)> _toggles = new List<(Rectangle, TimeSpan)>();
        WatchEvent _hover;
        public event Action<WatchEvent> EventClicked;

        public TimelineControl() { Dock = DockStyle.Fill; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var hit = _hits.Where(h => h.r.Contains(e.Location)).Select(h => h.e).LastOrDefault();
            Cursor = hit != null || _toggles.Any(t => t.r.Contains(e.Location)) ? Cursors.Hand : Cursors.Default;
            if (hit == _hover) return;
            _hover = hit;
            _tip.SetToolTip(this, hit == null ? null : $"{hit.Time:dd/MM HH:mm:ss} · {Fr.Sev(hit.Severity)}\n{hit.Title}");
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            foreach (var t in _toggles)
                if (t.r.Contains(e.Location)) { Window = t.w; Invalidate(); return; }
            if (_hover != null) EventClicked?.Invoke(_hover);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Hq(g);
            g.Clear(Theme.Surface);
            const int labelW = 108;
            var plot = new RectangleF(labelW, 26, Width - labelW - 12, Height - 48);
            if (plot.Width < 50 || plot.Height < 40) return;

            // Sélecteur de fenêtre
            _toggles.Clear();
            int tx = Width - 12;
            foreach (var w in Windows.Reverse())
            {
                var txt = w.TotalHours >= 1 ? $"{w.TotalHours:0} h" : $"{w.TotalMinutes:0} min";
                var sz = TextRenderer.MeasureText(txt, Theme.SmallBold);
                var r = new Rectangle(tx - sz.Width - 12, 2, sz.Width + 12, 20);
                tx = r.Left - 4;
                bool sel = w == Window;
                using (var p = Theme.RoundRect(r, 9)) using (var b = new SolidBrush(sel ? Theme.Accent : Theme.Surface2)) g.FillPath(b, p);
                TextRenderer.DrawText(g, txt, Theme.SmallBold, r, sel ? Color.White : Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                _toggles.Add((r, w));
            }

            var now = DateTime.Now;
            var start = now - Window;
            float rowH = plot.Height / Rows.Length;
            float X(DateTime t) => plot.Left + (float)((t - start).TotalMilliseconds / Window.TotalMilliseconds) * plot.Width;

            for (int i = 0; i < Rows.Length; i++)
            {
                float y = plot.Top + i * rowH;
                if (i % 2 == 0) using (var b = new SolidBrush(Color.FromArgb(0x19, 0x1F, 0x27))) g.FillRectangle(b, plot.Left, y, plot.Width, rowH);
                TextRenderer.DrawText(g, Rows[i].name, Theme.Small, new Rectangle(8, (int)y, labelW - 12, (int)rowH), Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }

            // Graduations
            var tick = Window.TotalHours >= 24 ? TimeSpan.FromHours(3) : Window.TotalHours >= 6 ? TimeSpan.FromHours(1) : Window.TotalHours >= 1 ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(2);
            var first = new DateTime((start.Ticks / tick.Ticks + 1) * tick.Ticks);
            using (var pen = new Pen(Theme.Border) { DashStyle = DashStyle.Dot })
                for (var t = first; t <= now; t += tick)
                {
                    float x = X(t);
                    g.DrawLine(pen, x, plot.Top, x, plot.Bottom);
                    TextRenderer.DrawText(g, t.ToString("HH:mm"), Theme.Small, new Rectangle((int)x - 25, (int)plot.Bottom + 4, 50, 16), Theme.Muted, TextFormatFlags.HorizontalCenter);
                }

            // Incidents
            foreach (var inc in Incidents?.Invoke() ?? new List<Incident>())
            {
                if (inc.LastTrigger < start) continue;
                float x1 = Math.Max(plot.Left, X(inc.Start)), x2 = Math.Min(plot.Right, Math.Max(X(inc.LastTrigger), X(inc.Start) + 4));
                var c = Theme.SevColor(inc.Severity);
                using (var b = new SolidBrush(Color.FromArgb(inc.Closed ? 45 : 80, c))) g.FillRectangle(b, x1, plot.Top, Math.Max(3, x2 - x1), plot.Height);
                using (var p = new Pen(Color.FromArgb(140, c))) g.DrawLine(p, x1, plot.Top, x1, plot.Bottom);
            }

            // Événements
            _hits = new List<(RectangleF, WatchEvent)>();
            var evs = EventSource?.Invoke() ?? new List<WatchEvent>();
            foreach (var ev in evs)
            {
                if (ev.Time < start || ev.Severity < Severity.Notice && ev.Category != EventCategory.Process && ev.Category != EventCategory.Session) continue;
                int row = Array.FindIndex(Rows, r => r.cats.Contains(ev.Category));
                if (row < 0) continue;
                float x = X(ev.Time);
                float y = plot.Top + row * rowH + rowH / 2;
                float rad = ev.Trigger ? 5f : ev.Severity >= Severity.Warning ? 4f : 3f;
                var rect = new RectangleF(x - rad, y - rad, rad * 2, rad * 2);
                var col = ev.Severity <= Severity.Notice ? Theme.CatColor(ev.Category) : Theme.SevColor(ev.Severity);
                using (var b = new SolidBrush(col)) g.FillEllipse(b, rect);
                if (ev.Trigger) using (var p = new Pen(Color.White, 1.2f)) g.DrawEllipse(p, rect);
                _hits.Add((RectangleF.Inflate(rect, 3, 3), ev));
            }
            using (var p = new Pen(Theme.Accent, 1.5f)) g.DrawLine(p, plot.Right, plot.Top, plot.Right, plot.Bottom);
        }
    }

    // ------------------------------------------------------------------
    /// <summary>Disposition réelle des écrans (comme dans Paramètres > Affichage) avec état de chaque écran.</summary>
    public sealed class MonitorLayoutControl : PaintedControl
    {
        public Func<List<MonitorInfo>> Monitors;
        public Func<List<(MonitorInfo info, DateTime at)>> Lost;
        public Func<List<MonitorStats>> Stats;

        public MonitorLayoutControl() { Dock = DockStyle.Fill; }

        public static string ShortGpu(string n) => (n ?? "").Replace("(R)", "").Replace("(TM)", "").Replace("GeForce ", "").Replace("Graphics ", "").Replace("  ", " ").Trim();

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Hq(g);
            g.Clear(Theme.Surface);
            var mons = Monitors?.Invoke() ?? new List<MonitorInfo>();
            var lost = Lost?.Invoke() ?? new List<(MonitorInfo, DateTime)>();
            var stats = (Stats?.Invoke() ?? new List<MonitorStats>()).ToDictionary(s => s.Key, s => s);
            var all = mons.Select(m => (m, lostAt: (DateTime?)null)).Concat(lost.Where(l => mons.All(m => m.Key != l.info.Key)).Select(l => (m: l.info, lostAt: (DateTime?)l.at))).ToList();
            if (all.Count == 0)
            {
                TextRenderer.DrawText(g, "Aucun écran détecté", Theme.Base, ClientRectangle, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            int minX = all.Min(a => a.m.X), minY = all.Min(a => a.m.Y);
            int maxX = all.Max(a => a.m.X + Math.Max(1, a.m.Width)), maxY = all.Max(a => a.m.Y + Math.Max(1, a.m.Height));
            float pad = 12;
            float scale = Math.Min((Width - pad * 2) / (float)(maxX - minX), (Height - pad * 2) / (float)(maxY - minY));
            float offX = pad + ((Width - pad * 2) - (maxX - minX) * scale) / 2;
            float offY = pad + ((Height - pad * 2) - (maxY - minY) * scale) / 2;

            foreach (var (m, lostAt) in all)
            {
                var r = new RectangleF(offX + (m.X - minX) * scale + 3, offY + (m.Y - minY) * scale + 3, m.Width * scale - 6, m.Height * scale - 6);
                stats.TryGetValue(m.Key, out var st);
                bool isLost = lostAt.HasValue;
                bool recent = st?.LastLost != null && (DateTime.Now - st.LastLost.Value).TotalMinutes < 10;
                var border = isLost ? Theme.Err : recent ? Theme.Warn : m.IsVirtual ? Theme.Faint : Theme.Accent;
                using (var p = Theme.RoundRect(r, 6))
                {
                    using (var b = new LinearGradientBrush(r, isLost ? Color.FromArgb(0x2A, 0x14, 0x18) : Theme.Surface3, isLost ? Color.FromArgb(0x1A, 0x10, 0x12) : Theme.Surface2, 90f))
                        g.FillPath(b, p);
                    using var pen = new Pen(border, isLost || recent ? 2.2f : 1.4f);
                    if (isLost || m.IsVirtual) pen.DashStyle = DashStyle.Dash;
                    g.DrawPath(pen, p);
                }
                var inner = Rectangle.Round(RectangleF.Inflate(r, -8, -6));
                var lines = new List<(string t, Font f, Color c)>
                {
                    ((m.Primary ? "★ " : "") + m.Name, Theme.Bold, Theme.Text),
                    ($"{m.ShortGdi} · {m.Connector}", Theme.Small, Theme.Muted),
                    (m.ModeText, Theme.Small, Theme.Muted),
                    (ShortGpu(m.AdapterName), Theme.Small, Theme.Purple),
                };
                if (isLost) lines.Insert(0, ($"PERDU depuis {Fr.Duration(DateTime.Now - lostAt.Value)}", Theme.SmallBold, Theme.Err));
                if (st != null && st.Disconnects > 0) lines.Add(($"{st.Disconnects} perte(s) · noir {Fr.Duration(st.Downtime)}", Theme.SmallBold, recent ? Theme.Warn : Theme.Muted));
                int y = inner.Top;
                foreach (var (t, f, c) in lines)
                {
                    int h = TextRenderer.MeasureText("Ag", f).Height;
                    if (y + h > inner.Bottom) break;
                    TextRenderer.DrawText(g, t, f, new Rectangle(inner.Left, y, inner.Width, h), c, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                    y += h + 1;
                }
            }
        }
    }
}
