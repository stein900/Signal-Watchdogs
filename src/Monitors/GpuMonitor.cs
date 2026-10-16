using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using SignalWatchdogs.Core;
using SignalWatchdogs.Native;

namespace SignalWatchdogs.Monitors
{
    public sealed class NvidiaSample
    {
        public int Index;
        public string BusId;
        public string Name;
        public double? Temp, Util, EncUtil, DecUtil, Power, ClockGr, ClockMem, PcieGen, PcieWidth, PcieWidthMax, Fan, MemUsedMb, MemTotalMb;
        public string Driver, PState, Throttle;
        public DateTime Time = DateTime.Now;
    }

    public sealed class GpuAdapterState
    {
        public string Key;
        public string Name;
        public string Vendor;
        public LUID Luid;
        public bool HasLuid;
        public string PciKey;
        public ulong VramTotal;
        public bool Software;
        public bool IsVirtual;
        public int Outputs;
        public string DriverVersion;
        public DateTime? DriverDate;
        public string PnpId;
        public string Status;
        public int ConfigError;
        public double Util, Util3D, UtilCopy, UtilDecode, UtilEncode, VramUsed, SharedUsed;
        public NvidiaSample Nv;
        public readonly TimeSeries UtilHistory = new TimeSeries("Charge");
        public readonly TimeSeries VramHistory = new TimeSeries("VRAM");
        public readonly TimeSeries TempHistory = new TimeSeries("Température");
        public readonly TimeSeries EncodeHistory = new TimeSeries("Encodeur");

        public int DriverAgeDays => DriverDate.HasValue ? (int)(DateTime.Now - DriverDate.Value).TotalDays : -1;
    }

    /// <summary>
    /// Surveille les cartes graphiques : inventaire (DXGI + WMI), charge par moteur (PDH),
    /// mémoire vidéo, encodeur vidéo (streaming / partage d'écran), télémétrie NVIDIA (nvidia-smi).
    /// </summary>
    public sealed class GpuMonitor : IDisposable
    {
        const string Src = "Cartes graphiques";
        readonly Settings _settings;
        readonly Thread _thread;
        readonly AutoResetEvent _wake = new AutoResetEvent(false);
        volatile bool _stop;
        bool _refreshInventory = true;
        PdhQuery _pdh;
        string _nvSmiPath;
        bool _nvLost;
        bool _nvExtended = true;
        DateTime _lastNv = DateTime.MinValue, _lastInventory = DateTime.MinValue, _lastMetrics = DateTime.MinValue;
        readonly Dictionary<string, DateTime> _rateLimit = new Dictionary<string, DateTime>();
        readonly Dictionary<string, int> _encodeCounters = new Dictionary<string, int>();
        readonly HashSet<string> _encodeActive = new HashSet<string>();
        bool _pcieReported;

        public volatile List<GpuAdapterState> Adapters = new List<GpuAdapterState>();
        public readonly TimeSeries Cpu = new TimeSeries("CPU");
        public readonly TimeSeries Ram = new TimeSeries("RAM");
        public bool EncoderActive => _encodeActive.Count > 0;
        public bool NvidiaSmiAvailable => _nvSmiPath != null;

        public GpuMonitor(Settings settings)
        {
            _settings = settings;
            _thread = new Thread(Run) { IsBackground = true, Name = "SignalWatchdogs.Gpu", Priority = ThreadPriority.BelowNormal };
        }

        public void Start() => _thread.Start();

        public void RequestInventory() { _refreshInventory = true; _wake.Set(); }

        void Run()
        {
            try
            {
                _pdh = new PdhQuery();
                bool okEng = _pdh.Add("eng", @"\GPU Engine(*)\Utilization Percentage");
                bool okMem = _pdh.Add("ded", @"\GPU Adapter Memory(*)\Dedicated Usage");
                _pdh.Add("shr", @"\GPU Adapter Memory(*)\Shared Usage");
                _pdh.Add("cpu", @"\Processor(_Total)\% Processor Time");
                _pdh.Collect();
                Hub.SetSource("Compteurs GPU (PDH)", okEng, okEng ? "Charge par moteur 3D / copie / encodage / décodage" + (okMem ? " + VRAM" : "") : "Compteurs GPU indisponibles");
            }
            catch (Exception ex) { Hub.SetSource("Compteurs GPU (PDH)", false, ex.Message); }

            if (_settings.UseNvidiaSmi)
            {
                foreach (var p in new[] { Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"), @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe" })
                    if (File.Exists(p)) { _nvSmiPath = p; break; }
                Hub.SetSource("Télémétrie NVIDIA (nvidia-smi)", _nvSmiPath != null, _nvSmiPath ?? "non installé (pas de carte NVIDIA ?)");
            }

            while (!_stop)
            {
                try
                {
                    if (_refreshInventory || (DateTime.Now - _lastInventory).TotalSeconds > 60) RefreshInventory();
                    Sample();
                    if (_nvSmiPath != null && (DateTime.Now - _lastNv).TotalMilliseconds >= Math.Max(5000, _settings.GpuSampleMs)) SampleNvidia();
                    if ((DateTime.Now - _lastMetrics).TotalSeconds >= 10) WriteMetrics();
                }
                catch (Exception ex) { Hub.SetSource(Src, false, "Erreur : " + ex.Message); }
                _wake.WaitOne(_settings.GpuSampleMs);
            }
            try { _pdh?.Dispose(); } catch { }
        }

