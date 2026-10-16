using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Windows.Forms;
using SignalWatchdogs.Core;
using SignalWatchdogs.Diagnostics;
using SignalWatchdogs.Native;

namespace SignalWatchdogs.UI
{
    public static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(0x0D, 0x11, 0x17);
        public static readonly Color Sidebar = Color.FromArgb(0x10, 0x15, 0x1C);
        public static readonly Color Surface = Color.FromArgb(0x16, 0x1B, 0x22);
        public static readonly Color Surface2 = Color.FromArgb(0x1E, 0x24, 0x2E);
        public static readonly Color Surface3 = Color.FromArgb(0x26, 0x2D, 0x39);
        public static readonly Color Border = Color.FromArgb(0x2A, 0x31, 0x3C);
        public static readonly Color Text = Color.FromArgb(0xE6, 0xED, 0xF3);
        public static readonly Color Muted = Color.FromArgb(0x8B, 0x94, 0x9E);
        public static readonly Color Faint = Color.FromArgb(0x48, 0x50, 0x5C);
        public static readonly Color Accent = Color.FromArgb(0x4F, 0x8C, 0xFF);
        public static readonly Color Selection = Color.FromArgb(0x1F, 0x33, 0x52);
        public static readonly Color Ok = Color.FromArgb(0x3F, 0xB9, 0x50);
        public static readonly Color Warn = Color.FromArgb(0xD2, 0x99, 0x22);
        public static readonly Color Err = Color.FromArgb(0xF8, 0x51, 0x49);
        public static readonly Color Crit = Color.FromArgb(0xFF, 0x3B, 0x6B);
        public static readonly Color Info = Color.FromArgb(0x58, 0xA6, 0xFF);
        public static readonly Color Purple = Color.FromArgb(0xA3, 0x71, 0xF7);
        public static readonly Color Teal = Color.FromArgb(0x39, 0xC5, 0xCF);
        public static readonly Color Pink = Color.FromArgb(0xDB, 0x61, 0xA2);
        public static readonly Color Orange = Color.FromArgb(0xF0, 0x88, 0x3E);
        public static readonly Color Yellow = Color.FromArgb(0xE3, 0xB3, 0x41);

        public static readonly Color[] SeriesColors = { Accent, Purple, Teal, Orange, Pink, Yellow, Ok };

        public static readonly Font Base = new Font("Segoe UI", 9.5f);
        public static readonly Font Small = new Font("Segoe UI", 8.5f);
        public static readonly Font SmallBold = new Font("Segoe UI Semibold", 8.5f);
        public static readonly Font Bold = new Font("Segoe UI Semibold", 9.5f);
        public static readonly Font H2 = new Font("Segoe UI Semibold", 10.5f);
        public static readonly Font H1 = new Font("Segoe UI Semibold", 15f);
        public static readonly Font Kpi = new Font("Segoe UI Semibold", 22f);
        public static readonly Font Mono = new Font("Consolas", 9f);
        public static readonly Font Icons = CreateIconFont(13f);
        public static readonly Font IconsSmall = CreateIconFont(10f);

