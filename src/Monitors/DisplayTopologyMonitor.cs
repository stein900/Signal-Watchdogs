using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Threading;
using SignalWatchdogs.Core;
using SignalWatchdogs.Native;

namespace SignalWatchdogs.Monitors
{
    public sealed class MonitorInfo
    {
        public string Key;
        public string FriendlyName;
        public string DevicePath;
        public string InstanceId;
        public string GdiName;
        public LUID AdapterLuid;
        public string AdapterName;
        public uint TargetId;
        public uint OutputTech;
        public string Connector;
        public bool IsVirtual;
        public bool IsInternal;
        public int Width, Height, X, Y;
        public double RefreshHz;
        public uint Rotation;
        public bool Primary;
        public string EdidMfg;
        public ushort EdidProduct;
        public string Serial;
        public string Year;
        public uint ConnectorInstance;

        public string ShortGdi => (GdiName ?? "").Replace(@"\\.\", "");
        public string Name => !string.IsNullOrEmpty(FriendlyName) ? FriendlyName : IsInternal ? "Écran interne" : IsVirtual ? "Écran virtuel" : $"Écran #{TargetId}";
        public string Label => $"{Name} ({ShortGdi})";
        public string ModeText => $"{Width}×{Height} @ {RefreshHz.ToString("0.##", CultureInfo.InvariantCulture)} Hz";

        public string Signature => $"{Width}x{Height}@{Math.Round(RefreshHz, 1)}|{X},{Y}|r{Rotation}|p{Primary}";
    }

    public sealed class MonitorStats
    {
        public string Key;
        public string Label;
        public string Connector;
        public string Gpu;
        public int Disconnects;
        public int ModeChanges;
        public TimeSpan Downtime;
        public DateTime? LastLost;
        public DateTime? LastBack;
        public bool CurrentlyLost;
    }

    /// <summary>
    /// Surveille la topologie d'affichage (QueryDisplayConfig) à haute fréquence :
    /// détecte la perte / le retour de chaque écran, mesure la durée du noir,
    /// les changements de résolution / fréquence / écran principal.
    /// </summary>
    public sealed class DisplayTopologyMonitor : IDisposable
    {
        const string Src = "Topologie d'affichage";
        readonly int _pollMs;
        readonly Thread _thread;
        readonly AutoResetEvent _wake = new AutoResetEvent(false);
        volatile bool _stop;

        readonly Dictionary<string, (MonitorInfo info, DateTime at)> _lost = new Dictionary<string, (MonitorInfo, DateTime)>();
        readonly Dictionary<string, MonitorStats> _stats = new Dictionary<string, MonitorStats>();
        readonly Dictionary<string, string> _adapterNames = new Dictionary<string, string>();
        readonly Dictionary<string, (string serial, string year, string mfg)> _wmiCache = new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, MonitorInfo> _current;
        bool _accessible = true;
        int _consecutiveErrors;

        public volatile List<MonitorInfo> Current = new List<MonitorInfo>();
        /// <summary>Écrans actuellement perdus avec l'heure de la perte (pour l'affichage « fantôme »).</summary>
        public volatile List<(MonitorInfo info, DateTime at)> LostNow = new List<(MonitorInfo, DateTime)>();
        public DateTime LastPoll { get; private set; }
        public int PollCount { get; private set; }
        public bool Accessible => _accessible;
        public event Action TopologyChanged;
        /// <summary>Période où les pertes d'écran sont attendues (écrans éteints, veille, réveil).</summary>
        public Func<bool> QuietPeriod;

        bool SafeQuiet()
        {
            try { return QuietPeriod?.Invoke() ?? false; } catch { return false; }
        }

        public DisplayTopologyMonitor(int pollMs)
        {
            _pollMs = pollMs;
            _thread = new Thread(Run) { IsBackground = true, Name = "SignalWatchdogs.Topology", Priority = ThreadPriority.AboveNormal };
        }

        public void Start() => _thread.Start();

        /// <summary>Force une relecture immédiate (WM_DISPLAYCHANGE, arrivée/retrait PnP).</summary>
        public void Poke() => _wake.Set();

        public List<MonitorStats> Stats()
        {
            lock (_stats) return _stats.Values.Select(s => (MonitorStats)MemberwiseCloneStats(s)).ToList();
        }

