using System;
using System.Collections.Generic;
using System.Linq;

namespace SignalWatchdogs.Core
{
    /// <summary>
    /// Moteur de règles : à partir des événements d'un incident, déduit la cause la plus
    /// probable de l'écran noir et propose des actions concrètes.
    /// </summary>
    public static class Diagnoser
    {
        static readonly string[] StreamingKeywords = { "Parsec", "Teams", "Bureau à distance", "Remote Desktop", "Zoom", "AnyDesk", "TeamViewer", "RustDesk", "OBS", "Webex", "GoTo", "Discord", "Splashtop", "VNC", "Sunshine", "Moonlight", "Hyper-V", "VMware" };

        public static void Apply(Incident inc)
        {
            var ev = inc.Events;
            var d = new Diagnosis();

            var triggers = ev.Where(e => e.Trigger).ToList();
            inc.Severity = triggers.Count > 0 ? triggers.Max(e => e.Severity) : Severity.Notice;

            var lost = ev.Where(e => e.Code == Codes.DisplayLost).ToList();
            var back = ev.Where(e => e.Code == Codes.DisplayBack).ToList();
            var monitorNames = ev.Where(e => e.Code == Codes.DisplayLost || e.Code == Codes.DisplayBack || e.Code == Codes.DisplayMode || e.Code == Codes.DisplayNew)
                                 .Select(e => e.Get("monitor")).Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
            inc.AffectedMonitors = monitorNames;

            bool gpuLost = inc.Has(Codes.GpuLost) || inc.Has(Codes.GpuAdapterLost);
            bool gpuCrash = inc.HasAny(Codes.GpuCrashCodes);
            bool dwm = inc.Has(Codes.DwmCrash) || inc.Has(Codes.DwmRestart);
            bool bsod = inc.Has(Codes.KernelPower41) || inc.Has(Codes.Bsod) || inc.Has(Codes.UnexpectedShutdown);
            bool modeChange = inc.Has(Codes.DisplayMode) || inc.Has(Codes.DisplayPrimary);
            bool usbFault = inc.HasAny(Codes.UsbFaultCodes);
            bool usbRemoved = inc.Has(Codes.UsbRemoved) || inc.Has(Codes.UsbHubRemoved) || inc.Has(Codes.PnpMissing);
            bool powerOff = inc.Has(Codes.DisplayPowerOff) || inc.Has(Codes.Suspend) || inc.Has(Codes.DisplayDimmed);
            bool sessionSwitch = inc.HasAny(Codes.SessionSwitchCodes);
            bool userReport = inc.Has(Codes.UserReport);
            bool stall = inc.Has(Codes.Stall);
            bool explorer = inc.Has(Codes.ExplorerRestart);
            bool virtualOnly = (lost.Count + back.Count) > 0 && lost.Concat(back).All(e => e.Get("virtual") == "1");
            bool virtualEvent = inc.Has(Codes.VirtualDisplay) || virtualOnly;
            bool encoder = inc.Has(Codes.GpuEncodeOn) || (inc.Context ?? "").Contains("Encodeur vidéo GPU actif");

            var apps = new List<string>(inc.ActiveApps ?? new List<string>());
            apps.AddRange(ev.Where(e => e.Code == Codes.AppStart).Select(e => e.Get("app")).Where(a => a != null));
            apps = apps.Distinct().ToList();
            var streamingApps = apps.Where(a => StreamingKeywords.Any(k => a.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            string appsText = streamingApps.Count > 0 ? string.Join(", ", streamingApps) : null;

            double outageMs = back.Select(e => double.TryParse(e.Get("outage_ms"), out var v) ? v : 0).DefaultIfEmpty(0).Max();
            string outage = outageMs > 0 ? Fr.Duration(TimeSpan.FromMilliseconds(outageMs)) : null;

            string vendor = DetectVendor(ev);

            if (gpuLost)
            {
                d.Title = "Carte graphique perdue (décrochage PCIe ou arrêt complet du pilote)";
                d.Cause = "GPU_LOST";
                d.Confidence = "Haute";
                d.Explanation = "Windows a perdu la carte graphique elle-même (elle a disparu de l'énumération ou ne répond plus). Tous les écrans qui y sont reliés deviennent noirs, parfois sans retour avant redémarrage.";
                d.Recommendations.AddRange(new[]
                {
                    "Vérifier l'alimentation de la carte (câbles PCIe 6/8 broches, bloc d'alimentation suffisant).",
                    "Réinsérer la carte / vérifier la carte mère (slot PCIe) et le riser éventuel.",
                    "Contrôler les températures (onglet GPU) et dépoussiérer.",
                    "Installer proprement la dernière version stable du pilote" + (vendor != null ? " " + vendor : "") + ".",
                    "Mettre à jour le BIOS / désactiver l'ASPM PCIe dans le plan d'alimentation."
                });
            }
            else if (bsod)
            {
                d.Title = "Plantage système (écran bleu / redémarrage brutal)";
                d.Cause = "SYSTEM_CRASH";
                d.Confidence = "Haute";
                d.Explanation = "Le système a redémarré de façon inattendue ou a rencontré une erreur fatale. L'écran noir était la conséquence d'un plantage global, pas seulement de l'affichage.";
                d.Recommendations.AddRange(new[]
                {
                    "Consulter les codes d'arrêt (BugCheck) dans l'onglet Historique.",
                    "Mettre à jour pilotes graphiques, chipset et BIOS.",
                    "Tester la mémoire (mdsched) et vérifier l'alimentation."
                });
            }
            else if (gpuCrash)
            {
                d.Title = "Plantage et redémarrage du pilote graphique (TDR)" + (vendor != null ? " — " + vendor : "");
                d.Cause = "GPU_DRIVER";
                d.Confidence = "Haute";
                d.Explanation = "Windows a détecté que la carte graphique ne répondait plus et a relancé son pilote (Timeout Detection & Recovery). " +
                                "Pendant cette relance, tous les écrans reliés à cette carte deviennent noirs quelques secondes puis reviennent : c'est le symptôme classique de l'écran qui « saute ».";
                d.Recommendations.Add("Réinstaller proprement le pilote " + (vendor ?? "graphique") + " (désinstallation complète avec DDU en mode sans échec, puis installation de la dernière version stable / entreprise).");
                d.Recommendations.Add("Si le problème est apparu après une mise à jour du pilote ou de Windows : revenir à la version précédente du pilote.");
                if (appsText != null)
                    d.Recommendations.Add($"Le plantage survient pendant l'utilisation de {appsText} : désactiver l'accélération matérielle dans ces applications (Teams : Paramètres > Général ; Parsec : décodeur/encodeur logiciel) pour confirmer.");
                d.Recommendations.Add("Désactiver le MPO (Multi-Plane Overlay), cause connue d'écrans noirs en multi-écrans — voir l'onglet Diagnostic.");
                d.Recommendations.Add("Tester la désactivation de la « Planification GPU à accélération matérielle » (Paramètres > Système > Affichage > Graphiques).");
                d.Recommendations.Add("Vérifier températures et alimentation du GPU (onglet GPU).");
                d.Recommendations.Add("Contournement temporaire : augmenter TdrDelay (registre GraphicsDrivers) à 8 s.");
            }
            else if (dwm)
            {
                d.Title = "Redémarrage du compositeur d'affichage Windows (DWM)";
                d.Cause = "DWM";
                d.Confidence = "Haute";
                d.Explanation = "DWM (Desktop Window Manager) dessine tout le bureau. Quand il plante ou redémarre, tous les écrans clignotent en noir 1 à 3 secondes.";
                d.Recommendations.AddRange(new[]
                {
                    "Mettre à jour le pilote graphique et Windows.",
                    "Repérer les applications qui s'injectent dans l'affichage (overlays Teams/Parsec/NVIDIA/Discord, outils de capture) et les désactiver une à une.",
                    "Désactiver le MPO (voir onglet Diagnostic)."
                });
            }
            else if (lost.Count > 0 || back.Count > 0)
            {
                var lostNames = lost.Select(e => e.Get("monitor")).Where(s => s != null).Distinct().ToList();
                var lostGpus = lost.Select(e => e.Get("gpu")).Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
                var connectors = lost.Concat(back).Select(e => e.Get("connector")).Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
                bool usbcLink = connectors.Any(c => c.Contains("USB") || c.Contains("Indirect") || c.Contains("DisplayLink"));
                int adapterTotal = lost.Select(e => int.TryParse(e.Get("adapter_total"), out var n) ? n : 0).DefaultIfEmpty(0).Max();
                int activeBefore = lost.Select(e => int.TryParse(e.Get("active_before"), out var n) ? n : 0).DefaultIfEmpty(0).Max();

                if (virtualOnly)
                {
                    d.Title = "Ajout/retrait d'un écran virtuel (Parsec / streaming / IddCx)";
                    d.Cause = "VIRTUAL_DISPLAY";
                    d.Confidence = "Haute";
                    d.Explanation = "Un écran virtuel a été créé ou supprimé (ex. Parsec Virtual Display, outil de streaming). Windows recompose alors toute la disposition : les écrans physiques peuvent clignoter.";
                    d.Recommendations.Add("Dans Parsec (hôte) : désactiver les écrans virtuels / le changement de résolution si inutile.");
                    d.Recommendations.Add("Désinstaller les pilotes d'écran virtuel non utilisés (voir onglet Diagnostic).");
                }
                else if (powerOff && !usbFault && !gpuCrash)
                {
                    d.Title = "Écrans déconnectés lors de la mise en veille (économie d'énergie)";
                    d.Cause = "POWER_SAVE";
                    d.Confidence = "Moyenne";
                    d.Explanation = "Windows a éteint les écrans (délai d'inactivité ou mise en veille). De nombreux écrans DisplayPort / USB-C se « débranchent » logiquement en veille profonde, ce qui provoque l'écran noir et la réorganisation des fenêtres au réveil.";
                    d.Recommendations.AddRange(new[]
                    {
                        "Si l'extinction n'était pas voulue : ajuster le délai d'extinction de l'écran (plan d'alimentation).",
                        "Dans le menu de l'écran (OSD) : désactiver « DP Deep Sleep / Veille profonde / Auto-détection de source ».",
                        "Mettre à jour le firmware de l'écran et du dock."
                    });
                }
                else if (sessionSwitch)
                {
                    d.Title = "Bascule de session (Bureau à distance / changement d'utilisateur)";
                    d.Cause = "RDP_SESSION";
                    d.Confidence = "Haute";
                    d.Explanation = "Une session Bureau à distance s'est ouverte ou fermée sur ce poste (ou la console a été reprise). Windows bascule alors l'affichage : les écrans physiques deviennent noirs/verrouillés, puis la disposition est réappliquée au retour.";
                    d.Recommendations.AddRange(new[]
                    {
                        "Comportement normal si quelqu'un s'est connecté en RDP sur ce poste : vérifier dans le Journal (catégorie Session) qui et quand.",
                        "Pour les utilisateurs qui se connectent depuis ce poste : utiliser un client RDP à jour et éviter de basculer « Utiliser tous mes moniteurs » en cours de session."
                    });
                }
                else if (usbFault || (usbRemoved && usbcLink) || inc.Has(Codes.UsbHubRemoved))
                {
                    d.Title = "Décrochage du dock / de la liaison USB-C";
                    d.Cause = "DOCK_USBC";
                    d.Confidence = usbFault ? "Haute" : "Moyenne";
                    d.Explanation = "Les écrans et des périphériques USB ont été coupés au même moment : le dock, le câble USB-C/Thunderbolt ou le contrôleur USB-C a réinitialisé la liaison.";
                    d.Recommendations.AddRange(new[]
                    {
                        "Mettre à jour le firmware du dock, le BIOS et le pilote Thunderbolt/USB4/chipset.",
                        "Utiliser un câble USB-C/Thunderbolt certifié et court (idéalement celui d'origine du dock).",
                        "Vérifier la puissance de l'alimentation du dock (W) par rapport au PC portable.",
                        "Désactiver la « Suspension sélective USB » dans le plan d'alimentation (voir Diagnostic).",
                        "Pour tester : brancher un écran directement sur le PC (sans dock) et comparer."
                    });
                    if (connectors.Any(c => c.Contains("Indirect")))
                        d.Recommendations.Add("Écran via DisplayLink détecté : mettre à jour le pilote DisplayLink (synaptics.com).");
                }
                else if (inc.Closed && lost.Count > 0 && back.Count == 0 && !userReport)
                {
                    d.Title = lostNames.Count > 1 ? $"{lostNames.Count} écrans débranchés ou éteints (signal non revenu)" : $"Écran débranché ou éteint : {lostNames.FirstOrDefault() ?? "écran"}";
                    d.Cause = "UNPLUGGED";
                    d.Confidence = "Faible";
                    d.Explanation = "L'écran a disparu et n'est pas revenu dans les secondes suivantes. Si quelqu'un a débranché le câble, éteint l'écran ou retiré le PC du dock, c'est normal. " +
                                    "Sinon, l'écran n'arrive plus à se resynchroniser avec la carte graphique.";
                    d.Recommendations.AddRange(new[]
                    {
                        "Vérifier que l'écran est allumé et sur la bonne entrée (HDMI / DisplayPort).",
                        "Débrancher / rebrancher le câble côté PC et côté écran ; tester un autre câble.",
                        "Si l'écran revient seulement après un redémarrage : suspecter le pilote graphique ou le dock."
                    });
                }
                else if (lostNames.Count >= 2 && lostGpus.Count == 1 && adapterTotal > 0 && lostNames.Count >= adapterTotal)
                {
                    d.Title = $"Tous les écrans de la carte {lostGpus[0]} ont décroché en même temps";
                    d.Cause = "GPU_OUTPUTS";
                    d.Confidence = "Moyenne";
                    d.Explanation = "Toutes les sorties d'une même carte graphique ont été réinitialisées simultanément, sans plantage de pilote journalisé. L'origine est côté carte/pilote (changement de mode, MPO, gestion d'alimentation PCIe, renégociation HDCP) plutôt que les câbles.";
                    d.Recommendations.AddRange(new[]
                    {
                        "Mettre à jour (ou revenir en arrière) le pilote de cette carte.",
                        "Désactiver le MPO et tester la désactivation de la planification GPU matérielle.",
                        "Désactiver la gestion d'alimentation PCIe (ASPM) dans le plan d'alimentation.",
                        "Si les écrans sont répartis sur deux cartes (iGPU + GPU) : tout brancher sur la même carte."
                    });
                }
                else if (lostNames.Count >= 2)
                {
                    bool all = activeBefore > 0 && lostNames.Count >= activeBefore;
                    d.Title = all ? "Tous les écrans ont décroché simultanément" : $"{lostNames.Count} écrans ont décroché simultanément";
                    d.Cause = "MULTI_DISPLAY";
                    d.Confidence = "Moyenne";
                    d.Explanation = "Plusieurs écrans ont perdu le signal au même instant : la cause est commune (pilote graphique, dock, reconfiguration Windows) plutôt qu'un câble isolé.";
                    d.Recommendations.AddRange(new[]
                    {
                        "Mettre à jour le pilote graphique ; désactiver le MPO (onglet Diagnostic).",
                        "Si les écrans passent par un dock ou un hub : tester en direct sur la carte graphique.",
                        "Vérifier si une application (Parsec, Teams, jeu plein écran) change la résolution au même moment."
                    });
                }
                else
                {
                    var name = lostNames.FirstOrDefault() ?? monitorNames.FirstOrDefault() ?? "un écran";
                    var conn = connectors.FirstOrDefault();
                    d.Title = $"Perte de signal sur {name}" + (conn != null ? $" ({conn})" : "");
                    d.Cause = usbcLink ? "DOCK_USBC" : "CABLE_MONITOR";
                    d.Confidence = "Moyenne";
                    d.Explanation = "Un seul écran a été électriquement « débranché » (perte du signal Hot-Plug)" + (outage != null ? $" pendant {outage}" : "") +
                                    ". Les autres écrans n'ont pas bougé : la cause est locale à cet écran — câble, connecteur, port, adaptateur, dock ou l'écran lui-même (veille, HDCP).";
                    d.Recommendations.AddRange(new[]
                    {
                        "Remplacer le câble par un câble certifié (DisplayPort 1.4 / HDMI 2.0 « Premium ») et éviter les adaptateurs passifs HDMI↔DP.",
                        "Essayer un autre port de la carte graphique / du dock.",
                        "Dans le menu de l'écran : désactiver « DP Deep Sleep », « Auto-source » ; tester DP 1.1 au lieu de 1.2/MST.",
                        "Mettre à jour le firmware de l'écran.",
                        "Si cela se répète toujours sur le même écran (voir onglet Écrans) : échanger l'écran avec un autre poste pour isoler le matériel."
                    });
                }
            }
            else if (modeChange)
            {
                d.Title = "Changement de mode d'affichage (résolution / fréquence / disposition)";
                d.Cause = "MODE_CHANGE";
                d.Confidence = "Moyenne";
                d.Explanation = "La résolution, la fréquence ou la disposition d'un écran a changé : l'écran se resynchronise (1 à 3 s de noir)." +
                                (appsText != null ? $" Applications actives au même moment : {appsText}." : "");
                if (sessionSwitch) d.Explanation += " Une bascule de session (RDP) a eu lieu au même moment.";
                d.Recommendations.AddRange(new[]
                {
                    "Parsec (hôte) : désactiver l'adaptation automatique de résolution.",
                    "Jeux / applis plein écran : utiliser le mode « fenêtré sans bordure ».",
                    "Aligner les fréquences des écrans (ex. tous à 60 Hz) si elles diffèrent."
                });
            }
            else if (usbFault)
            {
                d.Title = "Erreur du contrôleur USB / USB-C";
                d.Cause = "USB_CONTROLLER";
                d.Confidence = "Moyenne";
                d.Explanation = "Le contrôleur USB (xHCI) ou le gestionnaire de port USB-C (UCSI) a signalé une erreur ou s'est réinitialisé. Sur un poste à dock USB-C, cela coupe souvent écrans et périphériques.";
                d.Recommendations.AddRange(new[]
                {
                    "Mettre à jour BIOS, pilotes chipset / Thunderbolt / USB4 et firmware du dock.",
                    "Désactiver la suspension sélective USB (plan d'alimentation).",
                    "Changer le câble USB-C ; tester un autre port USB-C."
                });
            }
            else if (usbRemoved)
            {
                d.Title = "Déconnexion inattendue de périphériques USB";
                d.Cause = "USB_DEVICE";
                d.Confidence = "Faible";
                d.Explanation = "Des périphériques USB (hub, dock, clé…) ont disparu sans action de l'utilisateur.";
                d.Recommendations.Add("Vérifier câbles et hubs, désactiver la suspension sélective USB.");
            }
            else if (explorer)
            {
                d.Title = "Redémarrage de l'Explorateur Windows (barre des tâches)";
                d.Cause = "EXPLORER";
                d.Confidence = "Moyenne";
                d.Explanation = "explorer.exe a redémarré : la barre des tâches et le bureau disparaissent un instant.";
                d.Recommendations.Add("Repérer une extension du shell ou un logiciel qui fait planter l'Explorateur (Journal > Application).");
            }
            else if (stall)
            {
                var stallEv = ev.First(e => e.Code == Codes.Stall);
                d.Title = "Gel du système" + (stallEv.Get("duration") != null ? $" ({stallEv.Get("duration")})" : "");
                d.Cause = "STALL";
                d.Confidence = "Faible";
                d.Explanation = "Le système entier s'est figé un instant (aucun thread n'a pu s'exécuter). Cela accompagne souvent un pilote bloqué (graphique, stockage, USB) ou une saturation CPU/disque.";
                d.Recommendations.Add("Comparer avec la charge CPU/GPU au même moment (onglet GPU) et vérifier les erreurs disque dans l'Observateur d'événements.");
            }
            else if (userReport && ev.Any(e => e.Code == Codes.UserReport && e.Get("scope") == "all"))
            {
                d.Title = "Tous les écrans noirs sans événement Windows : carte graphique / pilote suspecté";
                d.Cause = "GPU_SILENT";
                d.Confidence = "Moyenne";
                d.Explanation = "Plusieurs écrans sont devenus noirs en même temps alors que Windows n'a enregistré ni déconnexion, ni plantage de pilote. " +
                                "Une coupure simultanée sur plusieurs écrans pointe vers leur point commun : la carte graphique (sorties réinitialisées, MPO, gestion d'alimentation) ou le dock qui les alimente.";
                d.Recommendations.AddRange(new[]
                {
                    "Désactiver le MPO puis observer si le phénomène disparaît (voir onglet Diagnostic).",
                    "Mettre à jour / réinstaller proprement le pilote graphique (ou revenir à la version précédente).",
                    "Si les écrans passent par un dock : tester sans le dock, mettre à jour son firmware.",
                    "Désactiver l'ASPM PCIe et la suspension sélective USB dans le plan d'alimentation.",
                    "Continuer à signaler chaque occurrence pour confirmer le motif."
                });
            }
            else if (userReport)
            {
                d.Title = "Aucune anomalie vue par Windows : perte de signal côté écran / câble";
                d.Cause = "PHYSICAL_LINK";
                d.Confidence = "Moyenne";
                d.Explanation = "Vous avez signalé un écran noir, mais Windows n'a vu aucune déconnexion, aucun plantage de pilote et aucun changement de mode. " +
                                "Le PC « croit » donc que l'écran affiche normalement : la coupure se produit en aval — liaison câble/connecteur, électronique de l'écran (renégociation HDCP, mode éco, veille), adaptateur ou dock.";
                d.Recommendations.AddRange(new[]
                {
                    "Noter si UN seul écran ou TOUS les écrans sautent : un seul → câble/écran ; tous en même temps → carte graphique/pilote (MPO).",
                    "Échanger le câble et le port ; supprimer adaptateurs et rallonges.",
                    "Désactiver dans l'écran : DP Deep Sleep, Auto-source, Eco/Smart power ; mettre à jour son firmware.",
                    "Vérifier l'alimentation électrique de l'écran (multiprise, onduleur).",
                    "Continuer à signaler chaque occurrence (Ctrl+Alt+Shift+B) : la répétition permet de confirmer le motif."
                });
            }
            else
            {
                var first = triggers.FirstOrDefault();
                d.Title = first != null ? first.Title : "Anomalie détectée";
                d.Cause = "OTHER";
                d.Confidence = "Faible";
                d.Explanation = "Événement anormal détecté ; aucune règle de diagnostic spécifique ne s'applique.";
            }

            // ---- Facteurs secondaires ----
            if (outage != null) d.Factors.Add("Durée du noir mesurée : " + outage);
            if (monitorNames.Count > 0) d.Factors.Add("Écran(s) concerné(s) : " + string.Join(", ", monitorNames));
            if (appsText != null) d.Factors.Add("Applications actives : " + appsText);
            if (encoder) d.Factors.Add("Encodeur vidéo GPU actif (partage d'écran / streaming en cours)");
            if (!string.IsNullOrEmpty(inc.SessionState) && inc.SessionState.Contains("distante")) d.Factors.Add("Session distante (RDP) en cours");
            if (sessionSwitch && d.Cause != "RDP_SESSION") d.Factors.Add("Bascule de session RDP dans la même fenêtre de temps");
            if (gpuCrash && d.Cause != "GPU_DRIVER") d.Factors.Add("Erreur du pilote graphique dans la même fenêtre de temps");
            if (usbFault && d.Cause != "DOCK_USBC" && d.Cause != "USB_CONTROLLER") d.Factors.Add("Erreur USB / USB-C dans la même fenêtre de temps");
            if (powerOff && d.Cause != "POWER_SAVE") d.Factors.Add("Extinction des écrans / mise en veille juste avant");
            if (virtualEvent && d.Cause != "VIRTUAL_DISPLAY") d.Factors.Add("Écran virtuel (Parsec / IddCx) impliqué");
            if (stall && d.Cause != "STALL") d.Factors.Add("Gel système détecté au même moment");
            if (userReport && d.Cause != "PHYSICAL_LINK") d.Factors.Add("Confirmé par un signalement utilisateur");

            inc.Diagnosis = d;
        }

        static string DetectVendor(List<WatchEvent> ev)
        {
            foreach (var e in ev)
            {
                var s = ((e.Source ?? "") + " " + e.Title + " " + e.Details + " " + e.Get("provider") + " " + e.Get("gpu")).ToLowerInvariant();
                if (s.Contains("nvlddmkm") || s.Contains("nvidia")) return "NVIDIA";
                if (s.Contains("amdkm") || s.Contains("atikm") || s.Contains("amdwddmg") || s.Contains("radeon") || s.Contains("amd ")) return "AMD";
                if (s.Contains("igfx") || s.Contains("igdkmd") || s.Contains("intel")) return "Intel";
            }
            return null;
        }
    }
}
