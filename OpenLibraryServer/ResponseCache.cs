using System;
using System.Collections.Concurrent;
using System.Threading;

namespace OpenLibraryServer
{
    public sealed class ResponseCache : IDisposable
    {
        private readonly ConcurrentDictionary<string, Entry> _map = new ConcurrentDictionary<string, Entry>();
        private readonly TimeSpan _ttl;
        private readonly Timer _gc;
        private long _hits, _misses;

        private sealed class Entry
        {
            public byte[] Payload { get; set; }
            public DateTimeOffset ExpiresAt { get; set; }
        }

        public ResponseCache(TimeSpan ttl)
        {
            _ttl = ttl;
            _gc = new Timer(_ => Cleanup(), null, _ttl, _ttl);
        }

        public bool TryGet(string key, out byte[] payload)
        {
            if (_map.TryGetValue(key, out var e))
            {
                if (e.ExpiresAt > DateTimeOffset.UtcNow)
                {
                    Interlocked.Increment(ref _hits);
                    payload = e.Payload;
                    return true;
                }
                _map.TryRemove(key, out _);
            }
            Interlocked.Increment(ref _misses);
            payload = Array.Empty<byte>();
            return false;
        }

        public void Set(string key, byte[] payload)
        {
            _map[key] = new Entry
            {
                Payload = payload,
                ExpiresAt = DateTimeOffset.UtcNow + _ttl
            };
        }

        public object Stats => new
        {
            hits = Interlocked.Read(ref _hits),
            misses = Interlocked.Read(ref _misses),
            count = _map.Count,
            ttl_seconds = (int)_ttl.TotalSeconds
        };

        private void Cleanup()
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var kv in _map)
            {
                if (kv.Value.ExpiresAt <= now)
                    _map.TryRemove(kv.Key, out _);
            }
        }

        public void Dispose() => _gc.Dispose();
    }
}
