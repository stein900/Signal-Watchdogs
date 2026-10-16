using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SignalWatchdogs.Core;
using SignalWatchdogs.Monitors;
using SignalWatchdogs.Native;

namespace SignalWatchdogs.Diagnostics
{
    public enum CheckStatus { Ok, Info, Warning, Problem }

    public sealed class CheckResult
    {
        public string Area;
        public string Title;
        public CheckStatus Status;
        public string Detail;
        public string Recommendation;
    }

    /// <summary>
    /// Audit de configuration : réglages connus pour provoquer écrans noirs / clignotements
    /// (MPO, HAGS, TDR, gestion d'alimentation USB/PCIe, pilotes anciens, écrans sur 2 GPU, écrans virtuels…).
    /// </summary>
    public static class SystemDiagnostics
    {
        static readonly Guid SubUsb = new Guid("2a737441-1930-4402-8d77-b2bebba308a3");
        static readonly Guid UsbSelectiveSuspend = new Guid("48e6b7a6-50f5-4782-a5d4-53bb8f07e226");
        static readonly Guid SubPcie = new Guid("501a4d13-42af-4429-9fd1-a8218c268e20");
        static readonly Guid PcieAspm = new Guid("ee12f906-d277-404b-b6da-e5fa1a576df5");
        static readonly Guid SubVideo = new Guid("7516b95f-f776-4464-8c53-06167f40cc99");
        static readonly Guid VideoIdle = new Guid("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");

