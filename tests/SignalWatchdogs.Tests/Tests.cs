using System;
using System.Collections.Generic;
using System.Linq;
using SignalWatchdogs.Core;

namespace SignalWatchdogs.Tests
{
    /// <summary>Scénarios synthétiques : chaque séquence d'événements doit produire la bonne cause.</summary>
    static class Tests
    {
        static int _passed, _failed;
        static readonly DateTime T0 = new DateTime(2026, 9, 15, 14, 0, 0);

        static WatchEvent Ev(double sec, Severity sev, EventCategory cat, string code, string title, bool trigger, params (string k, object v)[] data)
        {
            var e = new WatchEvent(sev, cat, code, title, "", trigger) { Time = T0.AddSeconds(sec) };
            foreach (var (k, v) in data) e.With(k, v);
            return e;
        }

        static WatchEvent Lost(double s, string mon, string gpu, int adapterTotal = 1, int before = 3, string conn = "DisplayPort") =>
            Ev(s, Severity.Error, EventCategory.Display, Codes.DisplayLost, "Écran perdu : " + mon, true, ("monitor", mon), ("gpu", gpu), ("adapter_total", adapterTotal), ("active_before", before), ("connector", conn), ("virtual", "0"));

        static WatchEvent Back(double s, string mon, string gpu, double outageMs, string conn = "DisplayPort") =>
            Ev(s, Severity.Warning, EventCategory.Display, Codes.DisplayBack, "Écran revenu : " + mon, true, ("monitor", mon), ("gpu", gpu), ("outage_ms", (long)outageMs), ("connector", conn), ("virtual", "0"));

        static Incident Inc(bool closed, params WatchEvent[] evs)
        {
            var inc = new Incident { Id = 1, Start = evs.Min(e => e.Time), End = evs.Max(e => e.Time), LastTrigger = evs.Where(e => e.Trigger).Select(e => e.Time).DefaultIfEmpty(T0).Max(), Closed = closed };
            inc.Events.AddRange(evs.OrderBy(e => e.Time));
            return inc;
        }

        static void Expect(string name, Incident inc, string cause, string factorContains = null)
        {
            Diagnoser.Apply(inc);
            bool ok = inc.Diagnosis.Cause == cause && (factorContains == null || inc.Diagnosis.Factors.Any(f => f.Contains(factorContains)) || inc.Diagnosis.Title.Contains(factorContains));
            if (ok) _passed++; else _failed++;
            Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {name,-48} → {inc.Diagnosis.Cause,-16} | {inc.Diagnosis.Title}");
            if (!ok) Console.WriteLine($"       attendu {cause}{(factorContains != null ? " + « " + factorContains + " »" : "")} ; facteurs : {string.Join(" / ", inc.Diagnosis.Factors)}");
        }

        static int Main()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            const string nv = "NVIDIA GeForce GTX 1060 6GB", intel = "Intel(R) UHD Graphics 770";

            Expect("TDR + 2 écrans NVIDIA perdus/revenus",
                Inc(true, Ev(0, Severity.Critical, EventCategory.Gpu, Codes.Tdr, "TDR", true, ("provider", "Display")),
                    Lost(0.3, "DELL A", nv, 2), Lost(0.3, "DELL B", nv, 2), Back(3.1, "DELL A", nv, 2800), Back(3.2, "DELL B", nv, 2900)),
                "GPU_DRIVER", "Durée du noir mesurée");

            Expect("Un seul écran perd le signal 2,4 s",
                Inc(true, Lost(0, "DELL A", nv, 2), Back(2.4, "DELL A", nv, 2400)), "CABLE_MONITOR", "2,4 s");

            Expect("Écran + hub USB + erreur xHCI (dock)",
                Inc(true, Ev(-0.5, Severity.Error, EventCategory.Usb, Codes.XhciError, "xHCI", true), Lost(0, "DELL A", intel, 1, 2, "DP via USB-C/USB4"),
                    Ev(0.2, Severity.Warning, EventCategory.Usb, Codes.UsbHubRemoved, "Hub retiré", true), Back(4, "DELL A", intel, 4000, "DP via USB-C/USB4")),
                "DOCK_USBC");

            Expect("Extinction écran puis perte DP (veille profonde)",
                Inc(true, Ev(-2, Severity.Notice, EventCategory.Power, Codes.DisplayPowerOff, "Écrans éteints", false), Lost(0, "DELL A", nv), Back(600, "DELL A", nv, 600000)),
                "POWER_SAVE");

            Expect("Connexion RDP entrante puis bascule écrans",
                Inc(true, Ev(-1, Severity.Notice, EventCategory.Session, Codes.RemoteConnect, "RDP", false), Lost(0, "DELL A", nv, 2), Lost(0, "DELL B", nv, 2)),
                "RDP_SESSION");

            Expect("Signalement utilisateur seul (un écran)",
                Inc(true, Ev(0, Severity.Error, EventCategory.User, Codes.UserReport, "Signalement", true, ("scope", "one"))), "PHYSICAL_LINK");

