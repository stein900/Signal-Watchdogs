using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SignalWatchdogs.Core;
using SignalWatchdogs.Native;

namespace SignalWatchdogs.Monitors
{
    /// <summary>
    /// Fenêtre cachée de premier niveau qui reçoit les notifications Windows en temps réel :
    /// WM_DISPLAYCHANGE, arrivées/retraits PnP (écrans, GPU, USB), extinction des écrans,
    /// veille, sessions RDP/console, raccourci clavier de signalement.
    /// </summary>
    public sealed class SystemMessageWindow : NativeWindow, IDisposable
    {
        const string Src = "Notifications Windows";
        const int HotkeyId = 0x5744;
        public static readonly uint ShowMessage = NativeMethods.RegisterWindowMessage("SignalWatchdogs_ShowMainWindow_v1");

        readonly List<IntPtr> _deviceNotifs = new List<IntPtr>();
        readonly List<IntPtr> _powerNotifs = new List<IntPtr>();
        bool _firstDisplayState = true;
        bool _firstLid = true;
        bool _firstAcdc = true;
        readonly int _ownSession = System.Diagnostics.Process.GetCurrentProcess().SessionId;

        public event Action DisplayChanged;
        public event Action<Guid, string, bool> DeviceInterfaceChanged;
        public event Action DevNodesChanged;
        public event Action HotkeyPressed;
        public event Action ShowRequested;
        public event Action Suspending;
        public event Action Resumed;

        public string HotkeyText { get; private set; }
        public bool HotkeyRegistered { get; private set; }
        public int DisplayPowerState { get; private set; } = 1; // 0 éteint, 1 allumé, 2 atténué
        DateTime _lastDisplayOn = DateTime.MinValue, _lastResume = DateTime.MinValue, _suspendedAt = DateTime.MinValue;
        // Une vraie mise en veille fige le processus : si l'on s'exécute encore 5 min après le message,
        // c'est que la veille n'a pas eu lieu (ou que le message était forgé) → l'état expire.
        bool _suspended => (DateTime.Now - _suspendedAt).TotalMinutes < 5;

        /// <summary>
        /// Vrai quand les écrans sont éteints par Windows, pendant la veille, ou juste après le réveil :
        /// les pertes / retours d'écran sont alors attendus (veille profonde DisplayPort) et ne sont pas des incidents.
        /// </summary>
        public bool InDisplayQuietPeriod =>
            _suspended || DisplayPowerState == 0 || (DateTime.Now - _lastDisplayOn).TotalSeconds < 20 || (DateTime.Now - _lastResume).TotalSeconds < 30;

        public SystemMessageWindow()
        {
            CreateHandle(new CreateParams { Caption = "SignalWatchdogs.HiddenMessageWindow", ExStyle = 0x80 /* WS_EX_TOOLWINDOW */ });
            int okDev = 0;
            foreach (var g in new[]
            {
                NativeMethods.GUID_DEVINTERFACE_MONITOR, NativeMethods.GUID_DEVINTERFACE_DISPLAY_ADAPTER,
                NativeMethods.GUID_DEVINTERFACE_USB_DEVICE, NativeMethods.GUID_DEVINTERFACE_USB_HUB,
                NativeMethods.GUID_DEVINTERFACE_USB_HOST_CONTROLLER
            })
            {
                var filter = new DEV_BROADCAST_DEVICEINTERFACE
                {
                    dbcc_size = Marshal.SizeOf(typeof(DEV_BROADCAST_DEVICEINTERFACE)),
                    dbcc_devicetype = NativeMethods.DBT_DEVTYP_DEVICEINTERFACE,
                    dbcc_classguid = g
                };
                var h = NativeMethods.RegisterDeviceNotification(Handle, ref filter, NativeMethods.DEVICE_NOTIFY_WINDOW_HANDLE);
                if (h != IntPtr.Zero) { _deviceNotifs.Add(h); okDev++; }
            }
            Hub.SetSource("Notifications PnP (écrans, GPU, USB)", okDev > 0, $"{okDev}/5 classes d'interface enregistrées");

            int okPow = 0;
            foreach (var g in new[] { NativeMethods.GUID_CONSOLE_DISPLAY_STATE, NativeMethods.GUID_LIDSWITCH_STATE_CHANGE, NativeMethods.GUID_ACDC_POWER_SOURCE })
            {
                var guid = g;
                var h = NativeMethods.RegisterPowerSettingNotification(Handle, ref guid, NativeMethods.DEVICE_NOTIFY_WINDOW_HANDLE);
                if (h != IntPtr.Zero) { _powerNotifs.Add(h); okPow++; }
            }
            Hub.SetSource("Notifications alimentation / extinction écran", okPow > 0, $"{okPow}/3 paramètres d'alimentation suivis");

            bool wts = NativeMethods.WTSRegisterSessionNotification(Handle, 1 /* NOTIFY_FOR_ALL_SESSIONS */);
            Hub.SetSource("Notifications de session (RDP / console)", wts, wts ? "Toutes les sessions" : "Enregistrement refusé");
        }