        static MonitorStats MemberwiseCloneStats(MonitorStats s) => new MonitorStats
        {
            Key = s.Key, Label = s.Label, Connector = s.Connector, Gpu = s.Gpu, Disconnects = s.Disconnects, ModeChanges = s.ModeChanges,
            Downtime = s.Downtime, LastLost = s.LastLost, LastBack = s.LastBack, CurrentlyLost = s.CurrentlyLost
        };

        void Run()
        {
            try
            {
                var first = Query(out int err);
                if (first == null)
                {
                    Hub.SetSource(Src, false, $"QueryDisplayConfig a échoué (code {err})");
                    first = new Dictionary<string, MonitorInfo>();
                }
                else Hub.SetSource(Src, true, $"Interrogation toutes les {_pollMs} ms");
                _current = first;
                Current = first.Values.OrderBy(m => m.X).ThenBy(m => m.Y).ToList();
                lock (_stats) foreach (var m in first.Values) StatFor(m);
                Hub.Publish(Severity.Info, EventCategory.Display, Codes.WatchdogInfo,
                    $"Topologie initiale : {first.Count} écran(s) actif(s)", Describe(first.Values), source: Src);
                TopologyChanged?.Invoke();
            }
            catch (Exception ex) { Hub.SetSource(Src, false, ex.Message); }

            while (!_stop)
            {
                _wake.WaitOne(_pollMs);
                if (_stop) break;
                try { Poll(); }
                catch (Exception ex) { Hub.SetSource(Src, false, "Erreur : " + ex.Message); }
            }
        }

        void Poll()
        {
            var snap = Query(out int err);
            LastPoll = DateTime.Now;
            PollCount++;
            if (snap == null)
            {
                _consecutiveErrors++;
                if (_accessible && _consecutiveErrors >= 3)
                {
                    _accessible = false;
                    Hub.SetSource(Src, false, $"Topologie inaccessible (code {err}) — session probablement déconnectée de la console");
                    Hub.Publish(Severity.Notice, EventCategory.Display, Codes.DisplayTopoError,
                        "Topologie d'affichage inaccessible", $"QueryDisplayConfig code {err} : la session n'est probablement plus attachée à la console (RDP, changement d'utilisateur).", source: Src);
                }
                return;
            }
            _consecutiveErrors = 0;
            bool recovering = !_accessible;
            if (recovering)
            {
                _accessible = true;
                Hub.SetSource(Src, true, $"Interrogation toutes les {_pollMs} ms");
                Hub.Publish(Severity.Notice, EventCategory.Display, Codes.WatchdogInfo, "Topologie d'affichage de nouveau accessible", Describe(snap.Values), source: Src);
            }
            Diff(_current, snap, recovering);
            _current = snap;
        }

