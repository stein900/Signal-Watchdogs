using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using SignalWatchdogs.Core;
using SignalWatchdogs.Monitors;
using SignalWatchdogs.Native;

namespace SignalWatchdogs.Diagnostics
{
    /// <summary>Rapport HTML autonome (un seul fichier) à transmettre au support / à l'équipe IT.</summary>
    public static class HtmlReport
    {
        static string H(object s) => WebUtility.HtmlEncode(Convert.ToString(s) ?? "");

        static string SevClass(Severity s) => s switch
        {
            Severity.Critical => "crit",
            Severity.Error => "err",
            Severity.Warning => "warn",
            Severity.Notice => "notice",
            _ => "info"
        };

        static string StatusClass(CheckStatus s) => s switch
        {
            CheckStatus.Problem => "err",
            CheckStatus.Warning => "warn",
            CheckStatus.Info => "notice",
            _ => "ok"
        };

        public static string Build(WatchdogService svc)
        {
            var now = DateTime.Now;
            var sb = new StringBuilder();
            var monitors = svc.Topology?.Current ?? new List<MonitorInfo>();
            var stats = (svc.Topology?.Stats() ?? new List<MonitorStats>()).ToDictionary(s => s.Key, s => s);
            var adapters = svc.Gpu?.Adapters ?? new List<GpuAdapterState>();
            var incidents = svc.AllIncidents().OrderByDescending(i => i.Start).ToList();
            var hist = svc.History;
            var checks = svc.Checks;
            var uptime = TimeSpan.FromMilliseconds(NativeMethods.GetTickCount64());

            sb.Append(@"<!doctype html><html lang=""fr""><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1"">");
            // Défense en profondeur : aucun script, aucune ressource externe ne peut s'exécuter dans le rapport.
            sb.Append(@"<meta http-equiv=""Content-Security-Policy"" content=""default-src 'none'; style-src 'unsafe-inline'; img-src data:"">");
            sb.Append($"<title>Signal Watchdogs — {H(Environment.MachineName)}</title><style>");
            sb.Append(@"
:root{--bg:#f6f8fa;--card:#fff;--text:#1f2328;--muted:#656d76;--border:#d0d7de;--accent:#2f6feb;--ok:#1a7f37;--warn:#9a6700;--err:#cf222e;--crit:#a40e26;--notice:#0969da}
@media (prefers-color-scheme:dark){:root{--bg:#0d1117;--card:#161b22;--text:#e6edf3;--muted:#8b949e;--border:#30363d;--accent:#4f8cff;--ok:#3fb950;--warn:#d29922;--err:#f85149;--crit:#ff3b6b;--notice:#58a6ff}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.5 'Segoe UI',system-ui,sans-serif}
.wrap{max-width:1200px;margin:0 auto;padding:24px 16px 60px}
header{display:flex;gap:16px;align-items:center;flex-wrap:wrap;margin-bottom:8px}
h1{font-size:24px;margin:0}h2{font-size:18px;margin:32px 0 10px;border-bottom:1px solid var(--border);padding-bottom:6px}h3{font-size:15px;margin:0 0 4px}
.meta{color:var(--muted);font-size:13px}
.card{background:var(--card);border:1px solid var(--border);border-radius:10px;padding:14px 16px;margin:10px 0}
.kpis{display:grid;grid-template-columns:repeat(auto-fit,minmax(170px,1fr));gap:10px}
.kpi{background:var(--card);border:1px solid var(--border);border-radius:10px;padding:12px 14px}
.kpi b{display:block;font-size:26px}.kpi span{color:var(--muted);font-size:12px}
table{width:100%;border-collapse:collapse;font-size:13px}th,td{text-align:left;padding:6px 8px;border-bottom:1px solid var(--border);vertical-align:top}
th{color:var(--muted);font-weight:600;background:var(--bg)}
.scroll{overflow-x:auto}
.tag{display:inline-block;padding:1px 8px;border-radius:10px;font-size:12px;font-weight:600;color:#fff;white-space:nowrap}
.tag.ok{background:var(--ok)}.tag.warn{background:var(--warn)}.tag.err{background:var(--err)}.tag.crit{background:var(--crit)}.tag.notice{background:var(--notice)}.tag.info{background:var(--muted)}
.verdict li{margin:4px 0}
details{margin-top:8px}summary{cursor:pointer;color:var(--accent)}
ol,ul{margin:6px 0 6px 20px;padding:0}
.muted{color:var(--muted)}
");
            sb.Append("</style></head><body><div class=\"wrap\">");

            // ---- En-tête ----
            sb.Append($"<header><h1>Rapport Signal Watchdogs — {H(Environment.MachineName)}</h1></header>");
            sb.Append($"<div class=\"meta\">Généré le {now:dd/MM/yyyy à HH:mm} · {H(SystemDiagnostics.OsVersion())} · utilisateur {H(Environment.UserName)} · " +
                      $"{(svc.IsAdmin ? "administrateur" : "droits standard")} · démarré depuis {H(Fr.Duration(uptime))} · Signal Watchdogs {H(WatchdogService.Version)} actif depuis {H(Fr.Duration(now - svc.StartedAt))}</div>");

            // ---- Synthèse ----
            var since30 = now.AddDays(-30);
            int gpu30 = svc.CombinedCount(e => Codes.GpuCrashCodes.Contains(e.Code), since30);
            int usb30 = svc.CombinedCount(e => e.Code == Codes.UcsiError || e.Code == Codes.XhciError || e.Code == Codes.UsbError || e.Code == Codes.LiveKernelUsb, since30);
            int lostTotal = stats.Values.Sum(s => s.Disconnects);
            int reports = Hub.Count(e => e.Code == Codes.UserReport);
            sb.Append("<h2>Synthèse</h2><div class=\"kpis\">");
            Kpi(sb, monitors.Count.ToString(), "écran(s) actif(s)");
            Kpi(sb, incidents.Count(i => i.Start >= now.AddDays(-7)).ToString(), "incident(s) Signal Watchdogs (7 j)");
            Kpi(sb, lostTotal.ToString(), "perte(s) d'écran mesurée(s)");
            Kpi(sb, gpu30.ToString(), "plantage(s) pilote GPU (30 j)");
            Kpi(sb, usb30.ToString(), "erreur(s) USB / USB-C (30 j)");
            Kpi(sb, reports.ToString(), "signalement(s) utilisateur");
            sb.Append("</div>");

            var causes = incidents.Concat(hist.Incidents).Where(i => !string.IsNullOrEmpty(i.Diagnosis.Title))
                .GroupBy(i => i.Diagnosis.Cause).Select(g => (cause: g.Key, title: g.OrderByDescending(i => i.Start).First().Diagnosis.Title, count: g.Count(), last: g.Max(i => i.Start)))
                .OrderByDescending(x => x.count).ToList();
            sb.Append("<div class=\"card verdict\"><h3>Causes probables observées</h3>");
            if (causes.Count == 0) sb.Append("<p class=\"muted\">Aucun incident enregistré pour l'instant. Laisser Signal Watchdogs tourner et utiliser le raccourci de signalement lors du prochain écran noir.</p>");
            else
            {
                sb.Append("<ul>");
                foreach (var c in causes.Take(8)) sb.Append($"<li><b>{H(c.title)}</b> — {c.count} fois (dernière : {c.last:dd/MM HH:mm})</li>");
                sb.Append("</ul>");
            }
            var important = checks.Where(c => c.Status >= CheckStatus.Warning).OrderByDescending(c => c.Status).ToList();
            if (important.Count > 0)
            {
                sb.Append("<h3 style=\"margin-top:12px\">Points de configuration à corriger</h3><ul>");
                foreach (var c in important) sb.Append($"<li><span class=\"tag {StatusClass(c.Status)}\">{H(StatusText(c.Status))}</span> <b>{H(c.Title)}</b> — {H(c.Detail)}</li>");
                sb.Append("</ul>");
            }
            sb.Append("</div>");

            // ---- Écrans ----
            sb.Append("<h2>Écrans</h2><div class=\"card scroll\"><table><tr><th>Écran</th><th>Modèle</th><th>N° de série</th><th>Connectique</th><th>Mode</th><th>Carte graphique</th><th>Pertes</th><th>Noir cumulé</th><th>Dernière perte</th></tr>");
            foreach (var m in monitors)
            {
                stats.TryGetValue(m.Key, out var st);
                sb.Append($"<tr><td>{(m.Primary ? "★ " : "")}{H(m.ShortGdi)}</td><td>{H(m.Name)}{(m.IsVirtual ? " <span class=\"tag warn\">virtuel</span>" : "")}</td><td>{H((m.EdidMfg + " " + m.Serial).Trim())}</td>" +
                          $"<td>{H(m.Connector)}</td><td>{H(m.ModeText)}</td><td>{H(m.AdapterName)}</td><td>{st?.Disconnects ?? 0}</td><td>{H(st != null ? Fr.Duration(st.Downtime) : "—")}</td><td>{H(st?.LastLost?.ToString("dd/MM HH:mm:ss") ?? "—")}</td></tr>");
            }
            foreach (var st in stats.Values.Where(s => s.CurrentlyLost))
                sb.Append($"<tr><td colspan=\"9\"><span class=\"tag err\">PERDU</span> {H(st.Label)} ({H(st.Connector)}) — {st.Disconnects} perte(s)</td></tr>");
            sb.Append("</table></div>");

            // ---- GPU ----
            sb.Append("<h2>Cartes graphiques</h2><div class=\"card scroll\"><table><tr><th>Carte</th><th>Pilote</th><th>Date du pilote</th><th>VRAM</th><th>Charge</th><th>Télémétrie</th><th>État</th></tr>");
            foreach (var a in adapters)
            {
                int age = a.DriverAgeDays;
                var tele = a.Nv != null ? $"{a.Nv.Temp:0} °C · {a.Nv.Power:0} W · PCIe Gen{a.Nv.PcieGen:0} x{a.Nv.PcieWidth:0}/{a.Nv.PcieWidthMax:0} · {a.Nv.PState}" : "—";
                sb.Append($"<tr><td>{H(a.Name)}{(a.IsVirtual ? " <span class=\"tag warn\">virtuel</span>" : "")}</td><td>{H(a.DriverVersion)}</td>" +
                          $"<td>{H(a.DriverDate?.ToString("dd/MM/yyyy"))}{(age > 540 ? $" <span class=\"tag warn\">{age} j</span>" : "")}</td>" +
                          $"<td>{(a.VramTotal > 0 ? H(Fr.Bytes(a.VramUsed) + " / " + Fr.Bytes(a.VramTotal)) : "—")}</td><td>{(a.HasLuid ? $"{a.Util:0} % (encodeur {a.UtilEncode:0} %)" : "—")}</td>" +
                          $"<td>{H(tele)}</td><td>{(a.ConfigError != 0 && a.ConfigError != 22 ? $"<span class=\"tag err\">{H(CfgMgr.ProblemText((uint)a.ConfigError))}</span>" : H(a.Status ?? "OK"))}</td></tr>");
            }
            sb.Append("</table></div>");