        public bool RegisterHotkey(string text)
        {
            if (HotkeyRegistered) NativeMethods.UnregisterHotKey(Handle, HotkeyId);
            HotkeyRegistered = false;
            HotkeyText = text;
            if (!TryParseHotkey(text, out uint mods, out uint vk)) return false;
            HotkeyRegistered = NativeMethods.RegisterHotKey(Handle, HotkeyId, mods | 0x4000 /* MOD_NOREPEAT */, vk);
            Hub.SetSource("Raccourci de signalement", HotkeyRegistered, HotkeyRegistered ? text : $"{text} déjà utilisé par une autre application");
            return HotkeyRegistered;
        }

        public static bool TryParseHotkey(string text, out uint mods, out uint vk)
        {
            mods = 0; vk = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            foreach (var raw in text.Split('+'))
            {
                var p = raw.Trim().ToLowerInvariant();
                switch (p)
                {
                    case "ctrl": case "control": case "ctl": mods |= 2; break;
                    case "alt": mods |= 1; break;
                    case "shift": case "maj": mods |= 4; break;
                    case "win": mods |= 8; break;
                    default:
                        if (Enum.TryParse(raw.Trim(), true, out Keys k)) vk = (uint)k;
                        else return false;
                        break;
                }
            }
            return vk != 0 && mods != 0;
        }

        protected override void WndProc(ref Message m)
        {
            try { Dispatch(ref m); } catch { }
            base.WndProc(ref m);
        }

        void Dispatch(ref Message m)
        {
            if (m.Msg == (int)ShowMessage && ShowMessage != 0) { ShowRequested?.Invoke(); return; }
            switch (m.Msg)
            {
                case NativeMethods.WM_DISPLAYCHANGE:
                {
                    long lp = m.LParam.ToInt64();
                    int w = (int)(lp & 0xFFFF), h = (int)((lp >> 16) & 0xFFFF);
                    Hub.Publish(Severity.Info, EventCategory.Display, Codes.WmDisplayChange, "Windows signale un changement d'affichage",
                        $"WM_DISPLAYCHANGE : écran principal {w}×{h}, {m.WParam.ToInt64()} bits", source: Src);
                    DisplayChanged?.Invoke();
                    break;
                }
                case NativeMethods.WM_DEVICECHANGE:
                    OnDeviceChange(m.WParam.ToInt32(), m.LParam);
                    break;
                case NativeMethods.WM_POWERBROADCAST:
                    OnPower(m.WParam.ToInt32(), m.LParam);
                    m.Result = new IntPtr(1);
                    break;
                case NativeMethods.WM_WTSSESSION_CHANGE:
                    OnSession(m.WParam.ToInt32(), m.LParam.ToInt32());
                    break;
                case NativeMethods.WM_HOTKEY:
                    if (m.WParam.ToInt32() == HotkeyId) HotkeyPressed?.Invoke();
                    break;
            }
        }

