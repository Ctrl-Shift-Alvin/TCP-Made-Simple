using System;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;

namespace AlvinSoft;
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

    readonly static bool canUsePopCnt;
    readonly static bool canUsePopCntX64;

    private static readonly byte[] byteTable = new byte[256];

    static BitCounter() {

        canUsePopCnt = Popcnt.IsSupported;
        canUsePopCntX64 = Popcnt.X64.IsSupported;

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
    public static ulong CountOneBits(byte[] bytes) => canUsePopCnt ? PopCntCount(bytes) : LookupCount(bytes);

    /// <summary>Uses PopCnt operation to count the 1 bits in the <paramref name="bytes"/> array and uses the x64 instruction if possible. Otherwise calls <see cref="LookupCount(byte[])"/>.</summary>
    /// <remarks>Contains unsafe code.</remarks>
    public static ulong PopCntCount(byte[] bytes) {

        int length = bytes.Length;
        ulong bitCount = 0;
        int i = 0;

        unsafe {
            fixed (byte* pBytes = bytes) {

                byte* pData = pBytes;

                //take advantage of x64
                if (canUsePopCntX64) {

                    //it's faster to cast the bytes to ulongs to reduce instruction call count
                    for (; i <= length - sizeof(ulong); i += sizeof(ulong)) {
                        ulong value = *(ulong*)pData;
                        bitCount += Popcnt.X64.PopCount(value);
                        pData += sizeof(ulong);
                    }

                    //cast remaining bytes to uints
                    for (; i <= length - sizeof(uint); i += sizeof(uint)) {
                        uint value = *(uint*)pData;
                        bitCount += Popcnt.PopCount(value);
                        pData += sizeof(ulong);
                    }

                    //process remaining bytes
                    for (; i < length; i++) {
                        bitCount += Popcnt.PopCount(*pData);
                        pData++;
                    }

                } else if (canUsePopCnt) {

                    //cast to uints
                    for (; i <= length - sizeof(uint); i += sizeof(uint)) {
                        uint value = *(uint*)pData;
                        bitCount += Popcnt.PopCount(value);
                        pData += sizeof(ulong);
                    }

                    //process remaining bytes
                    for (; i < length; i++) {
                        bitCount += Popcnt.PopCount(*pData);
                        pData++;
                    }

                } else {
                    return LookupCount(bytes);
                }

            }//fixed
        }//unsafe

        return bitCount;
    }

    /// <summary>Uses a precomputed lookup table to count the one bits in the <paramref name="bytes"/> array</summary>
    public static ulong LookupCount(byte[] bytes) {

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

        ArgumentNullException.ThrowIfNull(bits);

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

internal static class HashTools {

    /// <summary>Uses SHA256 to compute a hash and returns the first 4 and last 4 bytes of the hash, both from left to right.</summary>
    /// <param name="data">The bytes used to calculate the hash</param>
    /// <returns>The truncated 8-byte long hash</returns>
    public static long Compute8ByteHash(byte[] data) {

        byte[] hash = SHA256.HashData(data);
        byte[] truncHash = new byte[8];

        Array.Copy(hash, truncHash, 4);
        Array.Copy(hash, 28, truncHash, 4, 4);

        return BitConverter.ToInt64(truncHash);

    }

}