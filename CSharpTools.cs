using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Diagnostics;

internal class RepeatingAction : IDisposable {

    private readonly System.Timers.Timer _internalTimer;

    private TimeSpan _recurrenceTime;
    public TimeSpan RecurrenceTime {
        get => _recurrenceTime;
        set {
            _recurrenceTime = value;
            _internalTimer.Interval = value.TotalMilliseconds;
        }
    }

    /// <summary>
    /// Can be null
    /// </summary>
    public Action Action { get; set; }

    /// <summary>
    /// Runs an action once at an interval
    /// </summary>
    /// <param name="recurrence">The interval at which the action is invoked</param>
    /// <param name="action">The action that is invoked when the interval elapses</param>
    public RepeatingAction(TimeSpan recurrence, Action action) {

        _internalTimer = new System.Timers.Timer(recurrence.TotalMilliseconds) { AutoReset = true };
        RecurrenceTime = recurrence;
        Action = action;
        _internalTimer.Elapsed += (o, a) => Task.Run(Action);
        _internalTimer.Start();
    }

    /// <summary>
    /// Runs an action once every interval, and calls <c>Dispose()</c> when the CancellationToken is cancelled
    /// </summary>
    /// <param name="recurrence">The interval at which the action is invoked</param>
    /// <param name="action">The action that is invoked when the interval elapses</param>
    /// <param name="cancellationToken">The cancellation token</param>
    public RepeatingAction(TimeSpan recurrence, Action action, CancellationToken cancellationToken) {
        _internalTimer = new System.Timers.Timer(recurrence.TotalMilliseconds) { AutoReset = true };
        RecurrenceTime = recurrence;
        cancellationToken.Register(Dispose);
        Action = action;
        _internalTimer.Elapsed += (o, a) => {
            if (cancellationToken.IsCancellationRequested)
                Dispose();
            else
                Action?.Invoke();
        };
        _internalTimer.Start();
    }


    public void Dispose() {
        _internalTimer.Stop();
        _internalTimer.Dispose();
    }
}

internal class TimedAction : IDisposable {

    private readonly System.Timers.Timer _internalTimer;
    /// <summary>
    /// Can be null
    /// </summary>
    public Action Action { get; set; }
    public bool IsRunning { get { return _internalTimer != null; } }
    private readonly TaskCompletionSource<bool> _completion;
    public TimedAction(TimeSpan interval, Action callback) {
        _internalTimer = new System.Timers.Timer() {
            Interval = interval.TotalMilliseconds,
            AutoReset = false
        };
        Action = callback;
        _internalTimer.Elapsed += (c, a) => {
            Action?.Invoke();
            _completion.SetResult(true);
            Dispose();
        };
        _completion = new TaskCompletionSource<bool>();
        _internalTimer.Start();
    }

    public TimedAction(TimeSpan interval, Action callback, CancellationToken cancellationToken) {
        _internalTimer = new System.Timers.Timer() {
            Interval = interval.TotalMilliseconds,
            AutoReset = false
        };
        Action = callback;
        _internalTimer.Elapsed += (c, a) => {

            if (cancellationToken.IsCancellationRequested) {
                Dispose();
            } else {
                Action?.Invoke();
                _completion.SetResult(true);
                Dispose();
            }

        };
        cancellationToken.Register(Dispose);
        _completion = new TaskCompletionSource<bool>();
        _internalTimer.Start();
    }

    public async Task Wait() {
        if (IsRunning)
            await _completion.Task;
        else
            return;
    }

    public void Dispose() {
        _internalTimer.Stop();
        _internalTimer.Dispose();
        Action = null;
    }
}