        void OnDeviceChange(int evt, IntPtr lParam)
        {
            if (evt == NativeMethods.DBT_DEVNODES_CHANGED) { DevNodesChanged?.Invoke(); return; }
            if (evt != NativeMethods.DBT_DEVICEARRIVAL && evt != NativeMethods.DBT_DEVICEREMOVECOMPLETE) return;
            if (lParam == IntPtr.Zero) return;
            int size = Marshal.ReadInt32(lParam, 0);
            int devType = Marshal.ReadInt32(lParam, 4);
            if (devType != NativeMethods.DBT_DEVTYP_DEVICEINTERFACE || size < 32 || size > 4096) return;
            var hdr = (DEV_BROADCAST_DEVICEINTERFACE)Marshal.PtrToStructure(lParam, typeof(DEV_BROADCAST_DEVICEINTERFACE));
            // Lecture bornée par la taille déclarée (pas de lecture au-delà du tampon).
            string path = Marshal.PtrToStringUni(lParam + 28, (size - 28) / 2);
            int nul = path.IndexOf('\0');
            if (nul >= 0) path = path.Substring(0, nul);
            bool arrival = evt == NativeMethods.DBT_DEVICEARRIVAL;
            var guid = hdr.dbcc_classguid;
            string inst = NativeMethods.InterfacePathToInstanceId(path);

            if (guid == NativeMethods.GUID_DEVINTERFACE_MONITOR)
            {
                Hub.Publish(new WatchEvent(arrival ? Severity.Info : Severity.Notice, EventCategory.Display,
                    arrival ? Codes.MonitorPnpArrival : Codes.MonitorPnpRemoval,
                    arrival ? "Moniteur branché (signal Hot-Plug)" : "Moniteur débranché (signal Hot-Plug perdu)", inst).With("instance", inst), Src);
                DisplayChanged?.Invoke();
            }
            else if (guid == NativeMethods.GUID_DEVINTERFACE_DISPLAY_ADAPTER)
            {
                var name = arrival ? CfgMgr.Get(inst, false)?.Name : null;
                name ??= inst;
                bool isVirtual = (name ?? "").ToLowerInvariant().Contains("virtual") || inst.StartsWith("ROOT\\") || inst.StartsWith("SWD\\");
                var e = new WatchEvent(isVirtual ? Severity.Warning : (arrival ? Severity.Warning : Severity.Critical),
                        isVirtual ? EventCategory.Display : EventCategory.Gpu,
                        isVirtual ? Codes.VirtualDisplay : Codes.GpuAdapterPnp,
                        (arrival ? "Adaptateur graphique (ré)apparu : " : "Adaptateur graphique retiré : ") + name,
                        arrival ? "Le pilote de la carte vient de (re)démarrer (mise à jour, redémarrage du pilote ou écran virtuel)." : "La carte graphique a disparu du système (pilote arrêté / désactivé / désinstallé).",
                        trigger: true).With("instance", inst).With("gpu", name);
                Hub.Publish(e, Src);
                DisplayChanged?.Invoke();
            }
            else
            {
                DeviceInterfaceChanged?.Invoke(guid, inst, arrival);
            }
        }