            Expect("Signalement utilisateur seul (tous les écrans)",
                Inc(true, Ev(0, Severity.Error, EventCategory.User, Codes.UserReport, "Signalement", true, ("scope", "all"))), "GPU_SILENT");

            Expect("2 écrans de la même carte (sur 2) sans TDR",
                Inc(true, Lost(0, "DELL A", nv, 2, 3), Lost(0.1, "DELL B", nv, 2, 3), Back(2, "DELL A", nv, 2000), Back(2, "DELL B", nv, 1900)),
                "GPU_OUTPUTS");

            var mode = Inc(true, Ev(0, Severity.Warning, EventCategory.Display, Codes.DisplayMode, "Mode modifié", true, ("monitor", "DELL A")));
            mode.ActiveApps = new List<string> { "Parsec" };
            Expect("Changement de mode avec Parsec ouvert", mode, "MODE_CHANGE", "Parsec");

            Expect("Redémarrage de dwm.exe",
                Inc(true, Ev(0, Severity.Critical, EventCategory.Display, Codes.DwmRestart, "DWM", true), Lost(0.2, "DELL A", nv)), "DWM");

            Expect("Écran perdu sans retour (débranché)",
                Inc(true, Lost(0, "DELL A", nv)), "UNPLUGGED");

            Expect("Écran virtuel Parsec ajouté/retiré",
                Inc(true, Ev(0, Severity.Notice, EventCategory.Display, Codes.DisplayLost, "Écran perdu : Parsec", true, ("monitor", "Parsec"), ("virtual", "1")),
                    Ev(5, Severity.Notice, EventCategory.Display, Codes.DisplayBack, "Écran revenu : Parsec", true, ("monitor", "Parsec"), ("virtual", "1"), ("outage_ms", 5000))),
                "VIRTUAL_DISPLAY");

            Expect("GPU perdu (fallen off the bus)",
                Inc(true, Ev(0, Severity.Critical, EventCategory.Gpu, Codes.GpuLost, "GPU perdu", true), Lost(0.5, "DELL A", nv)), "GPU_LOST");

            Expect("Gel système seul",
                Inc(true, Ev(0, Severity.Warning, EventCategory.System, Codes.Stall, "Gel", true, ("duration", "2,3 s"))), "STALL");

            Expect("Signalement après TDR → reste un TDR",
                Inc(true, Ev(0, Severity.Critical, EventCategory.Gpu, Codes.LiveKernelGpu, "LiveKernelEvent 141", true),
                    Ev(40, Severity.Error, EventCategory.User, Codes.UserReport, "Signalement", true)), "GPU_DRIVER", "signalement");

            // ---- Regroupement temps réel par le moteur d'incidents ----
            var engine = new IncidentEngine();
            Hub.Incidents = engine;
            var now = DateTime.Now;
            void Pub(WatchEvent e, double offset) { e.Time = now.AddSeconds(offset); Hub.Publish(e); }
            Pub(new WatchEvent(Severity.Notice, EventCategory.Session, Codes.RemoteConnect, "RDP", null), -10);
            Pub(new WatchEvent(Severity.Error, EventCategory.Display, Codes.DisplayLost, "Écran perdu", null, true).With("monitor", "A").With("gpu", nv), -5);
            Pub(new WatchEvent(Severity.Error, EventCategory.Display, Codes.DisplayLost, "Écran perdu", null, true).With("monitor", "B").With("gpu", nv), -4.9);
            var snap = engine.Snapshot();
            bool merged = snap.Count == 1 && snap[0].Events.Count == 3 && snap[0].Diagnosis.Cause == "RDP_SESSION";
            Console.WriteLine($"{(merged ? "OK  " : "FAIL")} {"Moteur : regroupement + contexte antérieur",-48} → {snap.Count} incident(s), {snap.FirstOrDefault()?.Events.Count} év., {snap.FirstOrDefault()?.Diagnosis.Cause}");
            if (merged) _passed++; else _failed++;
            Pub(new WatchEvent(Severity.Error, EventCategory.Gpu, Codes.Tdr, "TDR", null, true), 60);
            snap = engine.Snapshot();
            bool separate = snap.Count == 2;
            Console.WriteLine($"{(separate ? "OK  " : "FAIL")} {"Moteur : déclencheur éloigné → nouvel incident",-48} → {snap.Count} incident(s)");
            if (separate) _passed++; else _failed++;

            // ---- Persistance JSON ----
            var inc0 = snap[0];
            var round = Incident.FromDict(new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(
                new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(inc0.ToDict())) as Dictionary<string, object>);
            bool persisted = round.Diagnosis.Title == inc0.Diagnosis.Title && round.Events.Count == inc0.Events.Count && Math.Abs((round.Start - inc0.Start).TotalSeconds) < 1;
            Console.WriteLine($"{(persisted ? "OK  " : "FAIL")} {"Persistance JSON d'un incident",-48} → {round.Events.Count} év., « {round.Diagnosis.Title} »");
            if (persisted) _passed++; else _failed++;

            Console.WriteLine($"\n{_passed} réussi(s), {_failed} échec(s)");
            return _failed == 0 ? 0 : 1;
        }
    }
}
