using FileTransferModule.Models;

namespace FileTransferModule.Services;

/// <summary>
/// Cheap byte meter: per-second buckets in a small ring plus a running total. Add() on every
/// transferred chunk; the read methods run only when someone actually looks (UI refresh) -
/// nothing runs in between.
/// </summary>
public sealed class TransferRateMeter
{
    // 64 one-second buckets: enough for a 5 s rate and a full "last minute" amount.
    private const int RingSeconds = 64;
    private long _totalBytes;

    /// <summary>All bytes ever added to this meter.</summary>
    public long TotalBytes => Interlocked.Read(ref _totalBytes);

    private readonly long[] _bytes = new long[RingSeconds];
    private readonly long[] _second = new long[RingSeconds];
    private readonly object _lock = new();

    private static long NowSecond => Environment.TickCount64 / 1000;

    public void Add(long bytes)
    {
        if (bytes <= 0) return;
        Interlocked.Add(ref _totalBytes, bytes);
        long sec = NowSecond;
        int idx = (int)(sec % RingSeconds);
        lock (_lock)
        {
            if (_second[idx] != sec)
            {
                _second[idx] = sec;
                _bytes[idx] = 0;
            }
            _bytes[idx] += bytes;
        }
    }

    /// <summary>
    /// Average over the last <paramref name="windowSeconds"/> COMPLETE seconds (the current,
    /// partial second is excluded so the value doesn't dip at the start of every second).
    /// </summary>
    public double BytesPerSecond(int windowSeconds = 5)
    {
        windowSeconds = Math.Clamp(windowSeconds, 1, RingSeconds - 1);
        return (double)SumCompleteSeconds(windowSeconds) / windowSeconds;
    }

    /// <summary>Rate (5 s), last minute and total in one read.</summary>
    public TransferDirectionStats Snapshot() => new()
    {
        RateBps = BytesPerSecond(),
        LastMinuteBytes = BytesInLast(60),
        TotalBytes = TotalBytes
    };

    /// <summary>Bytes moved during the last <paramref name="seconds"/> complete seconds (max 60).</summary>
    public long BytesInLast(int seconds = 60) => SumCompleteSeconds(Math.Clamp(seconds, 1, 60));

    private long SumCompleteSeconds(int windowSeconds)
    {
        long now = NowSecond;
        long total = 0;
        lock (_lock)
        {
            for (int i = 0; i < RingSeconds; i++)
            {
                long age = now - _second[i];
                if (age >= 1 && age <= windowSeconds) total += _bytes[i];
            }
        }
        return total;
    }
}

/// <summary>
/// Node-wide token bucket for upload bandwidth. Rate is read on every call, so a settings change
/// applies immediately; 0 (or less) means unlimited. Burst capacity is one second's worth.
/// </summary>
public sealed class UploadRateLimiter
{
    private readonly object _lock = new();
    private double _tokens;
    private long _lastRefillTicks = Environment.TickCount64;

    public async Task WaitAsync(int bytes, int bytesPerSecond, CancellationToken ct)
    {
        while (true)
        {
            if (bytesPerSecond <= 0) return;

            TimeSpan delay;
            lock (_lock)
            {
                long now = Environment.TickCount64;
                double elapsedSec = (now - _lastRefillTicks) / 1000.0;
                _lastRefillTicks = now;
                _tokens = Math.Min(bytesPerSecond, _tokens + elapsedSec * bytesPerSecond);

                // Spend when there is any credit; tokens may go negative (a slice can be bigger
                // than one tick's refill), and the debt is paid back by waiting next time.
                if (_tokens > 0)
                {
                    _tokens -= bytes;
                    return;
                }

                delay = TimeSpan.FromSeconds(Math.Min(1.0, (-_tokens + 1) / bytesPerSecond));
            }

            await Task.Delay(delay < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : delay, ct);
        }
    }
}