        public static List<CheckResult> Run(WatchdogService svc)
        {
            var res = new List<CheckResult>();
            void Add(string area, string title, CheckStatus st, string detail, string reco = null) =>
                res.Add(new CheckResult { Area = area, Title = title, Status = st, Detail = detail, Recommendation = reco });

            var monitors = svc.Topology?.Current ?? new List<MonitorInfo>();
            var adapters = svc.Gpu?.Adapters ?? new List<GpuAdapterState>();
            var hist = svc.History.Events;
            var physical = monitors.Where(m => !m.IsVirtual).ToList();

            // ---- Écrans ----
            var byGpu = physical.GroupBy(m => m.AdapterName).ToList();
            if (byGpu.Count > 1)
                Add("Écrans", "Écrans répartis sur plusieurs cartes graphiques", CheckStatus.Warning,
                    string.Join(" ; ", byGpu.Select(g => $"{g.Key} : {string.Join(", ", g.Select(m => m.Name + " (" + m.Connector + ")"))}")),
                    "Mélanger iGPU (carte mère) et carte dédiée est une cause fréquente de clignotements et d'écrans noirs (synchronisation entre adaptateurs, MPO). Brancher si possible tous les écrans sur la même carte.");
            else if (physical.Count > 0)
                Add("Écrans", "Tous les écrans sont sur la même carte graphique", CheckStatus.Ok, $"{physical.Count} écran(s) sur {byGpu.FirstOrDefault()?.Key}");

            var rates = physical.Select(m => Math.Round(m.RefreshHz)).Distinct().ToList();
            if (rates.Count > 1)
                Add("Écrans", "Fréquences de rafraîchissement différentes", CheckStatus.Info,
                    string.Join(", ", physical.Select(m => $"{m.Name} {m.RefreshHz:0} Hz")),
                    "Avec le MPO actif, des fréquences différentes favorisent scintillements et écrans noirs. Tester en alignant tous les écrans sur la même fréquence.");

            var conns = physical.GroupBy(m => m.Connector).Select(g => $"{g.Count()}× {g.Key}");
            if (physical.Count > 0) Add("Écrans", "Connectique détectée", CheckStatus.Info, string.Join(", ", conns));

            var virt = monitors.Where(m => m.IsVirtual).ToList();
            var virtAdapters = adapters.Where(a => a.IsVirtual).Select(a => a.Name).ToList();
            if (virt.Count > 0 || virtAdapters.Count > 0)
                Add("Écrans", "Adaptateur(s) d'écran virtuel installé(s)", CheckStatus.Warning,
                    string.Join(", ", virtAdapters.Concat(virt.Select(v => v.Label)).Distinct()),
                    "Les pilotes d'écran virtuel (Parsec VDD, IddCx, spacedesk…) ajoutent/retirent des écrans et forcent Windows à recomposer l'affichage. Les désactiver s'ils ne servent pas.");

            // ---- Pilotes graphiques ----
            foreach (var a in adapters.Where(a => !a.IsVirtual))
            {
                if (a.ConfigError != 0 && a.ConfigError != 22)
                    Add("GPU", $"{a.Name} : périphérique en erreur", CheckStatus.Problem, CfgMgr.ProblemText((uint)a.ConfigError), "Réinstaller le pilote ; vérifier le Gestionnaire de périphériques.");
                int age = a.DriverAgeDays;
                if (age < 0) continue;
                var detail = $"Version {a.DriverVersion} du {a.DriverDate:dd/MM/yyyy} ({age} jours)";
                if (age > 540)
                    Add("GPU", $"Pilote ancien : {a.Name}", CheckStatus.Warning, detail, $"Installer une version récente et stable du pilote {a.Vendor} (de préférence la branche entreprise / certifiée).");
                else
                    Add("GPU", $"Pilote : {a.Name}", CheckStatus.Ok, detail);
                if (a.Nv?.PcieWidth != null && a.Nv.PcieWidthMax != null && a.Nv.PcieWidth < a.Nv.PcieWidthMax)
                    Add("GPU", $"Lien PCIe réduit : {a.Name}", CheckStatus.Warning, $"x{a.Nv.PcieWidth} au lieu de x{a.Nv.PcieWidthMax}", "Vérifier l'insertion de la carte, le slot utilisé et l'éventuel riser.");
            }

            // ---- Réglages graphiques Windows ----
            var overlay = RegInt(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode");
            bool nvidia = adapters.Any(a => a.Vendor == "NVIDIA");
            if (overlay == 5)
                Add("Windows", "MPO (Multi-Plane Overlay) désactivé", CheckStatus.Ok, "OverlayTestMode = 5");
            else
                Add("Windows", "MPO (Multi-Plane Overlay) actif", physical.Count > 1 && nvidia ? CheckStatus.Warning : CheckStatus.Info,
                    "Réglage par défaut de Windows.",
                    "Le MPO est une cause connue d'écrans noirs / scintillements en multi-écrans (surtout NVIDIA). Test : reg add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\Dwm\" /v OverlayTestMode /t REG_DWORD /d 5 /f puis redémarrer (supprimer la valeur pour revenir en arrière ; l'effet dépend de la version de Windows).");

            var hags = RegInt(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode");
            Add("Windows", "Planification GPU à accélération matérielle (HAGS)", hags == 2 ? CheckStatus.Info : CheckStatus.Ok,
                hags == 2 ? "Activée" : hags == 1 ? "Désactivée" : "Valeur par défaut du pilote",
                hags == 2 ? "En cas de TDR ou d'écrans noirs répétés, tester sa désactivation (Paramètres > Système > Affichage > Graphiques)." : null);

            var tdrDelay = RegInt(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "TdrDelay");
            var tdrLevel = RegInt(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "TdrLevel");
            if (tdrLevel == 0)
                Add("Windows", "Récupération TDR désactivée", CheckStatus.Warning, "TdrLevel = 0 : un GPU bloqué fige le PC au lieu de relancer le pilote.", "Supprimer la valeur TdrLevel sauf besoin de débogage.");
            else
                Add("Windows", "Délai TDR (détection de GPU bloqué)", CheckStatus.Info, tdrDelay.HasValue ? $"TdrDelay = {tdrDelay} s" : "Par défaut (2 s)",
                    "Contournement possible si des TDR surviennent sous forte charge : TdrDelay = 8 (REG_DWORD) — ne corrige pas la cause.");

            // ---- Alimentation ----
            var uss = ReadPower(SubUsb, UsbSelectiveSuspend, true);
            if (uss.HasValue)
                Add("Alimentation", "Suspension sélective USB (secteur)", uss == 1 ? CheckStatus.Warning : CheckStatus.Ok, uss == 1 ? "Activée" : "Désactivée",
                    uss == 1 ? "Peut mettre en veille des hubs / docks USB-C et couper écrans et périphériques. À désactiver sur les postes à dock : Options d'alimentation > Paramètres USB." : null);
            var aspm = ReadPower(SubPcie, PcieAspm, true);
            if (aspm.HasValue)
                Add("Alimentation", "Gestion d'alimentation PCI Express (ASPM)", aspm == 2 ? CheckStatus.Warning : aspm == 1 ? CheckStatus.Info : CheckStatus.Ok,
                    aspm == 0 ? "Désactivée" : aspm == 1 ? "Économies modérées" : "Économies maximales",
                    aspm >= 1 ? "Les économies d'énergie PCIe peuvent déstabiliser certaines cartes graphiques : tester « Désactivé »." : null);
            var idle = ReadPower(SubVideo, VideoIdle, true);
            if (idle.HasValue)
                Add("Alimentation", "Extinction automatique des écrans (secteur)", CheckStatus.Info,
                    idle == 0 ? "Jamais" : $"Après {idle / 60} min d'inactivité",
                    idle > 0 ? "Un écran noir après ce délai d'inactivité est normal (Signal Watchdogs le journalise comme « Écrans éteints par Windows »)." : null);

            var hiberboot = RegInt(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled");
            if (hiberboot == 1)
                Add("Alimentation", "Démarrage rapide activé", CheckStatus.Info, "L'arrêt conserve l'état des pilotes (hibernation partielle).",
                    "Pour tester un correctif pilote, utiliser « Redémarrer » plutôt qu'« Arrêter ».");

            // ---- USB / docks ----
            var tracked = svc.Devices?.Tracked ?? new List<PnpDevice>();
            var displayLink = tracked.Where(d => (d.Name ?? "").IndexOf("DisplayLink", StringComparison.OrdinalIgnoreCase) >= 0 || (d.Manufacturer ?? "").IndexOf("DisplayLink", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (displayLink.Count > 0)
                Add("USB", "Matériel DisplayLink détecté", CheckStatus.Info, string.Join(", ", displayLink.Select(d => d.DisplayName).Distinct()),
                    "Les écrans DisplayLink passent par le CPU/USB : mettre à jour le pilote DisplayLink et le firmware du dock.");
            var docks = tracked.Where(d => (d.Name ?? "").IndexOf("dock", StringComparison.OrdinalIgnoreCase) >= 0 || (d.BusName ?? "").IndexOf("dock", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (docks.Count > 0)
                Add("USB", "Station(s) d'accueil détectée(s)", CheckStatus.Info, string.Join(", ", docks.Select(d => d.DisplayName).Distinct()),
                    "Garder le firmware du dock, le BIOS et les pilotes Thunderbolt/USB4 à jour.");
            var problems = svc.Devices?.Problems ?? new List<PnpDevice>();
            var realProblems = problems.Where(p => p.Problem != 22).ToList();
            Add("Périphériques", realProblems.Count > 0 ? $"{realProblems.Count} périphérique(s) en erreur" : "Aucun périphérique en erreur",
                realProblems.Count > 0 ? CheckStatus.Warning : CheckStatus.Ok,
                realProblems.Count > 0 ? string.Join(" ; ", realProblems.Take(8).Select(p => $"{p.DisplayName} — {CfgMgr.ProblemText(p.Problem)}")) : "Gestionnaire de périphériques sain.");

            // ---- Historique ----
            if (svc.History.ScannedAt.HasValue)
            {
                int days = svc.History.Days;
                int tdr = hist.Count(e => Codes.GpuCrashCodes.Contains(e.Code));
                int usb = hist.Count(e => e.Code == Codes.UcsiError || e.Code == Codes.XhciError || e.Code == Codes.UsbError || e.Code == Codes.LiveKernelUsb);
                int kp = hist.Count(e => e.Code == Codes.KernelPower41 || e.Code == Codes.Bsod);
                int dwm = hist.Count(e => e.Code == Codes.DwmCrash);
                Add("Historique", $"Plantages du pilote graphique ({days} j)", tdr > 0 ? CheckStatus.Problem : CheckStatus.Ok, tdr > 0 ? $"{tdr} événement(s) TDR / erreur pilote / LiveKernelEvent GPU" : "Aucun",
                    tdr > 0 ? "Les écrans noirs coïncident très probablement avec ces plantages : voir l'onglet Historique pour les dates et appliquer les recommandations GPU." : null);
                Add("Historique", $"Erreurs USB / USB-C ({days} j)", usb > 0 ? CheckStatus.Warning : CheckStatus.Ok, usb > 0 ? $"{usb} erreur(s) contrôleur xHCI / UCSI / hub" : "Aucune",
                    usb > 0 ? "Sur un poste à dock USB-C, ces erreurs coupent les écrans : mettre à jour BIOS, pilotes chipset/Thunderbolt et firmware du dock." : null);
                if (kp > 0) Add("Historique", $"Plantages système ({days} j)", CheckStatus.Warning, $"{kp} redémarrage(s) brutal(aux) / écran(s) bleu(s)");
                if (dwm > 0) Add("Historique", $"Plantages DWM ({days} j)", CheckStatus.Warning, $"{dwm} plantage(s) du compositeur d'affichage");
            }

            // ---- Système ----
            var uptime = TimeSpan.FromMilliseconds(NativeMethods.GetTickCount64());
            Add("Système", "Durée depuis le dernier démarrage", uptime.TotalDays > 14 ? CheckStatus.Info : CheckStatus.Ok, Fr.Duration(uptime),
                uptime.TotalDays > 14 ? "Un redémarrage régulier évite l'accumulation de fuites mémoire de pilotes." : null);
            Add("Système", "Version de Windows", CheckStatus.Info, OsVersion());
            if (!svc.IsAdmin)
                Add("Signal Watchdogs", "Exécution sans droits administrateur", CheckStatus.Info, "Certaines sources (dossier LiveKernelReports) peuvent être inaccessibles.",
                    "Mode recommandé (moindre privilège). L'administrateur n'est utile que depuis un dossier protégé (C:\\Program Files\\Signal Watchdogs).");
            else if (svc.UnprotectedLocation != null)
                Add("Signal Watchdogs", "Administrateur depuis un emplacement non protégé", CheckStatus.Problem, svc.UnprotectedLocation,
                    "Risque d'élévation de privilèges : un utilisateur peut remplacer l'exe ou y déposer une DLL / un SignalWatchdogs.exe.config piégés. Placer SignalWatchdogs.exe dans C:\\Program Files\\Signal Watchdogs ou le lancer sans droits administrateur.");
            if (svc.Log != null && svc.Log.Disabled)
                Add("Signal Watchdogs", "Journalisation sur disque désactivée", CheckStatus.Warning, svc.Log.LastError, "Vérifier le dossier des journaux (droits d'écriture, jonction refusée en administrateur).");
            return res;
        }

        public static string OsVersion()
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                var product = k?.GetValue("ProductName") as string ?? "Windows";
                var build = k?.GetValue("CurrentBuild") as string ?? "";
                var display = k?.GetValue("DisplayVersion") as string ?? "";
                var ubr = k?.GetValue("UBR");
                if (int.TryParse(build, out int b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
                return $"{product} {display} (build {build}{(ubr != null ? "." + ubr : "")})";
            }
            catch { return Environment.OSVersion.VersionString; }
        }

        static int? RegInt(RegistryKey root, string path, string name)
        {
            try
            {
                using var k = root.OpenSubKey(path);
                var v = k?.GetValue(name);
                return v == null ? (int?)null : Convert.ToInt32(v);
            }
            catch { return null; }
        }

        static uint? ReadPower(Guid sub, Guid setting, bool ac)
        {
            try
            {
                if (NativeMethods.PowerGetActiveScheme(IntPtr.Zero, out IntPtr p) != 0) return null;
                try
                {
                    var scheme = (Guid)Marshal.PtrToStructure(p, typeof(Guid));
                    uint v;
                    uint r = ac ? NativeMethods.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out v)
                                : NativeMethods.PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out v);
                    return r == 0 ? v : (uint?)null;
                }
                finally { NativeMethods.LocalFree(p); }
            }
            catch { return null; }
        }
    }
}
