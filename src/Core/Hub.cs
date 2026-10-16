using System;
using System.Collections.Generic;
using System.Linq;

namespace SignalWatchdogs.Core
{
    /// <summary>
    /// Bus central : toutes les sources publient ici. Conserve un tampon mémoire,
    /// alimente la journalisation disque et le moteur d'incidents.
    /// </summary>
    public static class Hub
    {
        const int Capacity = 25000;
        static readonly object Lock = new object();
        static readonly List<WatchEvent> Events = new List<WatchEvent>(Capacity + 2048);
        static long _seq;
        static readonly Dictionary<string, (DateTime start, int count, int dropped)> Throttle = new Dictionary<string, (DateTime, int, int)>();
        static readonly Dictionary<string, SourceStatus> SourceMap = new Dictionary<string, SourceStatus>();

        public static LogWriter Log;
        public static IncidentEngine Incidents;
        public static Settings Settings;
        public static readonly DateTime StartedAt = DateTime.Now;

        /// <summary>Déclenché sur le thread de l'émetteur : les abonnés UI doivent marshaller.</summary>
        public static event Action<WatchEvent> Published;

        public static long LastSeq { get { lock (Lock) return _seq; } }

        public static void Publish(WatchEvent e, string source = null)
        {
            if (e == null) return;
            if (source != null) e.Source = source;
            int droppedSummary = 0;
            lock (Lock)
            {
                // Double limitation par minute : par titre exact (répétitions) et par code + source
                // (rafales dont le titre varie, ex. événements forgés dans le journal Application).
                if (!Allow(e.Code + "|" + e.Title, 20, ref droppedSummary)) return;
                int ignored = 0;
                if (!Allow("S|" + e.Code + "|" + e.Source, 200, ref ignored)) return;

                e.Seq = ++_seq;
                Events.Add(e);
                if (Events.Count > Capacity + 2000) Events.RemoveRange(0, Events.Count - Capacity);
            }

            if (droppedSummary > 0)
                e.Details = (e.Details ?? "") + $" ({droppedSummary} occurrences similaires regroupées sur la minute précédente)";

            try { Log?.WriteEvent(e); } catch { }
            try { Incidents?.OnEvent(e); } catch { }
            try { Published?.Invoke(e); } catch { }
        }

        /// <summary>Fenêtre glissante d'une minute ; à appeler sous verrou.</summary>
        static bool Allow(string key, int limit, ref int droppedSummary)
        {
            var now = DateTime.Now;
            if (Throttle.TryGetValue(key, out var t) && (now - t.start).TotalSeconds < 60)
            {
                if (t.count >= limit)
                {
                    Throttle[key] = (t.start, t.count, t.dropped + 1);
                    return false;
                }
                Throttle[key] = (t.start, t.count + 1, t.dropped);
                return true;
            }
            if (Throttle.TryGetValue(key, out var old)) droppedSummary = old.dropped;
            if (Throttle.Count > 20000)
                foreach (var stale in Throttle.Where(kv => (now - kv.Value.start).TotalSeconds >= 60).Select(kv => kv.Key).ToList()) Throttle.Remove(stale);
            Throttle[key] = (now, 1, 0);
            return true;
        }

        public static WatchEvent Publish(Severity sev, EventCategory cat, string code, string title, string details = null, bool trigger = false, string source = null)
        {
            var e = new WatchEvent(sev, cat, code, title, details, trigger);
            Publish(e, source);
            return e;
        }

        public static List<WatchEvent> Since(long seq, int max = int.MaxValue)
        {
            lock (Lock)
            {
                int idx = LowerBound(seq + 1);
                int count = Math.Min(max, Events.Count - idx);
                return count <= 0 ? new List<WatchEvent>() : Events.GetRange(idx, count);
            }
        }

        public static List<WatchEvent> Range(DateTime from, DateTime to)
        {
            lock (Lock) return Events.Where(e => e.Time >= from && e.Time <= to).ToList();
        }

        public static List<WatchEvent> All()
        {
            lock (Lock) return new List<WatchEvent>(Events);
        }

        public static int Count(Func<WatchEvent, bool> pred)
        {
            lock (Lock) return Events.Count(pred);
        }

        static int LowerBound(long seq)
        {
            int lo = 0, hi = Events.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (Events[mid].Seq < seq) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        // ---- Statut des sources ----
        public static void SetSource(string name, bool ok, string detail)
        {
            lock (SourceMap) SourceMap[name] = new SourceStatus { Name = name, Ok = ok, Detail = detail, Updated = DateTime.Now };
        }

        public static List<SourceStatus> Sources()
        {
            lock (SourceMap) return SourceMap.Values.OrderBy(s => s.Name).ToList();
        }
    }
}