            // ---- Incidents ----
            sb.Append("<h2>Incidents détectés par Signal Watchdogs (7 derniers jours)</h2>");
            var recentIncs = incidents.Where(i => i.Start >= now.AddDays(-7)).Take(100).ToList();
            if (recentIncs.Count == 0) sb.Append("<div class=\"card muted\">Aucun incident.</div>");
            foreach (var inc in recentIncs) IncidentCard(sb, inc);

            // ---- Historique ----
            sb.Append($"<h2>Historique Windows ({hist.Days} jours)</h2>");
            if (!hist.ScannedAt.HasValue) sb.Append("<div class=\"card muted\">Historique non analysé.</div>");
            else
            {
                sb.Append($"<div class=\"meta\">{hist.Events.Count} événements pertinents trouvés dans les journaux Windows, {hist.Incidents.Count} incident(s) reconstitué(s).</div>");
                if (hist.Incidents.Count > 0)
                {
                    sb.Append("<div class=\"card scroll\"><table><tr><th>Date</th><th>Gravité</th><th>Cause probable</th><th>Événements</th></tr>");
                    foreach (var inc in hist.Incidents.OrderByDescending(i => i.Start).Take(200))
                        sb.Append($"<tr><td>{inc.Start:dd/MM/yyyy HH:mm:ss}</td><td><span class=\"tag {SevClass(inc.Severity)}\">{H(Fr.Sev(inc.Severity))}</span></td><td>{H(inc.ShortTitle)}</td>" +
                                  $"<td>{H(string.Join(" · ", inc.Events.Where(e => e.Trigger).Select(e => e.Title).Distinct().Take(4)))}</td></tr>");
                    sb.Append("</table></div>");
                }
                var notable = hist.Events.Where(e => e.Trigger || e.Severity >= Severity.Warning || e.Code == Codes.DriverInstall).OrderByDescending(e => e.Time).Take(400).ToList();
                sb.Append("<details><summary>Événements notables (" + notable.Count + ")</summary><div class=\"card scroll\"><table><tr><th>Date</th><th>Gravité</th><th>Catégorie</th><th>Événement</th><th>Source</th></tr>");
                foreach (var e in notable)
                    sb.Append($"<tr><td>{e.Time:dd/MM HH:mm:ss}</td><td><span class=\"tag {SevClass(e.Severity)}\">{H(Fr.Sev(e.Severity))}</span></td><td>{H(Fr.Cat(e.Category))}</td><td>{H(e.Title)}</td><td class=\"muted\">{H(e.Source)}</td></tr>");
                sb.Append("</table></div></details>");
            }