        void Diff(Dictionary<string, MonitorInfo> prev, Dictionary<string, MonitorInfo> cur, bool afterGap)
        {
            var now = DateTime.Now;
            bool remote = NativeMethods.IsRemoteSession;
            bool quiet = SafeQuiet();
            bool trig = !afterGap && !remote && !quiet;
            string note = remote ? " [session distante RDP : écrans virtuels du client]" : afterGap ? " [constaté au retour de la session]"
                        : quiet ? " [écrans en veille ou au réveil : comportement attendu]" : "";
            bool changed = false;

            var removed = prev.Keys.Where(k => !cur.ContainsKey(k)).ToList();
            var added = cur.Keys.Where(k => !prev.ContainsKey(k)).ToList();

            foreach (var k in removed)
            {
                var m = prev[k];
                int adapterTotal = prev.Values.Count(x => x.AdapterLuid.LowPart == m.AdapterLuid.LowPart && x.AdapterLuid.HighPart == m.AdapterLuid.HighPart);
                _lost[k] = (m, now);
                lock (_stats)
                {
                    var st = StatFor(m);
                    st.Disconnects++;
                    st.LastLost = now;
                    st.CurrentlyLost = true;
                }
                var e = new WatchEvent(Severity.Error, EventCategory.Display, Codes.DisplayLost,
                        $"Écran perdu : {m.Label}",
                        $"{m.Connector} · {m.ModeText} · GPU : {m.AdapterName}{note}", trig)
                    .With("monitor", m.Label).With("connector", m.Connector).With("gpu", m.AdapterName)
                    .With("adapter_total", adapterTotal).With("active_before", prev.Count).With("virtual", m.IsVirtual ? "1" : "0")
                    .With("serial", m.Serial).With("key", k);
                if (m.IsVirtual || quiet) e.Severity = Severity.Notice;
                Hub.Publish(e, Src);
                changed = true;
            }

            foreach (var k in added)
            {
                var m = cur[k];
                WatchEvent e;
                if (_lost.TryGetValue(k, out var lostInfo))
                {
                    _lost.Remove(k);
                    var outage = now - lostInfo.at;
                    bool shortGlitch = outage.TotalMinutes < 5;
                    lock (_stats)
                    {
                        var st = StatFor(m);
                        st.Downtime += outage;
                        st.LastBack = now;
                        st.CurrentlyLost = false;
                    }
                    e = new WatchEvent(shortGlitch ? Severity.Warning : Severity.Notice, EventCategory.Display, Codes.DisplayBack,
                            $"Écran revenu : {m.Label} après {Fr.Duration(outage)}",
                            $"{m.Connector} · {m.ModeText} · GPU : {m.AdapterName}{note}", trig && shortGlitch)
                        .With("outage_ms", (long)outage.TotalMilliseconds);
                }
                else
                {
                    lock (_stats) StatFor(m).CurrentlyLost = false;
                    e = new WatchEvent(Severity.Notice, EventCategory.Display, Codes.DisplayNew,
                        $"Nouvel écran détecté : {m.Label}", $"{m.Connector} · {m.ModeText} · GPU : {m.AdapterName}{note}", false);
                }
                e.With("monitor", m.Label).With("connector", m.Connector).With("gpu", m.AdapterName).With("virtual", m.IsVirtual ? "1" : "0").With("serial", m.Serial).With("key", k);
                if (m.IsVirtual)
                    Hub.Publish(new WatchEvent(Severity.Warning, EventCategory.Display, Codes.VirtualDisplay, $"Écran virtuel activé : {m.Label}", $"Adaptateur : {m.AdapterName}", trig).With("monitor", m.Label), Src);
                Hub.Publish(e, Src);
                changed = true;
            }

            bool structural = removed.Count > 0 || added.Count > 0;
            foreach (var k in cur.Keys.Where(prev.ContainsKey))
            {
                var a = prev[k];
                var b = cur[k];
                if (a.Signature == b.Signature) continue;
                changed = true;
                var diffs = new List<string>();
                bool modeChanged = false;
                if (a.Width != b.Width || a.Height != b.Height) { diffs.Add($"résolution {a.Width}×{a.Height} → {b.Width}×{b.Height}"); modeChanged = true; }
                if (Math.Abs(a.RefreshHz - b.RefreshHz) > 0.5) { diffs.Add($"fréquence {a.RefreshHz:0.#} → {b.RefreshHz:0.#} Hz"); modeChanged = true; }
                if (a.Rotation != b.Rotation) { diffs.Add("rotation"); modeChanged = true; }
                if (a.Primary != b.Primary && b.Primary) diffs.Add("devient l'écran principal");
                if (a.X != b.X || a.Y != b.Y) diffs.Add($"position ({a.X},{a.Y}) → ({b.X},{b.Y})");
                if (modeChanged) lock (_stats) StatFor(b).ModeChanges++;
                var sev = modeChanged ? Severity.Warning : Severity.Info;
                var code = modeChanged ? Codes.DisplayMode : (a.Primary != b.Primary ? Codes.DisplayPrimary : Codes.WatchdogInfo);
                // Un simple déplacement consécutif à un retrait n'est pas une anomalie en soi.
                bool isTrigger = trig && modeChanged;
                Hub.Publish(new WatchEvent(sev, EventCategory.Display, code, $"Mode modifié : {b.Label}", string.Join(", ", diffs) + note, isTrigger)
                    .With("monitor", b.Label).With("connector", b.Connector).With("gpu", b.AdapterName), Src);
            }

            if (structural)
            {
                Hub.Publish(Severity.Info, EventCategory.Display, Codes.WatchdogInfo,
                    $"Topologie : {prev.Count} → {cur.Count} écran(s)", Describe(cur.Values), source: Src);
            }

            if (changed)
            {
                LostNow = _lost.Values.ToList();
                Current = cur.Values.OrderBy(m => m.X).ThenBy(m => m.Y).ToList();
                try { TopologyChanged?.Invoke(); } catch { }
            }
        }

        MonitorStats StatFor(MonitorInfo m)
        {
            if (!_stats.TryGetValue(m.Key, out var st)) _stats[m.Key] = st = new MonitorStats { Key = m.Key };
            st.Label = m.Label;
            st.Connector = m.Connector;
            st.Gpu = m.AdapterName;
            return st;
        }

