using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using SignalWatchdogs.Core;
using SignalWatchdogs.Diagnostics;
using SignalWatchdogs.Native;

namespace SignalWatchdogs.UI
{
    public sealed class MainForm : Form
    {
        readonly WatchdogService _svc;
        readonly Dictionary<string, PageBase> _pages = new Dictionary<string, PageBase>();
        readonly Dictionary<string, NavButton> _nav = new Dictionary<string, NavButton>();
        readonly Label _title, _subtitle, _sideInfo;
        readonly StatusPill _pill;
        readonly Panel _content;
        readonly NotifyIcon _tray;
        readonly Timer _timer;
        readonly Icon _iconNormal, _iconAlert, _trayNormal, _trayAlert;
        bool _allowShow, _exiting, _trayHintShown, _alertShown;
        string _current;
        int _balloonIncident = -1;
        DateTime _lastBalloon = DateTime.MinValue;
        readonly Dictionary<int, string> _notifiedTitles = new Dictionary<int, string>();

        public MainForm(WatchdogService svc, bool startMinimized)
        {
            _svc = svc;
            _allowShow = !startMinimized;
            _iconNormal = AppIcon.Create();
            _iconAlert = AppIcon.Create(true);
            _trayNormal = AppIcon.CreateSmall();
            _trayAlert = AppIcon.CreateSmall(true);

            Text = "Signal Watchdogs — Surveillance écrans · GPU · USB";
            Icon = _iconNormal;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Base;
            AutoScaleMode = AutoScaleMode.Dpi;
            Size = new Size(1440, 900);
            MinimumSize = new Size(1100, 700);
            StartPosition = FormStartPosition.CenterScreen;

            // ---- Barre latérale ----
            var side = new Panel { Dock = DockStyle.Left, Width = 236, BackColor = Theme.Sidebar };
            var logo = new LogoPanel { Dock = DockStyle.Top, Height = 84 };
            var navList = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Sidebar, Padding = new Padding(0, 6, 0, 0) };
            _sideInfo = new Label { Dock = DockStyle.Bottom, Height = 92, ForeColor = Theme.Muted, Font = Theme.Small, BackColor = Theme.Sidebar, Padding = new Padding(16, 8, 10, 8) };
            foreach (var (key, label, glyph) in new[]
            {
                ("dashboard", "Tableau de bord", ""), ("incidents", "Incidents", ""), ("journal", "Journal", ""),
                ("screens", "Écrans", ""), ("gpu", "Cartes graphiques", ""), ("devices", "USB & périphériques", ""),
                ("history", "Historique Windows", ""), ("diagnostic", "Diagnostic", ""), ("fleet", "Vue parc", ""), ("settings", "Paramètres", ""),
            })
            {
                var b = new NavButton { Text = label, Glyph = glyph, Width = 236, Margin = new Padding(0) };
                var k = key;
                b.Click += (s, e) => Navigate(k);
                _nav[key] = b;
                navList.Controls.Add(b);
            }
            side.Controls.Add(navList);
            side.Controls.Add(_sideInfo);
            side.Controls.Add(logo);

            // ---- En-tête ----
            var header = new Panel { Dock = DockStyle.Top, Height = 74, BackColor = Theme.Bg };
            _title = new Label { Text = "", Font = Theme.H1, ForeColor = Theme.Text, AutoSize = true, Location = new Point(18, 10), BackColor = Theme.Bg, UseMnemonic = false };
            _subtitle = new Label { Text = "", Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = true, Location = new Point(20, 44), BackColor = Theme.Bg };
            var actions = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, Width = 760, BackColor = Theme.Bg, Padding = new Padding(0, 20, 14, 0), WrapContents = false };
            var exportBtn = Theme.MakeButton("Exporter le rapport HTML");
            exportBtn.Click += (s, e) => ExportReport();
            var reportBtn = Theme.MakeButton("⚑  Signaler un écran noir", Theme.Err);
            reportBtn.Click += (s, e) => ReportFromUi();
            _pill = new StatusPill { Margin = new Padding(0, 1, 12, 0) };
            actions.Controls.AddRange(new Control[] { exportBtn, reportBtn, _pill });
            header.Controls.Add(_title);
            header.Controls.Add(_subtitle);
            header.Controls.Add(actions);