            // ---- Audit ----
            sb.Append("<h2>Audit de configuration</h2><div class=\"card scroll\"><table><tr><th>Statut</th><th>Domaine</th><th>Vérification</th><th>Constat</th><th>Recommandation</th></tr>");
            foreach (var c in checks.OrderByDescending(c => c.Status))
                sb.Append($"<tr><td><span class=\"tag {StatusClass(c.Status)}\">{H(StatusText(c.Status))}</span></td><td>{H(c.Area)}</td><td><b>{H(c.Title)}</b></td><td>{H(c.Detail)}</td><td>{H(c.Recommendation)}</td></tr>");
            sb.Append("</table></div>");

            // ---- Périphériques ----
            var probs = svc.Devices?.Problems ?? new List<PnpDevice>();
            var tracked = svc.Devices?.Tracked ?? new List<PnpDevice>();
            sb.Append("<h2>Périphériques</h2>");
            if (probs.Count > 0)
            {
                sb.Append("<div class=\"card scroll\"><h3>En erreur</h3><table><tr><th>Périphérique</th><th>Classe</th><th>Problème</th><th>ID</th></tr>");
                foreach (var p in probs) sb.Append($"<tr><td>{H(p.DisplayName)}</td><td>{H(p.Class)}</td><td>{H(CfgMgr.ProblemText(p.Problem))}</td><td class=\"muted\">{H(p.InstanceId)}</td></tr>");
                sb.Append("</table></div>");
            }
            sb.Append($"<details><summary>Périphériques USB / affichage suivis ({tracked.Count})</summary><div class=\"card scroll\"><table><tr><th>Nom</th><th>Classe</th><th>État</th><th>ID</th></tr>");
            foreach (var d in tracked) sb.Append($"<tr><td>{H(d.DisplayName)}</td><td>{H(d.Class)}</td><td>{H(d.StateText)}</td><td class=\"muted\">{H(d.InstanceId)}</td></tr>");
            sb.Append("</table></div></details>");

