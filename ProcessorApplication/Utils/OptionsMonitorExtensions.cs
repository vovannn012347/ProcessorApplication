using Microsoft.Extensions.Options;

namespace ProcessorApplication.Utils;

public static class OptionsMonitorExtensions
{
    /// <summary>Default quiet period: long enough to swallow one reload burst, short enough to feel instant.</summary>
    public static readonly TimeSpan DefaultSettingsQuietPeriod = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Like IOptionsMonitor.OnChange, but collapses a burst of change notifications into ONE call,
    /// made once nothing has changed for <paramref name="quietPeriod"/>, with the current value.
    ///
    /// Why this is needed: a single settings save normally produces several notifications.
    ///  - AddModuleSettings registers every settings class twice on the same section (module-named
    ///    and default-named - both are read in the app), so every reload fires each listener twice.
    ///  - Saves that don't batch their writes (SetAutoUpdate(false) ... ForceUpdateOptionsMonitor())
    ///    reload once PER KEY written.
    /// Listeners that rebuild timers, reconnect, or re-announce identity should run once per save,
    /// not once per notification.
    ///
    /// The listener runs on a thread-pool thread, never inline with the reload. Exceptions are
    /// caught so a faulty listener can't break the timer. Dispose the result to unsubscribe.
    /// </summary>
    public static IDisposable OnChangeDebounced<T>(this IOptionsMonitor<T> monitor, Action<T> listener, TimeSpan? quietPeriod = null)
    {
        return new DebouncedOptionsListener<T>(monitor, listener, quietPeriod ?? DefaultSettingsQuietPeriod);
    }

    private sealed class DebouncedOptionsListener<T> : IDisposable
    {
        private readonly IOptionsMonitor<T> _monitor;
        private readonly Action<T> _listener;
        private readonly TimeSpan _quietPeriod;
        private readonly Timer _timer;
        private readonly IDisposable? _registration;
        private volatile bool _disposed;

        public DebouncedOptionsListener(IOptionsMonitor<T> monitor, Action<T> listener, TimeSpan quietPeriod)
        {
            _monitor = monitor;
            _listener = listener;
            _quietPeriod = quietPeriod;
            _timer = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
            _registration = monitor.OnChange((_, _) => Restart());
        }

        // Every notification pushes the single pending call further out; only the last one fires.
        private void Restart()
        {
            if (_disposed) return;
            try { _timer.Change(_quietPeriod, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { /* raced with Dispose */ }
        }

        private void Fire()
        {
            if (_disposed) return;
            try
            {
                _listener(_monitor.CurrentValue);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Settings] Debounced settings listener for {typeof(T).Name} threw: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _registration?.Dispose();
            _timer.Dispose();
        }
    }
}
