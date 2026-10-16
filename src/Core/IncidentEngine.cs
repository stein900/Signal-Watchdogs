using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace SignalWatchdogs.Core
{
    public sealed class ContextSnapshot
    {
        public List<string> Apps = new List<string>();
        public string Session = "";
        public string Gpu = "";
        public string Display = "";
        public bool EncoderActive;
    }

    public enum IncidentChange { Opened, Updated, Closed }

    /// <summary>
    /// Regroupe les événements déclencheurs proches dans le temps en "incidents",
    /// y rattache le contexte (sessions, applis, USB…) et calcule un diagnostic.
    /// </summary>
    public sealed class IncidentEngine : IDisposable
    {
        const double MergeSeconds = 20;          // deux déclencheurs à moins de 20 s = même incident
        const double UserReportMergeSeconds = 150; // un signalement rejoint l'incident des 2,5 dernières minutes
        const double CloseAfterSeconds = 30;
        const double LookbackSeconds = 60;
        const double UserLookbackSeconds = 120;
        const double MaxIncidentMinutes = 15;      // au-delà, un nouvel incident est ouvert (évite un incident sans fin)
        const int MaxContextEvents = 500;          // plafonds anti-saturation (événements forgés en rafale)
        const int MaxEvents = 1000;

        readonly object _lock = new object();
        readonly List<Incident> _incidents = new List<Incident>();
        readonly Timer _timer;
        int _nextId = 1;

        public Func<ContextSnapshot> ContextProvider;
        public event Action<Incident, IncidentChange> Changed;

        public IncidentEngine()
        {
            _timer = new Timer(_ => CloseExpired(), null, 5000, 5000);
        }

        public void Restore(IEnumerable<Incident> restored)
        {
            lock (_lock)
            {
                // Fichiers relus = données potentiellement altérées : identifiants bornés, volume borné.
                var valid = restored
                    .Where(i => i.Id > 0 && i.Id < 100_000_000)
                    .GroupBy(i => i.Id + "|" + i.Start.ToString("o"))
                    .Select(g => g.Last())
                    .OrderBy(i => i.Start)
                    .ToList();
                _incidents.AddRange(valid.Skip(Math.Max(0, valid.Count - 2000)));
                if (_incidents.Count > 0) _nextId = _incidents.Max(i => i.Id) + 1;
            }
        }

        public List<Incident> Snapshot()
        {
            lock (_lock) return new List<Incident>(_incidents);
        }

        public int OpenCount { get { lock (_lock) return _incidents.Count(i => !i.Closed); } }

        public void OnEvent(WatchEvent e)
        {
            if (e.Historical) return;
            Incident target = null;
            IncidentChange change = IncidentChange.Updated;
            lock (_lock)
            {
                var latest = _incidents.LastOrDefault(i => !i.Restored);
                if (e.Trigger)
                {
                    bool isUser = e.Code == Codes.UserReport;
                    double gap = isUser ? UserReportMergeSeconds : MergeSeconds;
                    if (latest != null && (e.Time - latest.LastTrigger).TotalSeconds <= gap && (!latest.Closed || isUser)
                        && (e.Time - latest.Start).TotalMinutes < MaxIncidentMinutes)
                    {
                        target = latest;
                        if (latest.Closed) { latest.Closed = false; }
                        Attach(latest, e);
                        latest.LastTrigger = e.Time;
                        if (e.Time > latest.End) latest.End = e.Time;
                    }
                    else
                    {
                        target = new Incident { Id = _nextId++, Start = e.Time, End = e.Time, LastTrigger = e.Time };
                        change = IncidentChange.Opened;
                        Attach(target, e);
                        // Contexte précédent (session RDP qui s'ouvre, appli lancée, USB retiré…)
                        var lookback = isUser ? UserLookbackSeconds : LookbackSeconds;
                        foreach (var prev in Hub.Range(e.Time.AddSeconds(-lookback), e.Time))
                            if (prev != e && prev.IncidentId == 0 && IsContext(prev)) Attach(target, prev);
                        var ctx = SafeContext();
                        if (ctx != null)
                        {
                            target.ActiveApps = ctx.Apps;
                            target.SessionState = ctx.Session;
                            target.Context = string.Join(" · ", new[] { ctx.Session, ctx.Display, ctx.Gpu }.Where(s => !string.IsNullOrEmpty(s)));
                            if (ctx.EncoderActive) target.Context += " · Encodeur vidéo GPU actif";
                        }
                        _incidents.Add(target);
                        if (_incidents.Count > 2000) _incidents.RemoveAt(0);
                    }
                }
                else if (latest != null && !latest.Closed && e.Time <= latest.LastTrigger.AddSeconds(CloseAfterSeconds) && IsContext(e))
                {
                    target = latest;
                    Attach(latest, e);
                }

                if (target != null) Diagnoser.Apply(target);
            }
            if (target != null) Raise(target, change);
        }

        /// <summary>Les messages internes de Signal Watchdogs (démarrage, inventaires) ne sont pas du contexte utile.</summary>
        static bool IsContext(WatchEvent e) => e.Category != EventCategory.Watchdog && e.Code != Codes.WatchdogInfo;

        static void Attach(Incident inc, WatchEvent e)
        {
            lock (inc.Events)
            {
                if (inc.Events.Count >= MaxEvents || (!e.Trigger && inc.Events.Count >= MaxContextEvents)) return;
                if (inc.Events.Contains(e)) return;
                e.IncidentId = inc.Id;
                int idx = inc.Events.FindLastIndex(x => x.Time <= e.Time);
                inc.Events.Insert(idx + 1, e);
            }
            if (e.Time < inc.Start && e.Trigger) inc.Start = e.Time;
        }

        ContextSnapshot SafeContext()
        {
            try { return ContextProvider?.Invoke(); } catch { return null; }
        }

        void CloseExpired()
        {
            var closed = new List<Incident>();
            lock (_lock)
            {
                foreach (var inc in _incidents.Where(i => !i.Closed && !i.Restored))
                {
                    if ((DateTime.Now - inc.LastTrigger).TotalSeconds > CloseAfterSeconds)
                    {
                        inc.Closed = true;
                        Diagnoser.Apply(inc);
                        closed.Add(inc);
                    }
                }
            }
            foreach (var inc in closed)
            {
                try { Hub.Log?.WriteIncident(inc); } catch { }
                Raise(inc, IncidentChange.Closed);
            }
        }

        void Raise(Incident inc, IncidentChange change)
        {
            try { Changed?.Invoke(inc, change); } catch { }
        }

        /// <summary>Construit des incidents à partir d'événements historiques (journaux Windows).</summary>
        public static List<Incident> BuildFromHistory(List<WatchEvent> events)
        {
            var res = new List<Incident>();
            var sorted = events.OrderBy(e => e.Time).ToList();
            Incident cur = null;
            int id = 1;
            foreach (var e in sorted.Where(x => x.Trigger))
            {
                if (cur != null && (e.Time - cur.LastTrigger).TotalSeconds <= 60)
                {
                    cur.Events.Add(e);
                    cur.LastTrigger = cur.End = e.Time;
                    continue;
                }
                cur = new Incident { Id = id++, Start = e.Time, End = e.Time, LastTrigger = e.Time, Closed = true, Historical = true };
                cur.Events.Add(e);
                res.Add(cur);
            }
            foreach (var inc in res)
            {
                var from = inc.Start.AddSeconds(-90);
                var to = inc.End.AddSeconds(60);
                foreach (var e in sorted)
                {
                    if (e.Time < from) continue;
                    if (e.Time > to) break;
                    if (!e.Trigger && !inc.Events.Contains(e)) inc.Events.Add(e);
                }
                inc.Events.Sort((a, b) => a.Time.CompareTo(b.Time));
                Diagnoser.Apply(inc);
            }
            return res;
        }

        public void Dispose() => _timer.Dispose();
    }
}