            _content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            var right = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            right.Controls.Add(_content);
            right.Controls.Add(header);
            Controls.Add(right);
            Controls.Add(side);

            // ---- Pages ----
            AddPage("dashboard", new DashboardPage(svc, this));
            AddPage("incidents", new IncidentsPage(svc, this));
            AddPage("journal", new JournalPage(svc, this));
            AddPage("screens", new ScreensPage(svc, this));
            AddPage("gpu", new GpuPage(svc, this));
            AddPage("devices", new DevicesPage(svc, this));
            AddPage("history", new HistoryPage(svc, this));
            AddPage("diagnostic", new DiagnosticPage(svc, this));
            AddPage("fleet", new FleetPage(svc, this));
            AddPage("settings", new SettingsPage(svc, this));

            // ---- Zone de notification ----
            var menu = new ContextMenuStrip { Renderer = new ToolStripProfessionalRenderer(new DarkColors()), ForeColor = Theme.Text, BackColor = Theme.Surface, ShowImageMargin = false };
            menu.Items.Add("Ouvrir le tableau de bord", null, (s, e) => ShowFromTray());
            menu.Items.Add($"Signaler un écran noir  ({svc.Settings.Hotkey})", null, (s, e) => { ShowFromTray(); ReportFromUi(); });
            menu.Items.Add("Exporter un rapport HTML…", null, (s, e) => { ShowFromTray(); ExportReport(); });
            menu.Items.Add("Ouvrir le dossier des journaux", null, (s, e) => OpenFolder(_svc.Log.MachineDirectory));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quitter Signal Watchdogs", null, (s, e) => ExitApp(true));
            foreach (ToolStripItem it in menu.Items) it.ForeColor = Theme.Text;
            _tray = new NotifyIcon { Icon = _trayNormal, Text = "Signal Watchdogs — surveillance active", Visible = true, ContextMenuStrip = menu };
            _tray.DoubleClick += (s, e) => ShowFromTray();
            _tray.BalloonTipClicked += (s, e) =>
            {
                ShowFromTray();
                if (_balloonIncident > 0) Navigate("incidents", _balloonIncident);
            };

            // ---- Branchements ----
            svc.AttachMessageWindow();
            svc.Messages.HotkeyPressed += OnHotkey;
            svc.Messages.ShowRequested += ShowFromTray;
            svc.Incidents.Changed += (inc, change) =>
            {
                if (!IsHandleCreated || IsDisposed) return;
                try { BeginInvoke(new Action(() => OnIncidentChanged(inc, change))); } catch { }
            };