        // ------------------------------------------------------------------
        void RefreshInventory()
        {
            _refreshInventory = false;
            _lastInventory = DateTime.Now;
            var old = Adapters;
            var list = new List<GpuAdapterState>();

            try
            {
                foreach (var a in Dxgi.EnumerateAdapters().Where(a => !a.Software))
                {
                    var key = a.PciKey + "|" + a.Name;
                    var st = old.FirstOrDefault(o => o.Key == key) ?? new GpuAdapterState { Key = key };
                    st.Name = a.Name; st.Vendor = a.Vendor; st.Luid = a.Luid; st.HasLuid = true; st.PciKey = a.PciKey;
                    st.VramTotal = a.DedicatedVideoMemory; st.Outputs = a.Outputs; st.Software = a.Software;
                    if (list.All(x => x.Key != key)) list.Add(st);
                }
            }
            catch (Exception ex) { Hub.SetSource("DXGI", false, ex.Message); }

            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name, PNPDeviceID, DriverVersion, DriverDate, Status, ConfigManagerErrorCode, AdapterRAM FROM Win32_VideoController");
                foreach (ManagementObject mo in searcher.Get())
                {
                    var pnp = (mo["PNPDeviceID"] as string ?? "").ToUpperInvariant();
                    var name = (mo["Name"] as string ?? "").Trim();
                    var st = list.FirstOrDefault(x => x.PnpId == null && pnp.Contains(x.PciKey ?? "§"));
                    if (st == null)
                    {
                        var key = "WMI|" + pnp;
                        st = old.FirstOrDefault(o => o.Key == key) ?? new GpuAdapterState { Key = key, Name = name, Vendor = VendorFromPnp(pnp) };
                        st.IsVirtual = pnp.StartsWith("ROOT\\") || pnp.StartsWith("SWD\\") || name.ToLowerInvariant().Contains("virtual");
                        if (list.All(x => x.Key != key)) list.Add(st);
                    }
                    st.PnpId = pnp;
                    var newVersion = mo["DriverVersion"] as string;
                    if (st.DriverVersion != null && newVersion != null && st.DriverVersion != newVersion)
                        Hub.Publish(new WatchEvent(Severity.Notice, EventCategory.Gpu, Codes.GpuDriverChanged, $"Pilote graphique modifié : {st.Name}", $"{st.DriverVersion} → {newVersion}").With("gpu", st.Name), Src);
                    st.DriverVersion = newVersion;
                    st.DriverDate = ParseCimDate(mo["DriverDate"] as string);
                    st.Status = mo["Status"] as string;
                    int err = Convert.ToInt32(mo["ConfigManagerErrorCode"] ?? 0);
                    if (err != st.ConfigError && err != 0 && err != 22)
                        Hub.Publish(new WatchEvent(Severity.Critical, EventCategory.Gpu, Codes.GpuAdapterLost, $"Carte graphique en erreur : {st.Name}", CfgMgr.ProblemText((uint)err), true).With("gpu", st.Name), Src);
                    st.ConfigError = err;
                }
            }
            catch (Exception ex) { Hub.SetSource("WMI Win32_VideoController", false, ex.Message); }