        void OnPower(int evt, IntPtr lParam)
        {
            switch (evt)
            {
                case NativeMethods.PBT_APMSUSPEND:
                    _suspendedAt = DateTime.Now;
                    Hub.Publish(Severity.Notice, EventCategory.Power, Codes.Suspend, "Mise en veille du système", source: Src);
                    Suspending?.Invoke();
                    break;
                case NativeMethods.PBT_APMRESUMEAUTOMATIC:
                    _suspendedAt = DateTime.MinValue;
                    _lastResume = DateTime.Now;
                    Hub.Publish(Severity.Notice, EventCategory.Power, Codes.Resume, "Sortie de veille du système", source: Src);
                    Resumed?.Invoke();
                    break;
                case NativeMethods.PBT_POWERSETTINGCHANGE:
                    if (lParam == IntPtr.Zero) return;
                    var g = (Guid)Marshal.PtrToStructure(lParam, typeof(Guid));
                    int len = Marshal.ReadInt32(lParam, 16);
                    int val = len >= 4 ? Marshal.ReadInt32(lParam, 20) : Marshal.ReadByte(lParam, 20);
                    if (g == NativeMethods.GUID_CONSOLE_DISPLAY_STATE)
                    {
                        if (val == 1 && DisplayPowerState != 1) _lastDisplayOn = DateTime.Now;
                        DisplayPowerState = val;
                        if (_firstDisplayState) { _firstDisplayState = false; return; }
                        if (val == 0)
                            Hub.Publish(Severity.Notice, EventCategory.Power, Codes.DisplayPowerOff, "Écrans éteints par Windows (gestion d'alimentation)",
                                $"Inactivité clavier/souris : {Fr.Duration(NativeMethods.IdleTime)}", source: Src);
                        else if (val == 1)
                            Hub.Publish(Severity.Info, EventCategory.Power, Codes.DisplayPowerOn, "Écrans rallumés par Windows", source: Src);
                        else if (val == 2)
                            Hub.Publish(Severity.Info, EventCategory.Power, Codes.DisplayDimmed, "Écrans atténués par Windows", source: Src);
                    }
                    else if (g == NativeMethods.GUID_LIDSWITCH_STATE_CHANGE)
                    {
                        if (_firstLid) { _firstLid = false; return; }
                        Hub.Publish(Severity.Notice, EventCategory.Power, Codes.Lid, val == 0 ? "Capot du portable fermé" : "Capot du portable ouvert", source: Src);
                    }
                    else if (g == NativeMethods.GUID_ACDC_POWER_SOURCE)
                    {
                        if (_firstAcdc) { _firstAcdc = false; return; }
                        Hub.Publish(Severity.Info, EventCategory.Power, Codes.PowerSource,
                            val == 0 ? "Alimentation secteur" : val == 1 ? "Passage sur batterie" : "Alimentation de courte durée (onduleur)", source: Src);
                    }
                    break;
            }
        }

        void OnSession(int code, int sessionId)
        {
            string own = sessionId == _ownSession ? " (cette session)" : $" (session {sessionId})";
            (Severity sev, string c, string title) = code switch
            {
                1 => (Severity.Notice, Codes.ConsoleConnect, "Session reconnectée à la console (écrans physiques)"),
                2 => (Severity.Notice, Codes.ConsoleDisconnect, "Session déconnectée de la console (écrans physiques)"),
                3 => (Severity.Notice, Codes.RemoteConnect, "Connexion Bureau à distance (RDP) ouverte"),
                4 => (Severity.Notice, Codes.RemoteDisconnect, "Connexion Bureau à distance (RDP) fermée"),
                5 => (Severity.Info, Codes.Logon, "Ouverture de session"),
                6 => (Severity.Info, Codes.Logoff, "Fermeture de session"),
                7 => (Severity.Info, Codes.SessionLock, "Session verrouillée"),
                8 => (Severity.Info, Codes.SessionUnlock, "Session déverrouillée"),
                9 => (Severity.Notice, Codes.RemoteConnect, "Contrôle à distance de la session"),
                _ => (Severity.Info, Codes.WatchdogInfo, $"Changement de session ({code})")
            };
            Hub.Publish(new WatchEvent(sev, EventCategory.Session, c, title + own, null).With("session", sessionId), Src);
        }

        public void Dispose()
        {
            foreach (var h in _deviceNotifs) NativeMethods.UnregisterDeviceNotification(h);
            foreach (var h in _powerNotifs) NativeMethods.UnregisterPowerSettingNotification(h);
            _deviceNotifs.Clear();
            _powerNotifs.Clear();
            if (HotkeyRegistered) NativeMethods.UnregisterHotKey(Handle, HotkeyId);
            try { NativeMethods.WTSUnRegisterSessionNotification(Handle); } catch { }
            DestroyHandle();
        }
    }
}