        static Font CreateIconFont(float size)
        {
            foreach (var name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
            {
                var f = new Font(name, size);
                if (f.Name == name) return f;
                f.Dispose();
            }
            return new Font("Segoe UI Symbol", size);
        }

        public static Color SevColor(Severity s) => s switch
        {
            Severity.Info => Muted,
            Severity.Notice => Info,
            Severity.Warning => Warn,
            Severity.Error => Err,
            Severity.Critical => Crit,
            _ => Muted
        };

        public static Color CatColor(EventCategory c) => c switch
        {
            EventCategory.Display => Accent,
            EventCategory.Gpu => Purple,
            EventCategory.Usb => Teal,
            EventCategory.UsbC => Color.FromArgb(0x2B, 0xA0, 0xA8),
            EventCategory.Device => Orange,
            EventCategory.Session => Pink,
            EventCategory.Process => Color.FromArgb(0x79, 0x8B, 0xA6),
            EventCategory.Power => Yellow,
            EventCategory.System => Err,
            EventCategory.User => Crit,
            _ => Muted
        };

        public static Color StatusColor(CheckStatus s) => s switch
        {
            CheckStatus.Ok => Ok,
            CheckStatus.Info => Info,
            CheckStatus.Warning => Warn,
            _ => Err
        };

        public static string StatusText(CheckStatus s) => s switch
        {
            CheckStatus.Ok => "OK",
            CheckStatus.Info => "Info",
            CheckStatus.Warning => "Attention",
            _ => "Problème"
        };

        public static Color Blend(Color a, Color b, double t) =>
            Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = radius * 2;
            if (radius <= 0 || r.Width < d || r.Height < d) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void SetDoubleBuffered(Control c)
        {
            typeof(Control).GetProperty("DoubleBuffered", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(c, true, null);
        }

        public static DataGridView Grid(bool fill = true)
        {
            var g = new DataGridView();
            StyleGrid(g, fill);
            return g;
        }

        public static void StyleGrid(DataGridView g, bool fill = true)
        {
            g.BackgroundColor = Surface;
            g.BorderStyle = BorderStyle.None;
            g.GridColor = Border;
            g.EnableHeadersVisualStyles = false;
            g.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Surface2, ForeColor = Muted, Font = SmallBold, SelectionBackColor = Surface2, SelectionForeColor = Muted,
                Padding = new Padding(4, 0, 0, 0), WrapMode = DataGridViewTriState.False
            };
            g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            g.ColumnHeadersHeight = 30;
            g.DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Surface, ForeColor = Text, SelectionBackColor = Selection, SelectionForeColor = Color.White, Font = Base,
                Padding = new Padding(4, 0, 4, 0), WrapMode = DataGridViewTriState.False
            };
            g.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(0x19, 0x1F, 0x27) };
            g.RowHeadersVisible = false;
            g.AllowUserToAddRows = false;
            g.AllowUserToDeleteRows = false;
            g.AllowUserToResizeRows = false;
            g.AllowUserToOrderColumns = false;
            g.ReadOnly = true;
            g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            g.MultiSelect = false;
            g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            g.RowTemplate.Height = 26;
            g.AutoSizeColumnsMode = fill ? DataGridViewAutoSizeColumnsMode.Fill : DataGridViewAutoSizeColumnsMode.None;
            g.Dock = DockStyle.Fill;
            SetDoubleBuffered(g);
            foreach (Control c in g.Controls)
                if (c is ScrollBar) DarkScroll(c);
        }

        /// <summary>Barres de défilement sombres (Windows 10 1809+ / Windows 11).</summary>
        public static void DarkScroll(Control c)
        {
            void Apply() { try { NativeMethods.SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { } }
            if (c.IsHandleCreated) Apply();
            c.HandleCreated += (s, e) => Apply();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        /// <summary>Texte d'invite grisé dans un TextBox vide.</summary>
        public static void SetCue(TextBox tb, string text)
        {
            void Apply() { try { SendMessage(tb.Handle, 0x1501 /* EM_SETCUEBANNER */, (IntPtr)1, text); } catch { } }
            if (tb.IsHandleCreated) Apply();
            tb.HandleCreated += (s, e) => Apply();
        }

        public static string ShortPath(string p, int max = 36) => string.IsNullOrEmpty(p) || p.Length <= max ? p : "…" + p.Substring(p.Length - max + 1);

        public static DataGridViewTextBoxColumn Col(string header, float weight = 100, int minWidth = 60, bool wrap = false)
        {
            return new DataGridViewTextBoxColumn
            {
                HeaderText = header, FillWeight = weight, MinimumWidth = minWidth, SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = wrap ? new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True } : null
            };
        }

        public static Button MakeButton(string text, Color? accent = null, int width = 0)
        {
            var b = new Button
            {
                Text = text, FlatStyle = FlatStyle.Flat, ForeColor = Text, BackColor = accent ?? Surface3, Font = Bold,
                Height = 32, AutoSize = width == 0, Cursor = Cursors.Hand, Padding = new Padding(10, 0, 10, 0), UseVisualStyleBackColor = false
            };
            if (width > 0) b.Width = width;
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Blend(accent ?? Surface3, Color.White, 0.12);
            b.FlatAppearance.MouseDownBackColor = Blend(accent ?? Surface3, Color.Black, 0.2);
            return b;
        }

        public static Label MakeLabel(string text, Font font = null, Color? color = null, bool autoSize = true)
        {
            return new Label { Text = text, Font = font ?? Base, ForeColor = color ?? Text, AutoSize = autoSize, BackColor = Color.Transparent };
        }

        public static void StyleInput(Control c)
        {
            c.BackColor = Surface2;
            c.ForeColor = Text;
            c.Font = Base;
            if (c is TextBox tb) { tb.BorderStyle = BorderStyle.FixedSingle; DarkScroll(tb); }
            if (c is ComboBox cb) { cb.FlatStyle = FlatStyle.Flat; cb.DropDownStyle = ComboBoxStyle.DropDownList; }
            if (c is NumericUpDown nud) nud.BorderStyle = BorderStyle.FixedSingle;
        }
    }
}