            // Disparition / apparition
            if (old.Count > 0)
            {
                foreach (var gone in old.Where(o => !o.IsVirtual && list.All(n => n.Key != o.Key)))
                    Hub.Publish(new WatchEvent(Severity.Critical, EventCategory.Gpu, Codes.GpuAdapterLost, $"Carte graphique disparue : {gone.Name}",
                        "La carte n'est plus énumérée par DirectX/WMI (pilote arrêté, désactivé ou décrochage matériel).", true).With("gpu", gone.Name), Src);
                foreach (var added in list.Where(n => old.All(o => o.Key != n.Key)))
                    Hub.Publish(new WatchEvent(added.IsVirtual ? Severity.Warning : Severity.Notice, added.IsVirtual ? EventCategory.Display : EventCategory.Gpu,
                        added.IsVirtual ? Codes.VirtualDisplay : Codes.GpuAdapterNew, $"Nouvelle carte graphique : {added.Name}", added.PnpId).With("gpu", added.Name), Src);
            }
            else
            {
                Hub.Publish(Severity.Info, EventCategory.Gpu, Codes.WatchdogInfo, $"{list.Count} carte(s) graphique(s) détectée(s)",
                    string.Join(" | ", list.Select(a => $"{a.Name} — pilote {a.DriverVersion} du {a.DriverDate:dd/MM/yyyy}{(a.VramTotal > 0 ? ", " + Fr.Bytes(a.VramTotal) : "")}")), source: Src);
            }

            Adapters = list;
            Hub.SetSource(Src, true, string.Join(", ", list.Select(a => a.Name)));
        }

        static string VendorFromPnp(string pnp) =>
            pnp.Contains("VEN_10DE") ? "NVIDIA" : pnp.Contains("VEN_1002") ? "AMD" : pnp.Contains("VEN_8086") ? "Intel" : pnp.StartsWith("ROOT") ? "Virtuel" : "?";

        static DateTime? ParseCimDate(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length < 8) return null;
            return DateTime.TryParseExact(s.Substring(0, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateTime?)null;
        }