        public static string Describe(IEnumerable<MonitorInfo> list)
        {
            var items = list.OrderBy(m => m.X).Select(m => $"{m.Label} — {m.Connector}, {m.ModeText}, GPU {m.AdapterName}{(m.Primary ? ", principal" : "")}").ToList();
            return items.Count == 0 ? "Aucun écran actif" : string.Join(" | ", items);
        }

        Dictionary<string, MonitorInfo> Query(out int error)
        {
            error = 0;
            DISPLAYCONFIG_PATH_INFO[] paths = null;
            DISPLAYCONFIG_MODE_INFO[] modes = null;
            uint np = 0, nm = 0;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                error = NativeMethods.GetDisplayConfigBufferSizes(NativeMethods.QDC_ONLY_ACTIVE_PATHS, out np, out nm);
                if (error != 0) return null;
                paths = new DISPLAYCONFIG_PATH_INFO[np];
                modes = new DISPLAYCONFIG_MODE_INFO[nm];
                error = NativeMethods.QueryDisplayConfig(NativeMethods.QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero);
                if (error == NativeMethods.ERROR_INSUFFICIENT_BUFFER) continue;
                break;
            }
            if (error != 0) return null;

            var res = new Dictionary<string, MonitorInfo>();
            for (int i = 0; i < np; i++)
            {
                var p = paths[i];
                if ((p.flags & 1) == 0) continue; // DISPLAYCONFIG_PATH_ACTIVE
                var m = new MonitorInfo
                {
                    AdapterLuid = p.targetInfo.adapterId,
                    TargetId = p.targetInfo.id,
                    OutputTech = p.targetInfo.outputTechnology,
                    Rotation = p.targetInfo.rotation,
                };
                if (p.targetInfo.refreshRate.Denominator != 0)
                    m.RefreshHz = (double)p.targetInfo.refreshRate.Numerator / p.targetInfo.refreshRate.Denominator;

                if (p.sourceInfo.modeInfoIdx < nm && modes[p.sourceInfo.modeInfoIdx].infoType == 1)
                {
                    var sm = modes[p.sourceInfo.modeInfoIdx].sourceMode;
                    m.Width = (int)sm.width;
                    m.Height = (int)sm.height;
                    m.X = sm.positionX;
                    m.Y = sm.positionY;
                    m.Primary = sm.positionX == 0 && sm.positionY == 0;
                }
                if (p.targetInfo.modeInfoIdx < nm && modes[p.targetInfo.modeInfoIdx].infoType == 2)
                {
                    var vs = modes[p.targetInfo.modeInfoIdx].targetMode.vSyncFreq;
                    if (vs.Denominator != 0) m.RefreshHz = (double)vs.Numerator / vs.Denominator;
                }

                var tn = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                tn.header.type = NativeMethods.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                tn.header.size = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(DISPLAYCONFIG_TARGET_DEVICE_NAME));
                tn.header.adapterId = p.targetInfo.adapterId;
                tn.header.id = p.targetInfo.id;
                if (NativeMethods.DisplayConfigGetDeviceInfo(ref tn) == 0)
                {
                    m.FriendlyName = tn.monitorFriendlyDeviceName;
                    m.DevicePath = tn.monitorDevicePath;
                    m.EdidProduct = tn.edidProductCodeId;
                    m.EdidMfg = DecodePnpId(tn.edidManufactureId);
                    m.ConnectorInstance = tn.connectorInstance;
                    if (tn.outputTechnology != 0 || m.OutputTech == 0) m.OutputTech = tn.outputTechnology;
                }

                var sn = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                sn.header.type = NativeMethods.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                sn.header.size = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME));
                sn.header.adapterId = p.sourceInfo.adapterId;
                sn.header.id = p.sourceInfo.id;
                if (NativeMethods.DisplayConfigGetDeviceInfo(ref sn) == 0) m.GdiName = sn.viewGdiDeviceName;

                m.AdapterName = AdapterName(p.targetInfo.adapterId);
                m.Connector = ConnectorName(m.OutputTech);
                m.IsInternal = m.OutputTech == 6 || m.OutputTech == 11 || m.OutputTech == 13 || m.OutputTech == 0x80000000;
                var an = (m.AdapterName ?? "").ToLowerInvariant();
                m.IsVirtual = m.OutputTech == 17 || an.Contains("virtual") || an.Contains("parsec") || an.Contains("remote display") || an.Contains("idd") || an.Contains("spacedesk");
                if (m.OutputTech == 0xFFFFFFFF && m.IsVirtual) m.Connector = "Virtuel";

