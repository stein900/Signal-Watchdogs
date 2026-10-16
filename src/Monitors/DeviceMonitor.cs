using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SignalWatchdogs.Core;
using SignalWatchdogs.Native;

namespace SignalWatchdogs.Monitors
{
    /// <summary>
    /// Inventaire PnP différentiel : USB, USB-C (UCM), hubs/docks, écrans, cartes graphiques,
    /// et tout périphérique en erreur (code 10, 43…). Réagit instantanément aux notifications PnP.
    /// </summary>
    public sealed class DeviceMonitor : IDisposable
    {
        const string Src = "Périphériques (PnP)";
        readonly int _pollSeconds;
        readonly Thread _thread;
        readonly AutoResetEvent _wake = new AutoResetEvent(false);
        volatile bool _stop;
        Dictionary<string, PnpDevice> _known = new Dictionary<string, PnpDevice>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, PnpDevice> _cache = new Dictionary<string, PnpDevice>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> _nameCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, uint> _problemState = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

        public volatile List<PnpDevice> Tracked = new List<PnpDevice>();
        public volatile List<PnpDevice> Problems = new List<PnpDevice>();
        public int TotalDevices { get; private set; }
        public DateTime LastScan { get; private set; }
        public event Action Changed;

        public DeviceMonitor(int pollSeconds)
        {
            _pollSeconds = pollSeconds;
            _thread = new Thread(Run) { IsBackground = true, Name = "SignalWatchdogs.Devices", Priority = ThreadPriority.BelowNormal };
        }

        public void Start() => _thread.Start();

        public void RequestRescan() => _wake.Set();

        public void OnInterfaceNotification(Guid cls, string instanceId, bool arrival) => RequestRescan();

        public string ResolveName(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return null;
            lock (_nameCache) return _nameCache.TryGetValue(instanceId, out var n) ? n : null;
        }

        public static bool IsTracked(PnpDevice d)
        {
            var id = d.InstanceId;
            var cls = (d.Class ?? "").ToLowerInvariant();
            if (id.StartsWith("USB\\") || id.StartsWith("USB4\\") || id.StartsWith("UCMCX\\") || id.StartsWith("UCMUCSI") || id.StartsWith("THUNDERBOLT\\")) return true;
            if (id.StartsWith("DISPLAY\\") || id.StartsWith("ACPI\\USBC")) return true;
            return cls == "display" || cls == "monitor" || cls == "usb" || cls == "ucm" || cls == "usbdevice" || cls == "thunderbolt" || cls == "usb4";
        }

        public static bool IsHub(PnpDevice d)
        {
            var n = (d.Name ?? "").ToLowerInvariant();
            return n.Contains("hub") || n.Contains("concentrateur") || n.Contains("dock") || d.InstanceId.StartsWith("USB\\ROOT_HUB");
        }

        void Run()
        {
            try { Scan(initial: true); }
            catch (Exception ex) { Hub.SetSource(Src, false, ex.Message); }
            while (!_stop)
            {
                bool signaled = _wake.WaitOne(TimeSpan.FromSeconds(_pollSeconds));
                if (_stop) break;
                if (signaled)
                {
                    // Laisse la pile PnP se stabiliser et regroupe les rafales de notifications.
                    Thread.Sleep(700);
                    while (_wake.WaitOne(0)) Thread.Sleep(300);
                }
                try { Scan(initial: false); }
                catch (Exception ex) { Hub.SetSource(Src, false, ex.Message); }
            }
        }

        void Scan(bool initial)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ids = CfgMgr.PresentDeviceIds();
            var current = new Dictionary<string, PnpDevice>(StringComparer.OrdinalIgnoreCase);
            var problems = new List<PnpDevice>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var id in ids)
            {
                var upper = id.ToUpperInvariant();
                seen.Add(upper);
                PnpDevice d;
                if (_cache.TryGetValue(upper, out var prev))
                {
                    // Périphérique déjà connu : seul son état est relu (rapide).
                    if (!CfgMgr.TryGetStatus(upper, out uint st, out uint pr)) continue;
                    d = new PnpDevice
                    {
                        InstanceId = prev.InstanceId, Name = prev.Name, Class = prev.Class, ClassGuid = prev.ClassGuid, Service = prev.Service,
                        Manufacturer = prev.Manufacturer, BusName = prev.BusName, Location = prev.Location, FirstSeen = prev.FirstSeen,
                        Status = st, Problem = pr
                    };
                }
                else
                {
                    d = CfgMgr.Get(upper, true);
                    if (d == null) continue;
                }
                _cache[upper] = d;
                if (d.HasProblem && d.Problem != 45) problems.Add(d);
                if (IsTracked(d) || d.HasProblem) current[upper] = d;
            }
            foreach (var stale in _cache.Keys.Where(k => !seen.Contains(k)).ToList()) _cache.Remove(stale);

            lock (_nameCache)
                foreach (var d in current.Values) _nameCache[d.InstanceId] = d.DisplayName;

