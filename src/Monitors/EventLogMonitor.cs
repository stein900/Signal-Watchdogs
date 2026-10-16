using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SignalWatchdogs.Core;

namespace SignalWatchdogs.Monitors
{
    /// <summary>
    /// Abonnements temps réel aux journaux Windows (System, Application, DxgKrnl, Kernel-PnP,
    /// USB/UCSI, WerKernel, sessions RDP…) + lecture de l'historique sur N jours.
    /// </summary>
    public sealed class EventLogMonitor : IDisposable
    {
        delegate WatchEvent Mapper(EventRecord r, bool historical);

        sealed class Rule
        {
            public string Name;
            public string Channel;
            public string Cond;
            public Mapper Map;
            public bool HistoryOnly;
        }

        const string ClassDisplay = "{4d36e968-e325-11ce-bfc1-08002be10318}";
        const string ClassMonitor = "{4d36e96e-e325-11ce-bfc1-08002be10318}";
        const string ClassUsb = "{36fc9e60-c465-11cf-8056-444553540000}";
        const string ClassUcm = "{e6f1aa1c-7f3b-4473-b2e8-c97d8ac71d53}";
        const string Lv123 = "(Level=1 or Level=2 or Level=3)";

        static readonly string[] GpuProviders = { "nvlddmkm", "amdkmdag", "amdkmdap", "atikmdag", "atikmpag", "amdwddmg", "igfx", "igfxn", "igfxnd", "igfxdcn", "BasicDisplay", "BasicRender", "dxgkrnl", "Microsoft-Windows-DxgKrnl" };
        static readonly string[] UsbProviders = { "Microsoft-Windows-USB-USBHUB3", "USBHUB3", "usbhub", "USBHUB", "Microsoft-Windows-USB-USBHUB", "USBXHCI", "Microsoft-Windows-USB-USBXHCI", "Microsoft-Windows-USB-UCMUCSICX", "UcmUcsiCx", "UcmUcsiAcpiClient", "Microsoft-Windows-USB-UCX", "Microsoft-Windows-USB-USBPORT", "usbccgp", "Microsoft-Windows-Thunderbolt", "Thunderbolt" };
        static readonly string[] WatchedCrashApps = { "ms-teams.exe", "teams.exe", "mstsc.exe", "msrdc.exe", "parsecd.exe", "zoom.exe", "anydesk.exe", "teamviewer.exe", "obs64.exe", "explorer.exe", "dwm.exe", "nvcontainer.exe", "nvdisplay.container.exe", "displaylinkuseragent.exe" };

        readonly List<Rule> _rules = new List<Rule>();
        readonly List<EventLogWatcher> _watchers = new List<EventLogWatcher>();
        FileSystemWatcher _lkrWatcher;

        /// <summary>Résolution d'un ID d'instance en nom lisible (cache du moniteur de périphériques).</summary>
        public Func<string, string> NameResolver;

        public EventLogMonitor()
        {
            BuildRules();
        }

        public void Start()
        {
            foreach (var grp in _rules.Where(r => !r.HistoryOnly).GroupBy(r => r.Channel))
            {
                int ok = 0;
                string err = null;
                foreach (var rule in grp)
                {
                    try
                    {
                        var q = new EventLogQuery(rule.Channel, PathType.LogName, $"*[System[{rule.Cond}]]");
                        var w = new EventLogWatcher(q);
                        var r = rule;
                        w.EventRecordWritten += (s, a) => OnRecord(r, a);
                        w.Enabled = true;
                        _watchers.Add(w);
                        ok++;
                    }
                    catch (Exception ex) { err = ex.GetType().Name == "EventLogNotFoundException" ? "journal absent sur ce poste" : ex.Message; }
                }
                Hub.SetSource("Journal : " + grp.Key, ok > 0, ok > 0 ? $"{ok} règle(s) active(s)" + (err != null ? $" — {err}" : "") : err ?? "indisponible");
            }
            StartLiveKernelWatcher();
        }

        void OnRecord(Rule rule, EventRecordWrittenEventArgs a)
        {
            if (a.EventRecord == null) return;
            try
            {
                using (a.EventRecord)
                {
                    var e = rule.Map(a.EventRecord, false);
                    if (e != null) Hub.Publish(e);
                }
            }
            catch { }
        }