                if (!string.IsNullOrEmpty(m.DevicePath))
                {
                    m.InstanceId = NativeMethods.InterfacePathToInstanceId(m.DevicePath);
                    m.Key = m.DevicePath.ToLowerInvariant();
                    FillWmi(m);
                }
                else m.Key = $"{p.targetInfo.adapterId}:{p.targetInfo.id}";

                if (!res.ContainsKey(m.Key)) res[m.Key] = m;
            }
            return res;
        }

        string AdapterName(LUID luid)
        {
            var key = luid.ToString();
            lock (_adapterNames)
            {
                if (_adapterNames.TryGetValue(key, out var n)) return n;
                string name = null;
                try
                {
                    var an = new DISPLAYCONFIG_ADAPTER_NAME();
                    an.header.type = NativeMethods.DISPLAYCONFIG_DEVICE_INFO_GET_ADAPTER_NAME;
                    an.header.size = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(DISPLAYCONFIG_ADAPTER_NAME));
                    an.header.adapterId = luid;
                    if (NativeMethods.DisplayConfigGetDeviceInfo(ref an) == 0)
                    {
                        var id = NativeMethods.InterfacePathToInstanceId(an.adapterDevicePath);
                        name = CfgMgr.Get(id, false)?.Name;
                    }
                }
                catch { }
                name ??= "GPU " + key;
                _adapterNames[key] = name;
                return name;
            }
        }

        void FillWmi(MonitorInfo m)
        {
            var prefix = m.InstanceId;
            if (_wmiCache.TryGetValue(prefix, out var c))
            {
                m.Serial = c.serial; m.Year = c.year;
                if (string.IsNullOrEmpty(m.EdidMfg)) m.EdidMfg = c.mfg;
                return;
            }
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, ManufacturerName, SerialNumberID, YearOfManufacture FROM WmiMonitorID");
                foreach (ManagementObject mo in searcher.Get())
                {
                    var inst = (mo["InstanceName"] as string ?? "").ToUpperInvariant();
                    var serial = U16(mo["SerialNumberID"]);
                    var mfg = U16(mo["ManufacturerName"]);
                    var year = Convert.ToString(mo["YearOfManufacture"], CultureInfo.InvariantCulture);
                    int us = inst.LastIndexOf('_');
                    var baseId = us > 0 ? inst.Substring(0, us) : inst;
                    _wmiCache[baseId] = (serial, year, mfg);
                }
            }
            catch { }
            if (!_wmiCache.ContainsKey(prefix)) _wmiCache[prefix] = (null, null, null);
            c = _wmiCache[prefix];
            m.Serial = c.serial; m.Year = c.year;
            if (string.IsNullOrEmpty(m.EdidMfg)) m.EdidMfg = c.mfg;
        }

        static string U16(object o)
        {
            if (o is ushort[] arr) return new string(arr.Where(x => x != 0).Select(x => (char)x).ToArray()).Trim();
            return null;
        }

        static string DecodePnpId(ushort raw)
        {
            foreach (var v in new[] { (ushort)((raw >> 8) | (raw << 8)), raw })
            {
                char a = (char)('A' - 1 + ((v >> 10) & 0x1F));
                char b = (char)('A' - 1 + ((v >> 5) & 0x1F));
                char c = (char)('A' - 1 + (v & 0x1F));
                if (char.IsUpper(a) && char.IsUpper(b) && char.IsUpper(c)) return new string(new[] { a, b, c });
            }
            return null;
        }

        public static string ConnectorName(uint t) => t switch
        {
            0xFFFFFFFF => "Autre",
            0 => "VGA",
            1 => "S-Vidéo",
            2 => "Composite",
            3 => "Composante",
            4 => "DVI",
            5 => "HDMI",
            6 => "LVDS (interne)",
            8 => "D-JPN",
            9 => "SDI",
            10 => "DisplayPort",
            11 => "eDP (interne)",
            12 => "UDI",
            13 => "UDI (interne)",
            14 => "SDTV",
            15 => "Miracast",
            16 => "Indirect USB (DisplayLink)",
            17 => "Indirect virtuel",
            18 => "DP via USB-C/USB4",
            0x80000000 => "Interne",
            _ => $"Type {t}"
        };

        public void Dispose()
        {
            _stop = true;
            _wake.Set();
        }
    }
}
