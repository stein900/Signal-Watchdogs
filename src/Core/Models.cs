using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SignalWatchdogs.Core
{
    public enum Severity { Info = 0, Notice = 1, Warning = 2, Error = 3, Critical = 4 }

    public enum EventCategory { Display, Gpu, Usb, UsbC, Device, Session, Process, Power, System, User, Watchdog }

    /// <summary>Codes normalisés : servent à la corrélation et au diagnostic.</summary>
    public static class Codes
    {
        // Affichage
        public const string DisplayLost = "DISPLAY_LOST";
        public const string DisplayBack = "DISPLAY_BACK";
        public const string DisplayNew = "DISPLAY_NEW";
        public const string DisplayMode = "DISPLAY_MODE";
        public const string DisplayPrimary = "DISPLAY_PRIMARY";
        public const string DisplayTopoError = "DISPLAY_TOPO_ERR";
        public const string WmDisplayChange = "WM_DISPLAYCHANGE";
        public const string MonitorPnpArrival = "MONITOR_PNP_ARRIVAL";
        public const string MonitorPnpRemoval = "MONITOR_PNP_REMOVAL";
        public const string DwmRestart = "DWM_RESTART";
        public const string DwmCrash = "DWM_CRASH";
        public const string ExplorerRestart = "EXPLORER_RESTART";
        public const string DisplayPowerOff = "DISPLAY_POWER_OFF";
        public const string DisplayPowerOn = "DISPLAY_POWER_ON";
        public const string DisplayDimmed = "DISPLAY_DIMMED";
        // GPU
        public const string Tdr = "TDR";
        public const string GpuDriverError = "GPU_DRIVER_ERROR";
        public const string LiveKernelGpu = "LIVEKERNEL_GPU";
        public const string GpuAdapterLost = "GPU_ADAPTER_LOST";
        public const string GpuAdapterNew = "GPU_ADAPTER_NEW";
        public const string GpuAdapterPnp = "GPU_ADAPTER_PNP";
        public const string GpuLost = "GPU_LOST";
        public const string GpuTemp = "GPU_TEMP";
        public const string GpuVram = "GPU_VRAM";
        public const string GpuPcie = "GPU_PCIE";
        public const string GpuDriverChanged = "GPU_DRIVER_CHANGED";
        public const string GpuEncodeOn = "GPU_ENCODE_ON";
        public const string GpuEncodeOff = "GPU_ENCODE_OFF";
        public const string DxgkError = "DXGK_ERROR";
        public const string BasicDisplay = "BASIC_DISPLAY";
        public const string VirtualDisplay = "VIRTUAL_DISPLAY";
        // USB
        public const string UsbAdded = "USB_ADDED";
        public const string UsbRemoved = "USB_REMOVED";
        public const string UsbHubRemoved = "USB_HUB_REMOVED";
        public const string UsbError = "USB_ERROR";
        public const string XhciError = "XHCI_ERROR";
        public const string UcsiError = "UCSI_ERROR";
        public const string LiveKernelUsb = "LIVEKERNEL_USB";
        public const string PnpMissing = "PNP_MISSING";
        // Périphériques
        public const string DeviceProblem = "DEV_PROBLEM";
        public const string DeviceProblemCleared = "DEV_PROBLEM_CLEARED";
        public const string DeviceAdded = "DEV_ADDED";
        public const string DeviceRemoved = "DEV_REMOVED";
        public const string DriverInstall = "DRIVER_INSTALL";
        public const string DeviceStartProblem = "DEV_START_PROBLEM";
        // Sessions
        public const string RdpInConnect = "RDP_IN_CONNECT";
        public const string RdpInDisconnect = "RDP_IN_DISCONNECT";
        public const string RdpOutConnect = "RDP_OUT_CONNECT";
        public const string RdpOutDisconnect = "RDP_OUT_DISCONNECT";
        public const string ConsoleConnect = "CONSOLE_CONNECT";
        public const string ConsoleDisconnect = "CONSOLE_DISCONNECT";
        public const string RemoteConnect = "REMOTE_CONNECT";
        public const string RemoteDisconnect = "REMOTE_DISCONNECT";
        public const string SessionLock = "SESSION_LOCK";
        public const string SessionUnlock = "SESSION_UNLOCK";
        public const string Logon = "LOGON";
        public const string Logoff = "LOGOFF";
        // Processus
        public const string AppStart = "APP_START";
        public const string AppStop = "APP_STOP";
        public const string AppCrash = "APP_CRASH";
        // Alimentation
        public const string Suspend = "SUSPEND";
        public const string Resume = "RESUME";
        public const string Lid = "LID";
        public const string PowerSource = "POWER_SOURCE";
        public const string Thermal = "THERMAL";
        // Système
        public const string Stall = "STALL";
        public const string KernelPower41 = "KERNEL_POWER_41";
        public const string UnexpectedShutdown = "UNEXPECTED_SHUTDOWN";
        public const string Bsod = "BSOD";
        public const string Whea = "WHEA";
        public const string WindowsUpdate = "WINDOWS_UPDATE";
        public const string ServiceCrash = "SERVICE_CRASH";
        public const string LiveKernelOther = "LIVEKERNEL_OTHER";
        // Utilisateur / outil
        public const string UserReport = "USER_REPORT";
        public const string WatchdogStart = "WD_START";
        public const string WatchdogStop = "WD_STOP";
        public const string WatchdogInfo = "WD_INFO";

        public static readonly HashSet<string> GpuCrashCodes = new HashSet<string> { Tdr, GpuDriverError, LiveKernelGpu, DxgkError, BasicDisplay };
        public static readonly HashSet<string> UsbFaultCodes = new HashSet<string> { UsbHubRemoved, UsbError, XhciError, UcsiError, LiveKernelUsb };
        public static readonly HashSet<string> SessionSwitchCodes = new HashSet<string> { RdpInConnect, RdpInDisconnect, ConsoleConnect, ConsoleDisconnect, RemoteConnect, RemoteDisconnect, Logon, Logoff };
    }

    public sealed class WatchEvent
    {
        public long Seq;
        public DateTime Time;
        public Severity Severity;
        public EventCategory Category;
        public string Code;
        public string Source;
        public string Title;
        public string Details;
        public bool Trigger;
        public bool Historical;
        public int IncidentId;
        public Dictionary<string, string> Data;

        public WatchEvent(Severity sev, EventCategory cat, string code, string title, string details = null, bool trigger = false)
        {
            Time = DateTime.Now;
            Severity = sev;
            Category = cat;
            Code = code;
            Title = title;
            Details = details ?? "";
            Trigger = trigger;
            Source = "";
        }

        public WatchEvent With(string key, object value)
        {
            if (value == null) return this;
            Data ??= new Dictionary<string, string>();
            Data[key] = Convert.ToString(value, CultureInfo.InvariantCulture);
            return this;
        }

        public string Get(string key) => Data != null && Data.TryGetValue(key, out var v) ? v : null;

        public Dictionary<string, object> ToDict()
        {
            var d = new Dictionary<string, object>
            {
                ["seq"] = Seq,
                ["time"] = Time.ToString("o"),
                ["severity"] = Severity.ToString(),
                ["category"] = Category.ToString(),
                ["code"] = Code,
                ["source"] = Source,
                ["title"] = Title,
                ["details"] = Details,
                ["trigger"] = Trigger,
                ["machine"] = Environment.MachineName,
            };
            if (IncidentId > 0) d["incident"] = IncidentId;
            if (Data != null && Data.Count > 0) d["data"] = Data;
            return d;
        }

        public static WatchEvent FromDict(Dictionary<string, object> d)
        {
            Enum.TryParse(Str(d, "severity"), out Severity sev);
            Enum.TryParse(Str(d, "category"), out EventCategory cat);
            var e = new WatchEvent(sev, cat, Str(d, "code"), Str(d, "title"), Str(d, "details"), Str(d, "trigger") == "True")
            {
                Source = Str(d, "source"),
                Historical = true,
            };
            if (DateTime.TryParse(Str(d, "time"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)) e.Time = t;
            if (d.TryGetValue("data", out var dataObj) && dataObj is Dictionary<string, object> data)
                foreach (var kv in data) e.With(kv.Key, kv.Value);
            return e;
        }

        internal static string Str(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
    }

    public sealed class Diagnosis
    {
        public string Title = "";
        public string Cause = "";          // clé courte : GPU_DRIVER, CABLE, DOCK, RDP, POWER_SAVE, ...
        public string Confidence = "Faible";
        public string Explanation = "";
        public List<string> Recommendations = new List<string>();
        public List<string> Factors = new List<string>();
    }

    public sealed class Incident
    {
        public int Id;
        public DateTime Start;
        public DateTime End;
        public DateTime LastTrigger;
        public Severity Severity;
        public bool Closed;
        public bool Restored;       // rechargé depuis les journaux
        public bool Historical;     // reconstruit à partir de l'historique Windows
        public readonly List<WatchEvent> Events = new List<WatchEvent>();
        public Diagnosis Diagnosis = new Diagnosis();
        public string Context = "";
        public List<string> ActiveApps = new List<string>();
        public string SessionState = "";
        public List<string> AffectedMonitors = new List<string>();

        public IEnumerable<WatchEvent> Triggers => Events.Where(e => e.Trigger);

        /// <summary>Copie sûre de la liste d'événements (le moteur peut en ajouter depuis un autre thread).</summary>
        public List<WatchEvent> EventList()
        {
            lock (Events) return new List<WatchEvent>(Events);
        }
        public TimeSpan Duration => (End > Start ? End - Start : TimeSpan.Zero);
        public bool Has(string code) => Events.Any(e => e.Code == code);
        public bool HasAny(ICollection<string> codes) => Events.Any(e => codes.Contains(e.Code));

        public string ShortTitle => string.IsNullOrEmpty(Diagnosis.Title) ? "Analyse en cours…" : Diagnosis.Title;

        public Dictionary<string, object> ToDict()
        {
            return new Dictionary<string, object>
            {
                ["id"] = Id,
                ["machine"] = Environment.MachineName,
                ["start"] = Start.ToString("o"),
                ["end"] = End.ToString("o"),
                ["severity"] = Severity.ToString(),
                ["diagnosis"] = Diagnosis.Title,
                ["cause"] = Diagnosis.Cause,
                ["confidence"] = Diagnosis.Confidence,
                ["explanation"] = Diagnosis.Explanation,
                ["recommendations"] = Diagnosis.Recommendations,
                ["factors"] = Diagnosis.Factors,
                ["context"] = Context,
                ["apps"] = ActiveApps,
                ["session"] = SessionState,
                ["monitors"] = AffectedMonitors,
                ["events"] = EventList().Select(e => e.ToDict()).ToList(),
            };
        }

        public static Incident FromDict(Dictionary<string, object> d)
        {
            var inc = new Incident { Restored = true, Closed = true };
            int.TryParse(WatchEvent.Str(d, "id"), out inc.Id);
            DateTime.TryParse(WatchEvent.Str(d, "start"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out inc.Start);
            DateTime.TryParse(WatchEvent.Str(d, "end"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out inc.End);
            inc.LastTrigger = inc.End;
            Enum.TryParse(WatchEvent.Str(d, "severity"), out inc.Severity);
            inc.Diagnosis.Title = WatchEvent.Str(d, "diagnosis") ?? "";
            inc.Diagnosis.Cause = WatchEvent.Str(d, "cause") ?? "";
            inc.Diagnosis.Confidence = WatchEvent.Str(d, "confidence") ?? "";
            inc.Diagnosis.Explanation = WatchEvent.Str(d, "explanation") ?? "";
            inc.Diagnosis.Recommendations = StrList(d, "recommendations");
            inc.Diagnosis.Factors = StrList(d, "factors");
            inc.Context = WatchEvent.Str(d, "context") ?? "";
            inc.ActiveApps = StrList(d, "apps");
            inc.SessionState = WatchEvent.Str(d, "session") ?? "";
            inc.AffectedMonitors = StrList(d, "monitors");
            if (d.TryGetValue("events", out var evs) && evs is System.Collections.IEnumerable list)
                foreach (var o in list)
                    if (o is Dictionary<string, object> ed) inc.Events.Add(WatchEvent.FromDict(ed));
            return inc;
        }

        static List<string> StrList(Dictionary<string, object> d, string k)
        {
            var res = new List<string>();
            if (d.TryGetValue(k, out var v) && v is System.Collections.IEnumerable list && !(v is string))
                foreach (var o in list) if (o != null) res.Add(o.ToString());
            return res;
        }
    }

    /// <summary>État d'une source de surveillance (affiché dans l'onglet Diagnostic).</summary>
    public sealed class SourceStatus
    {
        public string Name;
        public bool Ok;
        public string Detail;
        public DateTime Updated = DateTime.Now;
    }

    public static class Fr
    {
        public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("fr-FR");

        public static string Sev(Severity s) => s switch
        {
            Severity.Info => "Info",
            Severity.Notice => "Notice",
            Severity.Warning => "Alerte",
            Severity.Error => "Erreur",
            Severity.Critical => "Critique",
            _ => s.ToString()
        };

        public static string Cat(EventCategory c) => c switch
        {
            EventCategory.Display => "Écran",
            EventCategory.Gpu => "GPU",
            EventCategory.Usb => "USB",
            EventCategory.UsbC => "USB-C",
            EventCategory.Device => "Périphérique",
            EventCategory.Session => "Session",
            EventCategory.Process => "Application",
            EventCategory.Power => "Alimentation",
            EventCategory.System => "Système",
            EventCategory.User => "Utilisateur",
            EventCategory.Watchdog => "Signal Watchdogs",
            _ => c.ToString()
        };

        public static string Duration(TimeSpan t)
        {
            if (t.TotalSeconds < 1) return $"{t.TotalMilliseconds:0} ms";
            if (t.TotalSeconds < 60) return t.TotalSeconds.ToString("0.0", Culture) + " s";
            if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes} min {t.Seconds:00} s";
            if (t.TotalHours < 48) return $"{(int)t.TotalHours} h {t.Minutes:00} min";
            return $"{(int)t.TotalDays} j {t.Hours} h";
        }

        public static string Bytes(double b)
        {
            if (b >= 1L << 30) return (b / (1L << 30)).ToString("0.0", Culture) + " Go";
            if (b >= 1L << 20) return (b / (1L << 20)).ToString("0", Culture) + " Mo";
            return (b / 1024).ToString("0", Culture) + " Ko";
        }

        public static string Ago(DateTime t)
        {
            var d = DateTime.Now - t;
            if (d.TotalSeconds < 60) return "à l'instant";
            if (d.TotalMinutes < 60) return $"il y a {(int)d.TotalMinutes} min";
            if (d.TotalHours < 24) return $"il y a {(int)d.TotalHours} h";
            return $"il y a {(int)d.TotalDays} j";
        }
    }
}
