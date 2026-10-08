using System;
using System.Collections.Generic;
using InvestorDataAutomation.Core.Interfaces;

namespace InvestorDataAutomation.Services
{
    /// <summary>
    /// Priority-ordered crawl queue keyed by <see cref="UrlNormalizer.PageKey"/>. Re-adding a URL with a
    /// higher priority upgrades it; stale lower-priority entries are skipped on dequeue. The number of
    /// pending URLs is capped so link-heavy sites cannot grow the queue without bound.
    /// </summary>
    internal sealed class CrawlFrontier
    {
        private readonly PriorityQueue<InvestorPageCandidate, (int NegPriority, long Order)> _queue = new();
        private readonly Dictionary<string, InvestorPageCandidate> _best = new(StringComparer.Ordinal);
        private readonly HashSet<string> _closed = new(StringComparer.Ordinal);
        private readonly int _maxPending;
        private long _order;

        public CrawlFrontier(int maxPending = int.MaxValue) => _maxPending = Math.Max(1, maxPending);

        public int PendingCount => _best.Count;
        public int RejectedForCapacity { get; private set; }

        /// <returns>True if the candidate was queued or upgraded.</returns>
        public bool TryAdd(InvestorPageCandidate candidate)
        {
            var key = UrlNormalizer.PageKey(candidate.Url);
            if (_closed.Contains(key)) return false;

            if (_best.TryGetValue(key, out var existing))
            {
                if (candidate.Priority <= existing.Priority) return false;
                candidate = candidate with { Depth = Math.Min(candidate.Depth, existing.Depth) };
            }
            else if (_best.Count >= _maxPending)
            {
                RejectedForCapacity++;
                return false;
            }

            _best[key] = candidate;
            _queue.Enqueue(candidate, (-candidate.Priority, _order++));
            return true;
        }

        public bool TryDequeue(out InvestorPageCandidate candidate)
        {
            while (_queue.TryDequeue(out var next, out _))
            {
                var key = UrlNormalizer.PageKey(next.Url);
                if (!_best.TryGetValue(key, out var best) || !ReferenceEquals(best, next)) continue;

                _best.Remove(key);
                _closed.Add(key);
                candidate = next;
                return true;
            }

            candidate = null!;
            return false;
        }

        /// <summary>Marks a URL as handled (visited, redirect target or known-invalid) so it is never fetched again.</summary>
        public void Close(string url)
        {
            var key = UrlNormalizer.PageKey(url);
            _closed.Add(key);
            _best.Remove(key);
        }

        public bool IsClosed(string url) => _closed.Contains(UrlNormalizer.PageKey(url));
    }
}