/// <summary>Inherits SemaphoreSlim and allows the user to put code in a using block. When <c>Dispose()</c> is called, the semaphore is freed.</summary>
/// <remarks>This implementation omits the semaphore.Release() and try-finally block, since Dispose() (equivalent to <c>Release()</c>) is always called. Use <see cref="ActualDispose"/> when you want to dispose of the instance.</remarks>
internal class SemaphoreSlimUsing : SemaphoreSlim, IDisposable {
    /// <summary>Creates a SemaphoreSlimUsing instance with <c>initialCount</c> and <c>maxCount</c> set to 1</summary>
    public SemaphoreSlimUsing() : base(1, 1) { }
    public SemaphoreSlimUsing(int initialCount) : base(initialCount) { }
    public SemaphoreSlimUsing(int initialCount, int maxCount) : base(initialCount, maxCount) { }

    /// <summary>IMPORTANT: Calls <c>base.Release()</c></summary>
    public new void Dispose() {
        Release();
    }
    /// <summary>Calls <c>base.Dispose()</c></summary>
    public void ActualDispose() {
        base.Dispose();
    }

}

/// <summary>Class used to count 1 bits in byte sequences</summary>
internal static class BitCounter {

    private static readonly byte[] byteTable = new byte[256];

    static BitCounter() {

        for (int i = 0; i < 256; i++) {

            byteTable[i] = (byte)(
                (i & 1) +
                ((i >> 1) & 1) +
                ((i >> 2) & 1) +
                ((i >> 3) & 1) +
                ((i >> 4) & 1) +
                ((i >> 5) & 1) +
                ((i >> 6) & 1) +
                ((i >> 7) & 1));
        }
    }

    /// <summary>Counts the one bit in the <paramref name="bytes"/> array, preferring the PopCnt operation. Otherwise uses a lookup table.</summary>
    /// <returns>The amount of one bits inside <paramref name="bytes"/></returns>
    public static ulong CountOneBits(byte[] bytes) {
        ulong bitCount = 0;
        foreach (byte b in bytes)
            bitCount += byteTable[b];

        return bitCount;
    }

}

internal static class BitTools {

    /// <summary>Convert a bit array to a byte representing the bits</summary>
    /// <remarks>Missing bits in the array are assumed to be 0</remarks>
    /// <returns>A byte representing the bit array</returns>
    /// <exception cref="ArgumentException">The bit array is larger than 8</exception>
    /// <exception cref="ArgumentNullException">The bit array is null</exception>
    public static byte GetByte(bool[] bits) {

        if (bits == null)
            throw new ArgumentNullException(nameof(bits), "The bit array cannot be null.");

        if (bits.Length > 8)
            throw new ArgumentException("The bit array must be 8 bits or less.", nameof(bits));

        byte value = 0;

        for (int i = 0; i < bits.Length; i++)
            if (bits[i])
                value |= (byte)(1 << (7 - i));

        return value;

    }

    /// <summary>Extract the bits in the byte</summary>
    /// <returns>A bool array of length 8 representing the bits in <paramref name="value"/></returns>
    public static bool[] GetBits(byte value) {

        bool[] bits = new bool[8];

        for (int i = 0; i < 8; i++)
            bits[i] = (value & (1 << (7 - i))) != 0;

        return bits;
    }

}

/// <summary>
/// Shared, thread-safe <see cref="Random"/> instance.
/// </summary>
internal static class Rdm {
    public static Random Shared = new Random();
    public static byte[] GetBytes(int length) {
        byte[] buffer = new byte[length];
        lock (Shared)
            Shared.NextBytes(buffer);
        return buffer;
    }
    public static int Next(int min, int max) {
        lock (Shared)
            return Shared.Next(min, max);
    }
}

#pragma warning disable
public static class Dbg {

    private static ConcurrentQueue<object?> DebugQueue = new ConcurrentQueue<object?>();
    [Conditional("DEBUG")]
    public static void Log(object? obj) {
        DebugQueue.Enqueue(obj);
    }
    [Conditional("DEBUG")]
    public static void OutputAll() {
        Debug.WriteLine($"Showing {DebugQueue.Count} debug objects: ----------------");
        foreach (var obj in DebugQueue) {
            Debug.WriteLine(obj);
        }
        DebugQueue.Clear();
        Debug.WriteLine($"Debug end ------------------------------------------------");
    }
}
#pragma warning restore