            // ---- Journal récent ----
            var live = Hub.All().Where(e => e.Severity >= Severity.Notice).OrderByDescending(e => e.Time).Take(300).ToList();
            sb.Append($"<h2>Journal récent (session Signal Watchdogs)</h2><details><summary>{live.Count} événements (Notice et plus)</summary><div class=\"card scroll\"><table><tr><th>Heure</th><th>Gravité</th><th>Catégorie</th><th>Événement</th><th>Détails</th></tr>");
            foreach (var e in live)
                sb.Append($"<tr><td>{e.Time:dd/MM HH:mm:ss.fff}</td><td><span class=\"tag {SevClass(e.Severity)}\">{H(Fr.Sev(e.Severity))}</span></td><td>{H(Fr.Cat(e.Category))}</td><td>{(e.Trigger ? "⚑ " : "")}{H(e.Title)}</td><td class=\"muted\">{H(Trunc(e.Details, 300))}</td></tr>");
            sb.Append("</table></div></details>");

            // ---- Sources ----
            sb.Append("<h2>Sources de surveillance</h2><div class=\"card scroll\"><table><tr><th>État</th><th>Source</th><th>Détail</th></tr>");
            foreach (var s in Hub.Sources())
                sb.Append($"<tr><td><span class=\"tag {(s.Ok ? "ok" : "err")}\">{(s.Ok ? "Actif" : "Inactif")}</span></td><td>{H(s.Name)}</td><td class=\"muted\">{H(s.Detail)}</td></tr>");
            sb.Append("</table></div>");