        // ------------------------------------------------------------------
        static readonly Regex EngRx = new Regex(@"(luid_0x[0-9a-f]{8}_0x[0-9a-f]{8})_phys_\d+_eng_\d+_engtype_(.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        void Sample()
        {
            var now = DateTime.Now;
            Ram.Add(now, NativeMethods.MemoryLoad());
            if (_pdh == null || !_pdh.Collect()) return;

            var cpu = _pdh.Read("cpu");
            if (cpu.Count > 0) Cpu.Add(now, Math.Min(100, cpu.Values.First()));

            var eng = _pdh.Read("eng");
            var perLuid = new Dictionary<string, Dictionary<string, double>>();
            foreach (var kv in eng)
            {
                var m = EngRx.Match(kv.Key);
                if (!m.Success) continue;
                var luid = m.Groups[1].Value.ToLowerInvariant();
                var type = m.Groups[2].Value.ToLowerInvariant();
                if (!perLuid.TryGetValue(luid, out var d)) perLuid[luid] = d = new Dictionary<string, double>();
                d[type] = (d.TryGetValue(type, out var p) ? p : 0) + kv.Value;
            }
            var ded = _pdh.Read("ded");
            var shr = _pdh.Read("shr");

            foreach (var a in Adapters.Where(x => x.HasLuid))
            {
                var key = a.Luid.PdhKey;
                if (perLuid.TryGetValue(key, out var types))
                {
                    a.Util = Math.Min(100, types.Values.DefaultIfEmpty(0).Max());
                    a.Util3D = Math.Min(100, types.TryGetValue("3d", out var v3) ? v3 : 0);
                    a.UtilCopy = Math.Min(100, types.TryGetValue("copy", out var vc) ? vc : 0);
                    a.UtilDecode = Math.Min(100, types.Where(t => t.Key.Contains("decode")).Sum(t => t.Value));
                    a.UtilEncode = Math.Min(100, types.Where(t => t.Key.Contains("encode")).Sum(t => t.Value));
                }
                else { a.Util = a.Util3D = a.UtilCopy = a.UtilDecode = a.UtilEncode = 0; }
                a.VramUsed = ded.Where(k => k.Key.ToLowerInvariant().StartsWith(key)).Sum(k => k.Value);
                a.SharedUsed = shr.Where(k => k.Key.ToLowerInvariant().StartsWith(key)).Sum(k => k.Value);
                a.UtilHistory.Add(now, a.Util);
                a.EncodeHistory.Add(now, a.UtilEncode);
                if (a.VramTotal > 0) a.VramHistory.Add(now, 100.0 * a.VramUsed / a.VramTotal);

                TrackEncoder(a);
                if (a.VramTotal > 0 && a.VramUsed / a.VramTotal > 0.95 && RateOk("vram" + a.Key, 600))
                    Hub.Publish(new WatchEvent(Severity.Warning, EventCategory.Gpu, Codes.GpuVram, $"Mémoire vidéo presque saturée : {a.Name}",
                        $"{Fr.Bytes(a.VramUsed)} / {Fr.Bytes(a.VramTotal)} — une VRAM saturée peut provoquer TDR et écrans noirs.").With("gpu", a.Name), Src);
            }
        }

        void TrackEncoder(GpuAdapterState a)
        {
            _encodeCounters.TryGetValue(a.Key, out int c);
            bool active = _encodeActive.Contains(a.Key);
            if (!active)
            {
                c = a.UtilEncode > 3 ? c + 1 : 0;
                if (c >= 2)
                {
                    _encodeActive.Add(a.Key);
                    c = 0;
                    Hub.Publish(new WatchEvent(Severity.Info, EventCategory.Process, Codes.GpuEncodeOn, $"Encodeur vidéo GPU actif : {a.Name}",
                        "Partage d'écran, visioconférence ou streaming (Teams, Parsec, OBS…) en cours.").With("gpu", a.Name), Src);
                }
            }
            else
            {
                c = a.UtilEncode < 1 ? c + 1 : 0;
                if (c >= 5)
                {
                    _encodeActive.Remove(a.Key);
                    c = 0;
                    Hub.Publish(new WatchEvent(Severity.Info, EventCategory.Process, Codes.GpuEncodeOff, $"Encodeur vidéo GPU inactif : {a.Name}").With("gpu", a.Name), Src);
                }
            }
            _encodeCounters[a.Key] = c;
        }

        bool RateOk(string key, int seconds)
        {
            if (_rateLimit.TryGetValue(key, out var t) && (DateTime.Now - t).TotalSeconds < seconds) return false;
            _rateLimit[key] = DateTime.Now;
            return true;
        }

        // ------------------------------------------------------------------
        const string NvFieldsExt = "index,pci.bus_id,name,temperature.gpu,utilization.gpu,utilization.encoder,utilization.decoder,power.draw,clocks.gr,clocks.mem,pcie.link.gen.current,pcie.link.width.current,pcie.link.width.max,fan.speed,memory.used,memory.total,driver_version,pstate,clocks_event_reasons.active";
        const string NvFieldsBase = "index,pci.bus_id,name,temperature.gpu,utilization.gpu,power.draw,clocks.gr,clocks.mem,pcie.link.gen.current,pcie.link.width.current,pcie.link.width.max,fan.speed,memory.used,memory.total,driver_version,pstate";

        void SampleNvidia()
        {
            _lastNv = DateTime.Now;
            var output = RunNvSmi(_nvExtended ? NvFieldsExt : NvFieldsBase, out int exit);
            if (exit != 0 && _nvExtended && output.IndexOf("Field", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _nvExtended = false;
                output = RunNvSmi(NvFieldsBase, out exit);
            }
            var lower = output.ToLowerInvariant();
            if (lower.Contains("gpu is lost") || lower.Contains("fallen off the bus") || lower.Contains("unable to determine the device handle"))
            {
                if (!_nvLost)
                {
                    _nvLost = true;
                    Hub.Publish(new WatchEvent(Severity.Critical, EventCategory.Gpu, Codes.GpuLost, "GPU NVIDIA perdu (« fallen off the bus »)",
                        output.Trim(), true).With("gpu", "NVIDIA"), "nvidia-smi");
                }
                return;
            }
            if (exit != 0) return;
            if (_nvLost)
            {
                _nvLost = false;
                Hub.Publish(Severity.Notice, EventCategory.Gpu, Codes.WatchdogInfo, "GPU NVIDIA de nouveau accessible", source: "nvidia-smi");
            }

            var nvAdapters = Adapters.Where(a => a.Vendor == "NVIDIA" && !a.IsVirtual).ToList();
            var assigned = new HashSet<GpuAdapterState>();
            int idx = 0;
            foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var f = line.Split(',').Select(s => s.Trim()).ToArray();
                var s = new NvidiaSample();
                int i = 0;
                s.Index = (int)(D(f, i++) ?? 0);
                s.BusId = At(f, i++);
                s.Name = At(f, i++);
                s.Temp = D(f, i++);
                s.Util = D(f, i++);
                if (_nvExtended) { s.EncUtil = D(f, i++); s.DecUtil = D(f, i++); }
                s.Power = D(f, i++);
                s.ClockGr = D(f, i++);
                s.ClockMem = D(f, i++);
                s.PcieGen = D(f, i++);
                s.PcieWidth = D(f, i++);
                s.PcieWidthMax = D(f, i++);
                s.Fan = D(f, i++);
                s.MemUsedMb = D(f, i++);
                s.MemTotalMb = D(f, i++);
                s.Driver = At(f, i++);
                s.PState = At(f, i++);
                if (_nvExtended) s.Throttle = At(f, i++);

                var target = nvAdapters.FirstOrDefault(a => !assigned.Contains(a) && string.Equals(a.Name, s.Name, StringComparison.OrdinalIgnoreCase))
                             ?? nvAdapters.FirstOrDefault(a => !assigned.Contains(a));
                idx++;
                if (target == null) continue;
                assigned.Add(target);
                target.Nv = s;
                if (s.Temp.HasValue)
                {
                    target.TempHistory.Add(s.Temp.Value);
                    int warn = _settings.GpuTempWarning;
                    if (s.Temp.Value >= warn && RateOk("temp" + target.Key, 600))
                        Hub.Publish(new WatchEvent(s.Temp.Value >= warn + 10 ? Severity.Error : Severity.Warning, EventCategory.Gpu, Codes.GpuTemp,
                            $"Température GPU élevée : {s.Temp:0} °C ({target.Name})", "Une surchauffe peut provoquer TDR et écrans noirs.").With("gpu", target.Name), "nvidia-smi");
                }
                if (s.PcieWidth.HasValue && s.PcieWidthMax.HasValue && s.PcieWidth < s.PcieWidthMax && !_pcieReported)
                {
                    _pcieReported = true;
                    Hub.Publish(new WatchEvent(Severity.Notice, EventCategory.Gpu, Codes.GpuPcie,
                        $"Lien PCIe réduit : x{s.PcieWidth} au lieu de x{s.PcieWidthMax} ({target.Name})",
                        "Peut indiquer une carte mal enfichée, un riser ou un slot câblé en x8/x4.").With("gpu", target.Name), "nvidia-smi");
                }
            }
            foreach (var a in nvAdapters) if (!assigned.Contains(a)) a.Nv = null;
        }

        string RunNvSmi(string fields, out int exitCode)
        {
            exitCode = -1;
            try
            {
                var psi = new ProcessStartInfo(_nvSmiPath, $"--query-gpu={fields} --format=csv,noheader,nounits")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                using var p = Process.Start(psi);
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(4000)) { try { p.Kill(); } catch { } return ""; }
                exitCode = p.ExitCode;
                return outTask.Result + errTask.Result;
            }
            catch { return ""; }
        }