            if (!initial)
            {
                foreach (var gone in _known.Values.Where(k => !current.ContainsKey(k.InstanceId)))
                    PublishRemoval(gone);
                foreach (var added in current.Values.Where(c => !_known.ContainsKey(c.InstanceId)))
                    PublishArrival(added);
            }

            foreach (var d in current.Values)
            {
                _problemState.TryGetValue(d.InstanceId, out var oldProb);
                uint newProb = d.HasProblem ? d.Problem : 0;
                if (newProb == oldProb) continue;
                _problemState[d.InstanceId] = newProb;
                if (newProb != 0 && newProb != 45)
                {
                    bool gpu = (d.Class ?? "").Equals("Display", StringComparison.OrdinalIgnoreCase);
                    bool usb = d.InstanceId.StartsWith("USB") || (d.Class ?? "").StartsWith("USB", StringComparison.OrdinalIgnoreCase);
                    var sev = newProb == 22 ? Severity.Info : gpu ? Severity.Critical : Severity.Warning;
                    Hub.Publish(new WatchEvent(sev, gpu ? EventCategory.Gpu : usb ? EventCategory.Usb : EventCategory.Device,
                        gpu ? Codes.GpuAdapterLost : Codes.DeviceProblem,
                        (initial ? "Périphérique en erreur : " : "Nouveau problème : ") + d.DisplayName, $"{CfgMgr.ProblemText(newProb)} — {d.InstanceId}",
                        trigger: !initial && newProb != 22 && (gpu || usb)).With("instance", d.InstanceId).With("problem", newProb), Src);
                }
                else if (!initial && oldProb != 0)
                {
                    Hub.Publish(new WatchEvent(Severity.Notice, EventCategory.Device, Codes.DeviceProblemCleared, "Problème résolu : " + d.DisplayName, d.InstanceId).With("instance", d.InstanceId), Src);
                }
            }

            _known = current;
            TotalDevices = ids.Count;
            LastScan = DateTime.Now;
            Tracked = current.Values.Where(IsTracked).OrderBy(d => d.Class).ThenBy(d => d.DisplayName).ToList();
            Problems = problems.OrderBy(p => p.DisplayName).ToList();
            Hub.SetSource(Src, true, $"{ids.Count} périphériques présents, {Tracked.Count} suivis, {problems.Count} en erreur — scan {sw.ElapsedMilliseconds} ms");
            if (initial)
                Hub.Publish(Severity.Info, EventCategory.Device, Codes.WatchdogInfo, $"Inventaire PnP : {Tracked.Count} périphériques USB / affichage suivis",
                    $"{Tracked.Count(IsHub)} hub(s)/dock(s), {problems.Count} périphérique(s) en erreur", source: Src);
            try { Changed?.Invoke(); } catch { }
        }

        void PublishRemoval(PnpDevice d)
        {
            var id = d.InstanceId;
            if (id.StartsWith("DISPLAY\\"))
            {
                Hub.Publish(new WatchEvent(Severity.Notice, EventCategory.Display, Codes.MonitorPnpRemoval, "Écran retiré (PnP) : " + d.DisplayName, id).With("instance", id), Src);
                return;
            }
            if ((d.Class ?? "").Equals("Display", StringComparison.OrdinalIgnoreCase)) return; // géré par la fenêtre de notifications et le moniteur GPU
            bool hub = IsHub(d);
            bool ucm = id.StartsWith("UCM") || (d.Class ?? "").Equals("UCM", StringComparison.OrdinalIgnoreCase) || id.StartsWith("ACPI\\USBC");
            var e = new WatchEvent(hub || ucm ? Severity.Warning : Severity.Notice, ucm ? EventCategory.UsbC : EventCategory.Usb,
                hub ? Codes.UsbHubRemoved : Codes.UsbRemoved,
                (hub ? "Hub / dock USB déconnecté : " : ucm ? "Connecteur USB-C retiré : " : "Périphérique USB retiré : ") + d.DisplayName,
                $"{d.Class} · {id}", trigger: hub || ucm).With("instance", id).With("class", d.Class);
            Hub.Publish(e, Src);
        }

        void PublishArrival(PnpDevice d)
        {
            var id = d.InstanceId;
            if (id.StartsWith("DISPLAY\\"))
            {
                Hub.Publish(new WatchEvent(Severity.Info, EventCategory.Display, Codes.MonitorPnpArrival, "Écran ajouté (PnP) : " + d.DisplayName, id).With("instance", id), Src);
                return;
            }
            if ((d.Class ?? "").Equals("Display", StringComparison.OrdinalIgnoreCase)) return;
            bool hub = IsHub(d);
            Hub.Publish(new WatchEvent(Severity.Info, EventCategory.Usb, Codes.UsbAdded, (hub ? "Hub / dock USB connecté : " : "Périphérique USB connecté : ") + d.DisplayName,
                $"{d.Class} · {id}").With("instance", id).With("class", d.Class), Src);
        }

        public void Dispose()
        {
            _stop = true;
            _wake.Set();
        }
    }
}
