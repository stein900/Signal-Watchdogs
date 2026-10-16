using System;
using System.Collections.Generic;
using System.Linq;

namespace SignalWatchdogs.Core
{
    /// <summary>Série temporelle circulaire (pour les graphiques temps réel).</summary>
    public sealed class TimeSeries
    {
        readonly object _lock = new object();
        readonly Queue<(DateTime t, double v)> _items;
        readonly int _capacity;

        public string Name;
        public double Last { get; private set; } = double.NaN;

        public TimeSeries(string name, int capacity = 1800)
        {
            Name = name;
            _capacity = capacity;
            _items = new Queue<(DateTime, double)>(capacity + 1);
        }

        public void Add(double v) => Add(DateTime.Now, v);

        public void Add(DateTime t, double v)
        {
            lock (_lock)
            {
                _items.Enqueue((t, v));
                while (_items.Count > _capacity) _items.Dequeue();
                Last = v;
            }
        }

        public List<(DateTime t, double v)> Snapshot(TimeSpan window)
        {
            var from = DateTime.Now - window;
            lock (_lock) return _items.Where(x => x.t >= from).ToList();
        }

        public double Max(TimeSpan window)
        {
            var s = Snapshot(window);
            return s.Count == 0 ? 0 : s.Max(x => x.v);
        }

        public double Average(TimeSpan window)
        {
            var s = Snapshot(window);
            return s.Count == 0 ? 0 : s.Average(x => x.v);
        }
    }
}
