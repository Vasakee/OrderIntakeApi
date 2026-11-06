namespace OrderIntakeApi.Services;

public class KeyedIdempotencyLock
{
    private readonly object _sync = new();
    private readonly Dictionary<string, int> _activeKeys = new(StringComparer.Ordinal);

    public bool TryAcquire(string key, out IDisposable? lease)
    {
        lock (_sync)
        {
            if (_activeKeys.ContainsKey(key))
            {
                lease = null;
                return false;
            }

            _activeKeys[key] = 1;
            lease = new Lease(this, key);
            return true;
        }
    }

    private void Release(string key)
    {
        lock (_sync)
        {
            _activeKeys.Remove(key);
        }
    }

    private sealed class Lease : IDisposable
    {
        private readonly KeyedIdempotencyLock _owner;
        private readonly string _key;
        private int _disposed;

        public Lease(KeyedIdempotencyLock owner, string key)
        {
            _owner = owner;
            _key = key;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.Release(_key);
            }
        }
    }
}