            _timer = new Timer { Interval = 1000 };
            _timer.Tick += (s, e) => RefreshUi();
            _timer.Start();
            Navigate("dashboard");
        }

        void AddPage(string key, PageBase page)
        {
            page.Visible = false;
            _pages[key] = page;
            _content.Controls.Add(page);
        }

        protected override void SetVisibleCore(bool value)
        {
            if (!_allowShow)
            {
                value = false;
                if (!IsHandleCreated) CreateHandle();
            }
            base.SetVisibleCore(value);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeMethods.UseDarkTitleBar(Handle);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (!_trayHintShown)
                {
                    _trayHintShown = true;
                    _tray.ShowBalloonTip(4000, "Signal Watchdogs reste actif", "La surveillance continue en arrière-plan. Double-cliquez sur l'icône pour rouvrir le tableau de bord.", ToolTipIcon.Info);
                }
                return;
            }
            _timer.Stop();
            _tray.Visible = false;
            base.OnFormClosing(e);
        }

        public void ShowFromTray()
        {
            _allowShow = true;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
            NativeMethods.SetForegroundWindow(Handle);
            RefreshUi();
        }

        public void ExitApp(bool confirm)
        {
            if (confirm && MessageBox.Show(this, "Arrêter Signal Watchdogs ? La surveillance des écrans, GPU et USB sera interrompue.", "Signal Watchdogs",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _exiting = true;
            Close();
        }

        public void Navigate(string key, object arg = null)
        {
            if (!_pages.TryGetValue(key, out var page)) return;
            if (_current != null && _current != key) { _pages[_current].Visible = false; _nav[_current].Selected = false; }
            _current = key;
            _nav[key].Selected = true;
            _title.Text = page.Title;
            _subtitle.Text = page.Subtitle;
            page.Visible = true;
            page.BringToFront();
            try { page.RefreshData(); } catch { }
            if (arg != null) try { page.Select(arg); } catch { }
        }

        void RefreshUi()
        {
            var now = DateTime.Now;
            var incs = _svc.AllIncidents();
            int open = incs.Count(i => !i.Closed);
            int lost = _svc.Topology?.LostNow?.Count ?? 0;
            var recent = incs.Where(i => !i.Restored && i.LastTrigger >= now.AddMinutes(-60)).OrderByDescending(i => i.LastTrigger).FirstOrDefault();
            if (lost > 0) _pill.Set($"{lost} écran(s) perdu(s) en ce moment", Theme.Err);
            else if (open > 0) _pill.Set("Incident en cours d'analyse…", Theme.Crit);
            else if (recent != null) _pill.Set($"Dernier incident {Fr.Ago(recent.LastTrigger)}", Theme.Warn);
            else _pill.Set("Surveillance active · aucun incident récent", Theme.Ok);

            int inc24 = incs.Count(i => i.Start >= now.AddHours(-24));
            _nav["incidents"].Badge = inc24 > 0 ? inc24.ToString() : null;
            _nav["incidents"].BadgeColor = open > 0 ? Theme.Crit : Theme.Warn;
            int warn = _svc.Checks.Count(c => c.Status >= CheckStatus.Warning);
            _nav["diagnostic"].Badge = warn > 0 ? warn.ToString() : null;
            _nav["diagnostic"].BadgeColor = _svc.Checks.Any(c => c.Status == CheckStatus.Problem) ? Theme.Err : Theme.Warn;
            _nav["history"].Badge = _svc.History.Running ? "…" : null;
            _nav["history"].BadgeColor = Theme.Accent;
            foreach (var b in _nav.Values) b.Invalidate();

            _sideInfo.Text = $"{Environment.MachineName} · {Environment.UserName}\n{(_svc.IsAdmin ? "Administrateur" : "Droits standard")} · actif depuis {Fr.Duration(now - _svc.StartedAt)}\n" +
                             $"{Hub.LastSeq} événements · {incs.Count} incident(s)\nJournaux : {Theme.ShortPath(_svc.Log.MachineDirectory, 30)}";

            bool alert = open > 0 || lost > 0 || (recent != null && recent.LastTrigger >= now.AddMinutes(-10));
            if (alert != _alertShown)
            {
                _alertShown = alert;
                _tray.Icon = alert ? _trayAlert : _trayNormal;
                Icon = alert ? _iconAlert : _iconNormal;
            }
            _tray.Text = Trunc(alert ? "Signal Watchdogs — incident récent" : "Signal Watchdogs — surveillance active", 63);

            if (Visible && WindowState != FormWindowState.Minimized && _current != null)
                try { _pages[_current].RefreshData(); } catch (Exception ex) { Debug.WriteLine(ex); }
        }

        static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n);

        void OnIncidentChanged(Incident inc, IncidentChange change)
        {
            if (!_svc.Settings.Notifications) return;
            if (change == IncidentChange.Updated) return;
            _notifiedTitles.TryGetValue(inc.Id, out var shownTitle);
            if (change == IncidentChange.Closed && (shownTitle == inc.ShortTitle || inc.Severity < Severity.Warning)) return;
            if (change == IncidentChange.Opened && (DateTime.Now - _lastBalloon).TotalSeconds < 15) return;
            _notifiedTitles[inc.Id] = inc.ShortTitle;
            _lastBalloon = DateTime.Now;
            _balloonIncident = inc.Id;
            var head = change == IncidentChange.Opened ? "Anomalie d'affichage détectée" : "Diagnostic de l'incident";
            var body = inc.ShortTitle + (inc.Diagnosis.Recommendations.Count > 0 ? "\n→ " + inc.Diagnosis.Recommendations[0] : "");
            _tray.ShowBalloonTip(6000, head, Trunc(body, 250), inc.Severity >= Severity.Error ? ToolTipIcon.Error : ToolTipIcon.Warning);
        }

        void OnHotkey()
        {
            _svc.ReportUserIncident("Signalé via le raccourci " + _svc.Settings.Hotkey, null);
            _balloonIncident = -1;
            _lastBalloon = DateTime.Now; // le diagnostic sera notifié à la clôture de l'incident
            _tray.ShowBalloonTip(5000, "Signalement enregistré", "Signal Watchdogs corrèle ce signalement avec tout ce qui s'est passé dans les 2 dernières minutes. Résultat dans l'onglet Incidents.", ToolTipIcon.Info);
        }

        public void ReportFromUi()
        {
            using var dlg = new ReportDialog();
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            var e = _svc.ReportUserIncident(dlg.Note, dlg.Scope);
            Navigate("incidents", e.IncidentId > 0 ? (object)e.IncidentId : null);
        }

        public void ExportReport()
        {
            using var dlg = new SaveFileDialog
            {
                Filter = "Rapport HTML (*.html)|*.html",
                FileName = $"SignalWatchdogs_{Environment.MachineName}_{DateTime.Now:yyyyMMdd_HHmm}.html",
                InitialDirectory = _svc.Log.MachineDirectory
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                File.WriteAllText(dlg.FileName, HtmlReport.Build(_svc), new UTF8Encoding(false));
                Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show(this, "Impossible d'écrire le rapport : " + ex.Message, "Signal Watchdogs", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        public void RestartElevated()
        {
            // Un exe lancé en administrateur depuis un dossier modifiable par un utilisateur peut être remplacé
            // (ou accompagné d'une DLL / d'un SignalWatchdogs.exe.config piégés) : on refuse dans ce cas.
            var why = SecurityGuard.WhyExecutableNotProtected(Settings.ExePath);
            if (why != null)
            {
                MessageBox.Show(this,
                    "Relance en administrateur refusée par sécurité : " + why + ".\n\n" +
                    "Copiez SignalWatchdogs.exe dans un dossier protégé (par exemple C:\\Program Files\\Signal Watchdogs) puis lancez-le en administrateur depuis cet emplacement.",
                    "Signal Watchdogs — sécurité", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                var psi = new ProcessStartInfo(Settings.ExePath, "--wait-pid " + Process.GetCurrentProcess().Id) { Verb = "runas", UseShellExecute = true };
                Process.Start(psi);
                _exiting = true;
                Close();
            }
            catch { /* élévation refusée par l'utilisateur */ }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        /// <summary>Option de vérification (--screenshots) : capture chaque page puis quitte.</summary>
        public async void RunScreenshotTour(string dir, int delayMs)
        {
            Directory.CreateDirectory(dir);
            await System.Threading.Tasks.Task.Delay(delayMs);
            foreach (var key in _pages.Keys.ToList())
            {
                Navigate(key);
                await System.Threading.Tasks.Task.Delay(2500);
                RefreshUi();
                await System.Threading.Tasks.Task.Delay(300);
                try
                {
                    // PrintWindow ne capture que cette fenêtre, même si elle est masquée par d'autres.
                    using var bmp = new Bitmap(Width, Height);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        var hdc = g.GetHdc();
                        try { PrintWindow(Handle, hdc, 2 /* PW_RENDERFULLCONTENT */); }
                        finally { g.ReleaseHdc(hdc); }
                    }
                    bmp.Save(Path.Combine(dir, key + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                catch { }
            }
            ExitApp(false);
        }

        public static void OpenFolder(string path, bool create = true)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || path.IndexOf('"') >= 0) return;
                if (create) Directory.CreateDirectory(path);
                else if (!Directory.Exists(path)) return;
                Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "\"" + path + "\"") { UseShellExecute = false });
            }
            catch { }
        }

        public static void ShowEventDetails(IWin32Window owner, WatchEvent ev) =>
            MessageBox.Show(owner, JournalPage.FormatEvent(ev), "Détail de l'événement", MessageBoxButtons.OK, MessageBoxIcon.Information);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _tray.Dispose();
                _timer.Dispose();
            }
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------
        sealed class LogoPanel : Panel
        {
            readonly Bitmap _icon = AppIcon.Render(40);
            // Plus petit que Theme.H1 : « Signal Watchdogs » doit tenir dans la barre latérale (236 px).
            static readonly Font TitleFont = new Font("Segoe UI Semibold", 12.5f);
            public LogoPanel()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
                BackColor = Theme.Sidebar;
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(Theme.Sidebar);
                e.Graphics.DrawImage(_icon, 18, 22, 40, 40);
                TextRenderer.DrawText(e.Graphics, "Signal Watchdogs", TitleFont, new Point(66, 21), Theme.Text);
                TextRenderer.DrawText(e.Graphics, "écrans · GPU · USB · RDP", Theme.Small, new Point(69, 48), Theme.Muted);
                using var pen = new Pen(Theme.Border);
                e.Graphics.DrawLine(pen, 16, Height - 1, Width - 16, Height - 1);
            }
        }

        sealed class DarkColors : ProfessionalColorTable
        {
            public override Color MenuItemSelected => Theme.Surface3;
            public override Color MenuItemBorder => Theme.Border;
            public override Color MenuBorder => Theme.Border;
            public override Color ToolStripDropDownBackground => Theme.Surface;
            public override Color ImageMarginGradientBegin => Theme.Surface;
            public override Color ImageMarginGradientMiddle => Theme.Surface;
            public override Color ImageMarginGradientEnd => Theme.Surface;
            public override Color SeparatorDark => Theme.Border;
            public override Color SeparatorLight => Theme.Surface;
            public override Color MenuItemSelectedGradientBegin => Theme.Surface3;
            public override Color MenuItemSelectedGradientEnd => Theme.Surface3;
            public override Color MenuItemPressedGradientBegin => Theme.Surface2;
            public override Color MenuItemPressedGradientEnd => Theme.Surface2;
        }
    }

    /// <summary>Fenêtre de signalement : permet à l'utilisateur de préciser ce qu'il a vu.</summary>
    public sealed class ReportDialog : Form
    {
        readonly RadioButton _one, _all, _flicker, _long;
        readonly TextBox _note;
        public string Scope { get; private set; }
        public string Note { get; private set; }

        public ReportDialog()
        {
            Text = "Signaler un écran noir";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
            Font = Theme.Base;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(520, 360);
            ShowInTaskbar = false;

            var title = Theme.MakeLabel("Qu'avez-vous constaté ?", Theme.H2);
            title.Location = new Point(20, 16);
            var help = Theme.MakeLabel("Signal Watchdogs va corréler ce signalement avec tout ce qui s'est passé juste avant.", Theme.Small, Theme.Muted);
            help.Location = new Point(20, 42);
            RadioButton R(string t, int y, bool c = false) => new RadioButton { Text = t, Location = new Point(24, y), AutoSize = true, Checked = c, ForeColor = Theme.Text };
            _one = R("Un seul écran est devenu noir", 72, true);
            _all = R("Plusieurs / tous les écrans sont devenus noirs en même temps", 100);
            _flicker = R("Simple clignotement (noir très bref)", 128);
            _long = R("Écran resté noir longtemps (plus de 10 secondes)", 156);
            var nl = Theme.MakeLabel("Précisions (quel écran ? que faisiez-vous : Teams, RDP, Parsec, vidéo… ?)", Theme.Small, Theme.Muted);
            nl.Location = new Point(20, 192);
            _note = new TextBox { Location = new Point(22, 214), Width = 476, Height = 70, Multiline = true };
            Theme.StyleInput(_note);
            var ok = Theme.MakeButton("Enregistrer le signalement", Theme.Err);
            ok.Location = new Point(250, 304);
            ok.DialogResult = DialogResult.OK;
            ok.Click += (s, e) =>
            {
                Scope = _all.Checked ? "all" : _one.Checked ? "one" : _flicker.Checked ? "flicker" : "long";
                var what = _all.Checked ? _all.Text : _one.Checked ? _one.Text : _flicker.Checked ? _flicker.Text : _long.Text;
                Note = what + (string.IsNullOrWhiteSpace(_note.Text) ? "" : " — " + _note.Text.Trim());
            };
            var cancel = Theme.MakeButton("Annuler");
            cancel.Location = new Point(150, 304);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.AddRange(new Control[] { title, help, _one, _all, _flicker, _long, nl, _note, ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;
            Load += (s, e) => { ok.Left = ClientSize.Width - ok.Width - 22; cancel.Left = ok.Left - cancel.Width - 10; };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeMethods.UseDarkTitleBar(Handle);
        }
    }
}