        static string At(string[] f, int i) => i < f.Length ? f[i] : null;

        static double? D(string[] f, int i)
        {
            var s = At(f, i);
            if (string.IsNullOrEmpty(s) || s.StartsWith("[")) return null;
            if (s.StartsWith("0x")) return long.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) ? h : (double?)null;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (double?)null;
        }

        // ------------------------------------------------------------------
        void WriteMetrics()
        {
            _lastMetrics = DateTime.Now;
            var ads = Adapters.Where(a => a.HasLuid).ToList();
            var header = "time,cpu_pct,ram_pct," + string.Join(",", ads.SelectMany((a, i) => new[] { $"gpu{i}_util", $"gpu{i}_vram_mb", $"gpu{i}_encode", $"gpu{i}_temp" })) + ",gpus";
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append(',')
              .Append(F(Cpu.Last)).Append(',').Append(F(Ram.Last));
            foreach (var a in ads)
                sb.Append(',').Append(F(a.Util)).Append(',').Append(F(a.VramUsed / 1048576.0)).Append(',').Append(F(a.UtilEncode)).Append(',').Append(F(a.Nv?.Temp ?? double.NaN));
            sb.Append(",\"").Append(string.Join(" | ", ads.Select(a => a.Name))).Append('"');
            Hub.Log?.WriteMetrics(header, sb.ToString());
        }

        static string F(double v) => double.IsNaN(v) ? "" : v.ToString("0.#", CultureInfo.InvariantCulture);

        public string ContextSummary()
        {
            return "GPU : " + string.Join(" ; ", Adapters.Where(a => a.HasLuid).Select(a =>
                $"{a.Name} {a.Util:0} %" + (a.VramTotal > 0 ? $", VRAM {Fr.Bytes(a.VramUsed)}/{Fr.Bytes(a.VramTotal)}" : "") +
                (a.Nv?.Temp != null ? $", {a.Nv.Temp:0} °C" : "") + (a.UtilEncode > 1 ? $", encodeur {a.UtilEncode:0} %" : "")));
        }

        public void Dispose()
        {
            _stop = true;
            _wake.Set();
        }
    }
}