        // ------------------------------------------------------------------
        // Historique
        // ------------------------------------------------------------------
        public List<WatchEvent> ScanHistory(int days, Action<string> progress, int maxPerRule = 400)
        {
            var res = new List<WatchEvent>();
            long ms = (long)days * 86400000L;
            foreach (var rule in _rules)
            {
                progress?.Invoke($"Lecture : {rule.Channel} — {rule.Name}");
                try
                {
                    var q = new EventLogQuery(rule.Channel, PathType.LogName, $"*[System[({rule.Cond}) and TimeCreated[timediff(@SystemTime) <= {ms}]]]") { ReverseDirection = true };
                    using var reader = new EventLogReader(q);
                    int n = 0;
                    EventRecord r;
                    while (n < maxPerRule && (r = reader.ReadEvent()) != null)
                    {
                        using (r)
                        {
                            try
                            {
                                var e = rule.Map(r, true);
                                if (e != null) { e.Historical = true; res.Add(e); n++; }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
            progress?.Invoke("Lecture : rapports LiveKernel");
            res.AddRange(ScanLiveKernelReports(days));
            return res.OrderBy(e => e.Time).ToList();
        }

        // ------------------------------------------------------------------
        // Règles
        // ------------------------------------------------------------------
        static string P(params string[] names) => "Provider[" + string.Join(" or ", names.Select(n => $"@Name='{n}'")) + "]";
        static string Ids(params int[] ids) => "(" + string.Join(" or ", ids.Select(i => $"EventID={i}")) + ")";

        void Add(string name, string channel, string cond, Mapper map, bool historyOnly = false) =>
            _rules.Add(new Rule { Name = name, Channel = channel, Cond = cond, Map = map, HistoryOnly = historyOnly });

        void BuildRules()
        {
            // ---------- GPU ----------
            Add("TDR (Display 4101)", "System", P("Display") + " and " + Ids(4101), (r, h) =>
                Make(r, Severity.Critical, EventCategory.Gpu, Codes.Tdr, "Le pilote graphique a cessé de répondre puis a été relancé (TDR)", true));

            Add("Erreurs pilotes graphiques", "System", P(GpuProviders) + " and " + Lv123, (r, h) =>
            {
                var p = (r.ProviderName ?? "").ToLowerInvariant();
                string vendor = p.StartsWith("nv") ? "NVIDIA" : p.StartsWith("amd") || p.StartsWith("ati") ? "AMD" : p.StartsWith("igfx") ? "Intel" : null;
                if (p.StartsWith("basic"))
                    return Make(r, Severity.Error, EventCategory.Gpu, Codes.BasicDisplay, "Bascule sur le pilote d'affichage de base Microsoft", true);
                if (p.Contains("dxgkrnl"))
                    return Make(r, Severity.Error, EventCategory.Gpu, Codes.DxgkError, $"Erreur du noyau graphique DirectX (dxgkrnl #{r.Id})", true);
                bool tdr = p == "nvlddmkm" && r.Id == 153;
                return Make(r, r.Level <= 2 ? Severity.Error : Severity.Warning, EventCategory.Gpu, tdr ? Codes.Tdr : Codes.GpuDriverError,
                    tdr ? "Réinitialisation du GPU NVIDIA (nvlddmkm 153)" : $"Erreur du pilote {vendor ?? r.ProviderName} ({r.ProviderName} #{r.Id})", r.Level <= 2);
            });

            Add("DxgKrnl Admin", "Microsoft-Windows-DxgKrnl-Admin", Lv123, (r, h) =>
                Make(r, Severity.Error, EventCategory.Gpu, Codes.DxgkError, $"Noyau graphique (DxgKrnl-Admin #{r.Id})", true));

            Add("DxgKrnl Operational", "Microsoft-Windows-DxgKrnl-Operational", Lv123, (r, h) =>
                Make(r, r.Level <= 2 ? Severity.Error : Severity.Warning, EventCategory.Gpu, Codes.DxgkError, $"Noyau graphique (DxgKrnl #{r.Id})", r.Level <= 2));

            Add("WER LiveKernelEvent / plantages", "Application", P("Windows Error Reporting") + " and " + Ids(1001), MapWer);

            Add("Plantages applicatifs (dwm, explorer…)", "Application", P("Application Error", "Application Hang") + " and " + Ids(1000, 1002), (r, h) =>
            {
                var app = Prop(r, 0)?.ToLowerInvariant() ?? "";
                if (app == "dwm.exe")
                    return Make(r, Severity.Critical, EventCategory.Display, Codes.DwmCrash, "Le compositeur d'affichage DWM (dwm.exe) a planté", true);
                if (app == "explorer.exe")
                    return Make(r, Severity.Warning, EventCategory.Display, Codes.ExplorerRestart, "L'Explorateur Windows (explorer.exe) a planté", true);
                if (WatchedCrashApps.Contains(app))
                    return Make(r, Severity.Notice, EventCategory.Process, Codes.AppCrash, $"Plantage / blocage de {app}", false).With("app", app);
                return null;
            });

            Add("Desktop Window Manager", "Application", P("Desktop Window Manager", "Microsoft-Windows-Dwm-Dwm", "Microsoft-Windows-Dwm-Core") + " and " + Lv123, (r, h) =>
                Make(r, Severity.Error, EventCategory.Display, Codes.DwmCrash, $"Erreur du compositeur DWM (#{r.Id})", true));

            Add("WerKernel (vidages LiveKernel)", "Microsoft-Windows-WerKernel/Operational", Ids(1001), (r, h) =>
            {
                var d = XmlData(r);
                d.TryGetValue("ComponentName", out var comp);
                comp ??= "?";
                return MapLiveKernelComponent(r, comp);
            });

            // ---------- Système ----------
            Add("Kernel-Power 41", "System", P("Microsoft-Windows-Kernel-Power") + " and " + Ids(41), (r, h) =>
                Make(r, Severity.Critical, EventCategory.System, Codes.KernelPower41, "Redémarrage inattendu du système (Kernel-Power 41)", true));

            Add("Arrêt inattendu (6008)", "System", P("EventLog") + " and " + Ids(6008), (r, h) =>
                Make(r, Severity.Error, EventCategory.System, Codes.UnexpectedShutdown, "L'arrêt précédent du système était inattendu", false));

            Add("Écran bleu (BugCheck)", "System", P("Microsoft-Windows-WER-SystemErrorReporting") + " and " + Ids(1001), (r, h) =>
                Make(r, Severity.Critical, EventCategory.System, Codes.Bsod, "Écran bleu (BugCheck) : le système a planté", true));

            Add("Erreurs matérielles WHEA", "System", P("Microsoft-Windows-WHEA-Logger") + " and " + Lv123, (r, h) =>
            {
                var msg = Msg(r);
                bool pcie = msg.IndexOf("PCI Express", StringComparison.OrdinalIgnoreCase) >= 0;
                return Make(r, r.Level <= 2 ? Severity.Error : Severity.Warning, pcie ? EventCategory.Gpu : EventCategory.System, Codes.Whea,
                    pcie ? "Erreur matérielle sur le bus PCI Express (WHEA)" : "Erreur matérielle signalée (WHEA)", r.Level <= 2, msg);
            });

            Add("Services en échec", "System", P("Service Control Manager") + " and " + Ids(7031, 7034), (r, h) =>
                Make(r, Severity.Notice, EventCategory.System, Codes.ServiceCrash, $"Service arrêté de façon inattendue : {Prop(r, 0)}", false));

            Add("Mises à jour Windows", "System", P("Microsoft-Windows-WindowsUpdateClient") + " and " + Ids(19, 20), (r, h) =>
            {
                var title = (r.Id == 19 ? Prop(r, 0) : Prop(r, 1) ?? Prop(r, 0)) ?? "";
                // Ignore le bruit quotidien : définitions Defender et applications du Store (9NXXXXXXXXXX-…)
                if (title.IndexOf("Defender", StringComparison.OrdinalIgnoreCase) >= 0 || Regex.IsMatch(title, @"^9[A-Z0-9]{11}-")) return null;
                return r.Id == 19
                    ? Make(r, Severity.Info, EventCategory.System, Codes.WindowsUpdate, "Mise à jour Windows installée : " + Trunc(title, 140), false)
                    : Make(r, Severity.Warning, EventCategory.System, Codes.WindowsUpdate, "Échec d'installation d'une mise à jour : " + Trunc(title, 140), false);
            });

            Add("Veille / reprise", "System", P("Microsoft-Windows-Kernel-Power") + " and " + Ids(42, 107, 506, 507), (r, h) =>
            {
                bool sleep = r.Id == 42 || r.Id == 506;
                string t = r.Id switch { 42 => "Entrée en veille", 107 => "Reprise après veille", 506 => "Entrée en veille moderne (écran éteint)", _ => "Sortie de veille moderne" };
                return Make(r, Severity.Info, EventCategory.Power, sleep ? Codes.Suspend : Codes.Resume, t, false);
            }, historyOnly: true);

            // ---------- USB / USB-C ----------
            Add("Erreurs USB (System)", "System", P(UsbProviders) + " and " + Lv123, (r, h) =>
            {
                var p = r.ProviderName ?? "";
                bool ucsi = p.IndexOf("ucm", StringComparison.OrdinalIgnoreCase) >= 0;
                bool xhci = p.IndexOf("xhci", StringComparison.OrdinalIgnoreCase) >= 0;
                return Make(r, r.Level <= 2 ? Severity.Error : Severity.Warning, ucsi ? EventCategory.UsbC : EventCategory.Usb,
                    ucsi ? Codes.UcsiError : xhci ? Codes.XhciError : Codes.UsbError, $"Erreur USB ({p} #{r.Id})", r.Level <= 2);
            });

            Add("Contrôleur USB-C (UCSI)", "Microsoft-Windows-USB-UCMUCSICX/Operational", "Level>=0", (r, h) =>
            {
                var msg = Msg(r);
                var ft = Regex.Match(msg, @"Failure Type\s*:\s*([^\r\n]+)");
                var cmd = Regex.Match(msg, @"UCSI Command\s*:\s*([^\r\n]+)");
                var detail = (ft.Success ? ft.Groups[1].Value.Trim() : "") + (cmd.Success ? " · commande " + cmd.Groups[1].Value.Trim() : "");
                return Make(r, Severity.Error, EventCategory.UsbC, Codes.UcsiError,
                    "Défaillance du contrôleur USB-C (UCSI)" + (detail.Length > 0 ? " : " + detail : ""), true, msg);
            });

            Add("Contrôleur USB xHCI", "Microsoft-Windows-USB-USBXHCI-Operational", "Level>=0", (r, h) =>
                Make(r, Severity.Error, EventCategory.Usb, Codes.XhciError, "Erreur / réinitialisation du contrôleur USB xHCI", true));

            // ---------- Périphériques (PnP) ----------
            Add("Configuration PnP", "Microsoft-Windows-Kernel-PnP/Configuration", Ids(400, 410, 411, 420), MapPnpConfiguration);

            Add("Périphériques disparus du bus", "Microsoft-Windows-Kernel-PnP/Device Management", Ids(1010), (r, h) =>
            {
                var d = XmlData(r);
                d.TryGetValue("DeviceInstanceId", out var inst);
                inst = (inst ?? "").ToUpperInvariant();
                if (inst.Length == 0 || inst.StartsWith("STORAGE\\") || inst.StartsWith("SWD\\") || inst.StartsWith("ROOT\\")) return null;
                if (inst.StartsWith("ACPI\\") && !inst.StartsWith("ACPI\\USBC")) return null;
                var name = NameResolver?.Invoke(inst) ?? inst;
                if (inst.StartsWith("USB") )
                    return Make(r, Severity.Warning, EventCategory.Usb, Codes.PnpMissing, "Périphérique USB disparu du bus sans retrait : " + name, !h, inst).With("instance", inst);
                if (inst.StartsWith("DISPLAY\\"))
                    return Make(r, Severity.Warning, EventCategory.Display, Codes.PnpMissing, "Écran disparu du bus : " + name, false, inst).With("instance", inst);
                if (inst.StartsWith("PCI\\"))
                    return Make(r, Severity.Error, EventCategory.Device, Codes.PnpMissing, "Périphérique PCI disparu du bus : " + name, true, inst).With("instance", inst);
                if (inst.StartsWith("HID\\")) return null;
                return Make(r, Severity.Notice, EventCategory.Device, Codes.PnpMissing, "Périphérique disparu du bus : " + name, false, inst).With("instance", inst);
            });

            Add("Installation de pilotes", "System", P("Microsoft-Windows-UserPnp") + " and " + Ids(20001, 20003), (r, h) =>
            {
                var msg = Msg(r);
                bool gfx = Regex.IsMatch(msg, "display|graphi|nvidia|radeon|intel\\(r\\).*graphics|uhd|iris|arc", RegexOptions.IgnoreCase);
                return Make(r, Severity.Notice, gfx ? EventCategory.Gpu : EventCategory.Device, Codes.DriverInstall,
                    gfx ? "Pilote graphique installé / mis à jour" : "Pilote installé / mis à jour", false, msg);
            });

            // ---------- Sessions ----------
            Add("Sessions locales / RDP entrantes", "Microsoft-Windows-TerminalServices-LocalSessionManager/Operational", Ids(21, 23, 24, 25, 39, 40), (r, h) =>
            {
                var d = XmlData(r);
                d.TryGetValue("User", out var user);
                d.TryGetValue("Address", out var addr);
                d.TryGetValue("SessionID", out var sid);
                bool local = string.IsNullOrEmpty(addr) || addr.Equals("LOCAL", StringComparison.OrdinalIgnoreCase);
                string who = string.IsNullOrEmpty(user) ? "" : " : " + user;
                string from = local ? "" : $" depuis {addr}";
                return r.Id switch
                {
                    21 => Make(r, Severity.Notice, EventCategory.Session, local ? Codes.Logon : Codes.RdpInConnect, (local ? "Ouverture de session (console)" : "Connexion RDP entrante" + from) + who, false),
                    23 => Make(r, Severity.Info, EventCategory.Session, Codes.Logoff, "Fermeture de session" + who, false),
                    24 => Make(r, Severity.Notice, EventCategory.Session, local ? Codes.ConsoleDisconnect : Codes.RdpInDisconnect, (local ? "Session déconnectée de la console" : "Session RDP déconnectée" + from) + who, false),
                    25 => Make(r, Severity.Notice, EventCategory.Session, local ? Codes.ConsoleConnect : Codes.RdpInConnect, (local ? "Session reconnectée à la console" : "Reconnexion RDP" + from) + who, false),
                    39 => Make(r, Severity.Notice, EventCategory.Session, Codes.ConsoleDisconnect, $"Session {sid} déconnectée par une autre session", false),
                    _ => Make(r, Severity.Info, EventCategory.Session, Codes.WatchdogInfo, $"Session {sid} déconnectée (code raison {(d.TryGetValue("Reason", out var rc) ? rc : "?")})", false),
                };
            });

            Add("Client RDP sortant", "Microsoft-Windows-TerminalServices-RDPClient/Operational", Ids(1024, 1026), (r, h) =>
            {
                // EventData : Name="Server Name" / Value="<serveur>" (ou "Disconnect Reason" / code)
                var d = XmlData(r);
                var v = d.TryGetValue("Value", out var val) && !string.IsNullOrEmpty(val) ? val : Prop(r, 1) ?? Prop(r, 0);
                return r.Id == 1024
                    ? Make(r, Severity.Notice, EventCategory.Session, Codes.RdpOutConnect, "Connexion RDP sortante vers " + v, false)
                    : Make(r, Severity.Notice, EventCategory.Session, Codes.RdpOutDisconnect, "Connexion RDP sortante fermée (raison " + v + ")", false);
            });

            // ---------- Alimentation ----------
            Add("Événements thermiques", "Microsoft-Windows-Kernel-Power/Thermal-Operational", "Level>=0", (r, h) =>
                Make(r, Severity.Warning, EventCategory.Power, Codes.Thermal, "Événement thermique (surchauffe / limitation)", false));
        }

        WatchEvent MapWer(EventRecord r, bool h)
        {
            string evName = Prop(r, 2) ?? "";
            string p1 = Prop(r, 5) ?? "";
            var msg = Msg(r);
            if (evName.Length == 0 || !Regex.IsMatch(evName, "^[A-Za-z]"))
            {
                var mName = Regex.Match(msg, @"(LiveKernelEvent|BlueScreen|APPCRASH|AppHangB1|AppHangXProcB1)");
                evName = mName.Success ? mName.Value : evName;
                var mP1 = Regex.Match(msg, @"P1\s*:\s*([^\r\n]+)");
                if (mP1.Success) p1 = mP1.Groups[1].Value.Trim();
            }
            if (evName.Equals("LiveKernelEvent", StringComparison.OrdinalIgnoreCase))
            {
                var code = p1.Trim().ToLowerInvariant().TrimStart('0');
                string label = code switch
                {
                    "141" => "le moteur graphique a cessé de répondre (LiveKernelEvent 141)",
                    "117" => "délai de réponse du GPU dépassé — TDR (LiveKernelEvent 117)",
                    "1a8" => "ÉCRAN NOIR détecté par le noyau graphique (LiveKernelEvent 1A8)",
                    "193" => "vidage du noyau graphique DirectX (LiveKernelEvent 193)",
                    "144" => "erreur du pilote USB 3 (LiveKernelEvent 144)",
                    "1d4" => "défaillance du contrôleur USB-C UCSI (LiveKernelEvent 1D4)",
                    _ => $"incident noyau LiveKernelEvent {code.ToUpperInvariant()}"
                };
                if (code == "141" || code == "117" || code == "1a8" || code == "193")
                    return Make(r, Severity.Critical, EventCategory.Gpu, Codes.LiveKernelGpu, "Incident GPU : " + label, true, msg).With("lke", code);
                if (code == "144" || code == "1d4")
                    return Make(r, Severity.Error, code == "1d4" ? EventCategory.UsbC : EventCategory.Usb, Codes.LiveKernelUsb, "Incident USB : " + label, true, msg).With("lke", code);
                return Make(r, Severity.Warning, EventCategory.System, Codes.LiveKernelOther, "Incident noyau : " + label, false, msg).With("lke", code);
            }
            if (evName.Equals("BlueScreen", StringComparison.OrdinalIgnoreCase))
                return Make(r, Severity.Critical, EventCategory.System, Codes.Bsod, "Écran bleu (rapport BlueScreen)", true, msg);
            var app = p1.ToLowerInvariant();
            if (app == "dwm.exe")
                return Make(r, Severity.Critical, EventCategory.Display, Codes.DwmCrash, "Rapport d'erreur : DWM a planté / ne répondait plus", true, msg);
            return null;
        }

        WatchEvent MapPnpConfiguration(EventRecord r, bool h)
        {
            var d = XmlData(r);
            d.TryGetValue("DeviceInstanceId", out var inst);
            d.TryGetValue("ClassGuid", out var cls);
            inst = (inst ?? "").ToUpperInvariant();
            cls = (cls ?? "").ToLowerInvariant();
            if (!cls.StartsWith("{")) cls = "{" + cls + "}";
            if (inst.StartsWith("SWD\\PRINTENUM") || inst.StartsWith("SWD\\WPDBUSENUM")) return null;
            bool display = cls == ClassDisplay;
            bool monitor = cls == ClassMonitor;
            bool usb = cls == ClassUsb || cls == ClassUcm || inst.StartsWith("USB");
            var name = NameResolver?.Invoke(inst) ?? inst;
            switch (r.Id)
            {
                case 411:
                    d.TryGetValue("Problem", out var prob);
                    if (display) return Make(r, Severity.Error, EventCategory.Gpu, Codes.DeviceStartProblem, "La carte graphique n'a pas pu démarrer : " + name, true).With("instance", inst).With("problem", prob);
                    if (usb) return Make(r, Severity.Warning, EventCategory.Usb, Codes.DeviceStartProblem, "Périphérique USB : problème de démarrage — " + name, !h).With("instance", inst).With("problem", prob);
                    if (monitor) return Make(r, Severity.Warning, EventCategory.Display, Codes.DeviceStartProblem, "Écran : problème de démarrage — " + name, false).With("instance", inst);
                    return Make(r, Severity.Notice, EventCategory.Device, Codes.DeviceStartProblem, "Problème de démarrage : " + name, false).With("instance", inst).With("problem", prob);
                case 400:
                    if (display) return Make(r, Severity.Notice, EventCategory.Gpu, Codes.DriverInstall, "Pilote graphique (re)configuré : " + name, false).With("instance", inst);
                    return null;
                case 410:
                    if (display && !inst.StartsWith("ROOT\\") && !inst.StartsWith("SWD\\"))
                        return Make(r, h ? Severity.Info : Severity.Warning, EventCategory.Gpu, Codes.GpuAdapterPnp, "Carte graphique démarrée (chargement du pilote) : " + name, !h).With("instance", inst);
                    if (display) return Make(r, Severity.Notice, EventCategory.Display, Codes.VirtualDisplay, "Adaptateur d'affichage virtuel démarré : " + name, false).With("instance", inst);
                    return null;
                case 420:
                    if (display) return Make(r, Severity.Warning, EventCategory.Gpu, Codes.GpuAdapterPnp, "Carte graphique supprimée du système : " + name, !h).With("instance", inst);
                    return null;
            }
            return null;
        }

        WatchEvent MapLiveKernelComponent(EventRecord r, string comp)
        {
            var c = comp.ToLowerInvariant();
            if (c.Contains("dxgkrnl") || c.Contains("nvlddmkm") || c.Contains("amdkm") || c.Contains("igdkmd") || c.Contains("basicdisplay") || c.Contains("watchdog"))
                return Make(r, Severity.Error, EventCategory.Gpu, Codes.LiveKernelGpu, $"Vidage mémoire demandé par {comp} (incident graphique)", true).With("component", comp);
            if (c.Contains("ucm") || c.Contains("usb") || c.Contains("ucx"))
                return Make(r, Severity.Error, c.Contains("ucm") ? EventCategory.UsbC : EventCategory.Usb, Codes.LiveKernelUsb, $"Vidage mémoire demandé par {comp} (incident USB/USB-C)", true).With("component", comp);
            if (c.Contains("pow32k"))
                return Make(r, Severity.Error, EventCategory.Display, Codes.LiveKernelGpu, "L'affichage n'a pas répondu à l'allumage (PoW32kWatchdog)", true).With("component", comp);
            return Make(r, Severity.Warning, EventCategory.System, Codes.LiveKernelOther, $"Vidage mémoire du noyau demandé par {comp}", false).With("component", comp);
        }

        // ------------------------------------------------------------------
        // Dossier C:\Windows\LiveKernelReports
        // ------------------------------------------------------------------
        static string LkrRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "LiveKernelReports");

        void StartLiveKernelWatcher()
        {
            try
            {
                if (!Directory.Exists(LkrRoot)) { Hub.SetSource("Rapports LiveKernel (dumps)", false, "Dossier absent"); return; }
                _lkrWatcher = new FileSystemWatcher(LkrRoot, "*.dmp") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime };
                _lkrWatcher.Created += (s, a) =>
                {
                    var e = LkrEvent(a.FullPath, DateTime.Now);
                    if (e != null) Hub.Publish(e);
                };
                _lkrWatcher.EnableRaisingEvents = true;
                Hub.SetSource("Rapports LiveKernel (dumps)", true, LkrRoot);
            }
            catch (Exception ex) { Hub.SetSource("Rapports LiveKernel (dumps)", false, "Accès refusé (lancer en administrateur) : " + ex.Message); }
        }

        List<WatchEvent> ScanLiveKernelReports(int days)
        {
            var res = new List<WatchEvent>();
            try
            {
                var limit = DateTime.Now.AddDays(-days);
                foreach (var f in Directory.EnumerateFiles(LkrRoot, "*.dmp", SearchOption.AllDirectories))
                {
                    var t = File.GetLastWriteTime(f);
                    if (t < limit) continue;
                    var e = LkrEvent(f, t);
                    if (e != null) { e.Historical = true; res.Add(e); }
                }
            }
            catch { }
            return res;
        }

        static WatchEvent LkrEvent(string path, DateTime time)
        {
            var folder = Path.GetFileName(Path.GetDirectoryName(path)) ?? "";
            var f = folder.ToLowerInvariant();
            WatchEvent e;
            if (f == "watchdog" || f.Contains("dxgkrnl") || f.Contains("nvlddmkm") || f.Contains("igdkmd") || f.Contains("amdkm"))
                e = new WatchEvent(Severity.Error, EventCategory.Gpu, Codes.LiveKernelGpu, $"Dump LiveKernel graphique créé ({folder})", path, true);
            else if (f.Contains("pow32k"))
                e = new WatchEvent(Severity.Error, EventCategory.Display, Codes.LiveKernelGpu, "Dump : l'affichage n'a pas répondu à l'allumage (PoW32kWatchdog)", path, true);
            else if (f.Contains("usb") || f.Contains("ucm"))
                e = new WatchEvent(Severity.Error, f.Contains("ucm") ? EventCategory.UsbC : EventCategory.Usb, Codes.LiveKernelUsb, $"Dump LiveKernel USB créé ({folder})", path, true);
            else
                e = new WatchEvent(Severity.Warning, EventCategory.System, Codes.LiveKernelOther, $"Dump LiveKernel créé ({folder})", path, false);
            e.Time = time;
            e.Source = "LiveKernelReports";
            return e.With("file", path);
        }

        // ------------------------------------------------------------------
        // Utilitaires
        // ------------------------------------------------------------------
        static WatchEvent Make(EventRecord r, Severity s, EventCategory c, string code, string title, bool trigger, string details = null)
        {
            var e = new WatchEvent(s, c, code, title, details ?? Msg(r), trigger)
            {
                Time = r.TimeCreated ?? DateTime.Now,
                Source = $"Journal {ShortLog(r.LogName)} · {r.ProviderName} #{r.Id}",
            };
            return e.With("provider", r.ProviderName).With("eventId", r.Id).With("channel", r.LogName);
        }

        static string ShortLog(string log)
        {
            if (string.IsNullOrEmpty(log)) return "?";
            var s = log.Replace("Microsoft-Windows-", "");
            return s.Length > 48 ? s.Substring(0, 48) : s;
        }

        static string Msg(EventRecord r)
        {
            string m = null;
            try { m = r.FormatDescription(); } catch { }
            if (string.IsNullOrWhiteSpace(m))
            {
                try { m = string.Join(" | ", r.Properties.Select(p => Convert.ToString(p.Value)).Where(v => !string.IsNullOrWhiteSpace(v))); } catch { m = ""; }
            }
            m = Regex.Replace(m ?? "", @"[ \t]{2,}", " ").Trim();
            return Trunc(m, 2500);
        }

        static string Prop(EventRecord r, int i)
        {
            try { return r.Properties.Count > i ? Convert.ToString(r.Properties[i].Value)?.Trim() : null; } catch { return null; }
        }

        static string Trunc(string s, int n) => s == null ? "" : s.Length <= n ? s : s.Substring(0, n) + "…";

        static readonly XNamespace Ns = "http://schemas.microsoft.com/win/2004/08/events/event";

        static Dictionary<string, string> XmlData(EventRecord r)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var x = XDocument.Parse(r.ToXml());
                var ed = x.Root?.Element(Ns + "EventData");
                if (ed != null)
                    foreach (var el in ed.Elements(Ns + "Data"))
                    {
                        var n = (string)el.Attribute("Name");
                        if (n != null) d[n] = el.Value;
                    }
                var ud = x.Root?.Element(Ns + "UserData");
                if (ud != null)
                    foreach (var el in ud.Descendants().Where(e => !e.HasElements))
                        d[el.Name.LocalName] = el.Value;
            }
            catch { }
            return d;
        }

        public void Dispose()
        {
            foreach (var w in _watchers)
            {
                try { w.Enabled = false; w.Dispose(); } catch { }
            }
            _watchers.Clear();
            try { _lkrWatcher?.Dispose(); } catch { }
        }
    }
}
