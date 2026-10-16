using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using SignalWatchdogs.Core;
using SignalWatchdogs.Diagnostics;
using SignalWatchdogs.Native;
using SignalWatchdogs.UI;

// Sécurité : les DLL natives appelées par P/Invoke ne sont cherchées que dans System32
// (jamais dans le dossier de l'exe, qui peut être modifiable par un utilisateur).
[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]

namespace SignalWatchdogs
{
    static class Program
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetDefaultDllDirectories(uint directoryFlags);
        const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x00000800;

        /// <summary>
        /// Options :
        ///   --minimized / -m          démarre dans la zone de notification
        ///   --report &lt;fichier.html&gt;   génère un rapport de diagnostic sans interface puis quitte
        ///   --logdir &lt;dossier&gt;        force le dossier des journaux (ex. partage réseau)
        ///   --export-icon &lt;fichier&gt;   écrit l'icône de l'application (utilisé par build.ps1)
        /// </summary>
        [STAThread]
        static int Main(string[] args)
        {
            // Durcissement contre le détournement de DLL : tout chargement dynamique ultérieur
            // (y compris par les composants Windows) est limité à System32.
            try { SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32); } catch { }

            string Arg(string name) { int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            bool Flag(params string[] names) => args.Any(a => names.Any(n => a.Equals(n, StringComparison.OrdinalIgnoreCase)));

            var icon = Arg("--export-icon");
            if (icon != null) { AppIcon.WriteIco(icon); return 0; }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Crash(e.Exception, false);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Crash(e.ExceptionObject as Exception, true);

            if (int.TryParse(Arg("--wait-pid"), out int waitPid))
                try { Process.GetProcessById(waitPid).WaitForExit(15000); } catch { }

            var settings = Settings.Load();
            var logdir = Arg("--logdir");
            if (logdir != null) settings.LogDirectory = logdir;

            var report = Arg("--report");
            if (report != null) return HeadlessReport(settings, report);

            var shots = Arg("--screenshots");
            using var mutex = new Mutex(true, @"Local\SignalWatchdogs_DisplayWatchdog_SingleInstance", out bool first);
            if (!first && shots == null)
            {
                // Une instance tourne déjà : on lui demande d'afficher sa fenêtre.
                NativeMethods.PostMessage(NativeMethods.HWND_BROADCAST, SystemMessageWindowShowMessage(), IntPtr.Zero, IntPtr.Zero);
                return 0;
            }

            using var svc = new WatchdogService(settings);
            svc.Start();
            using (var form = new MainForm(svc, shots == null && (Flag("--minimized", "-m", "/minimized") || settings.StartMinimized)))
            {
                if (shots != null) form.Shown += (s, e) => form.RunScreenshotTour(shots, int.TryParse(Arg("--shot-delay"), out var d) ? d : 8000);
                Application.Run(form);
            }
            return 0;
        }

        static uint SystemMessageWindowShowMessage() => Monitors.SystemMessageWindow.ShowMessage;

        static int HeadlessReport(Settings settings, string path)
        {
            try
            {
                using var svc = new WatchdogService(settings);
                svc.Start();
                var sw = Stopwatch.StartNew();
                Thread.Sleep(4000);
                while ((svc.History.Running || !svc.History.ScannedAt.HasValue) && sw.Elapsed.TotalSeconds < 240) Thread.Sleep(500);
                while ((svc.Gpu.Adapters.Count == 0 || svc.Devices.LastScan == default) && sw.Elapsed.TotalSeconds < 260) Thread.Sleep(500);
                svc.Checks = SystemDiagnostics.Run(svc);
                var full = Path.GetFullPath(path);
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                File.WriteAllText(full, HtmlReport.Build(svc), new UTF8Encoding(false));
                return 0;
            }
            catch (Exception ex)
            {
                Crash(ex, false);
                return 1;
            }
        }

        static void Crash(Exception ex, bool fatal)
        {
            if (ex == null) return;
            try
            {
                // En administrateur, jamais dans le profil utilisateur (cible possible d'une jonction).
                var dir = SecurityGuard.IsElevated
                    ? Hub.Log?.MachineDirectory
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SignalWatchdogs");
                if (dir == null || (SecurityGuard.IsElevated && (Hub.Log?.Disabled ?? true))) return;
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "crash.log"), $"{DateTime.Now:o} {(fatal ? "FATAL" : "ERREUR")} {ex}\r\n\r\n", Encoding.UTF8);
                Hub.Publish(Severity.Error, EventCategory.Watchdog, Codes.WatchdogInfo, "Erreur interne de Signal Watchdogs", ex.GetType().Name + " : " + ex.Message, source: "Signal Watchdogs");
            }
            catch { }
        }
    }
}