            sb.Append($"<p class=\"meta\">Journaux bruts : {H(svc.Log?.MachineDirectory)}</p>");
            sb.Append("</div></body></html>");
            return sb.ToString();
        }

        static void Kpi(StringBuilder sb, string value, string label) => sb.Append($"<div class=\"kpi\"><b>{H(value)}</b><span>{H(label)}</span></div>");

        static string StatusText(CheckStatus s) => s switch { CheckStatus.Ok => "OK", CheckStatus.Info => "Info", CheckStatus.Warning => "Attention", _ => "Problème" };

        static string Trunc(string s, int n) => string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s.Substring(0, n) + "…";

        static void IncidentCard(StringBuilder sb, Incident inc)
        {
            var d = inc.Diagnosis;
            sb.Append("<div class=\"card\">");
            sb.Append($"<h3><span class=\"tag {SevClass(inc.Severity)}\">{H(Fr.Sev(inc.Severity))}</span> {H(inc.ShortTitle)}</h3>");
            sb.Append($"<div class=\"meta\">{inc.Start:dd/MM/yyyy HH:mm:ss} · confiance {H(d.Confidence)}{(inc.AffectedMonitors.Count > 0 ? " · écran(s) : " + H(string.Join(", ", inc.AffectedMonitors)) : "")}{(inc.ActiveApps.Count > 0 ? " · applis : " + H(string.Join(", ", inc.ActiveApps)) : "")}</div>");
            sb.Append($"<p>{H(d.Explanation)}</p>");
            if (d.Factors.Count > 0) { sb.Append("<ul>"); foreach (var f in d.Factors) sb.Append($"<li>{H(f)}</li>"); sb.Append("</ul>"); }
            if (d.Recommendations.Count > 0) { sb.Append("<b>Que faire ?</b><ol>"); foreach (var r in d.Recommendations) sb.Append($"<li>{H(r)}</li>"); sb.Append("</ol>"); }
            sb.Append($"<details><summary>Chronologie ({inc.Events.Count} événements)</summary><table><tr><th>Heure</th><th>Gravité</th><th>Catégorie</th><th>Événement</th></tr>");
            foreach (var e in inc.EventList().OrderBy(x => x.Time))
                sb.Append($"<tr><td>{e.Time:HH:mm:ss.fff}</td><td><span class=\"tag {SevClass(e.Severity)}\">{H(Fr.Sev(e.Severity))}</span></td><td>{H(Fr.Cat(e.Category))}</td><td>{(e.Trigger ? "⚑ " : "")}{H(e.Title)}<div class=\"muted\">{H(Trunc(e.Details, 400))}</div></td></tr>");
            sb.Append("</table></details></div>");
        }
    }
}
