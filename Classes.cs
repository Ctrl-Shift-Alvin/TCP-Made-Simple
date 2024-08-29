using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using AlvinSoft.Cryptography;
using System.Text;

namespace AlvinSoft.TcpMs;

/// <summary>A struct that represents the settings of a server</summary>
/// <remarks>Default values:
/// <code>
    /// Version = CurrentVersion,
    /// EncryptionEnabled = true,
    /// ConnectionTestTries = 3,
    /// MaxClients = 15,
    /// MaxPanicsPerClient = 5,
    /// PingIntervalMs = 10000,
    /// PingTimeoutMs = 8000
/// </code>
/// These values are exported when calling <see cref="GetBytes"/>, the rest aren't:
/// <code>
    /// Version, ConnectionTestTries, EncryptionEnabled
/// </code>
/// </remarks>
public class ServerSettings(string password) {

    #region Public_Fields
    /// <summary>The current server version</summary>
    public const int CurrentVersion = 0;

    /// <summary>-1 for empty settings</summary>
    public int Version { get; internal set; } = CurrentVersion;

    /// <summary>The amount of test iterations when testing a client's connection.</summary>
    public byte ConnectionTestTries { get; set; } = 3;

    /// <summary>Encrypt the data in the packages.</summary>
    public bool EncryptionEnabled { get; set; } = true;

    #endregion

    /// <summary>The password used for encryption.</summary>
    /// <remarks>Irrelevant if <see cref="EncryptionEnabled"/> is false.</remarks>
    public SecurePassword Password { get; set; } = new(password ?? string.Empty);

    /// <summary>The maximum amount of clients allowed at the same time.</summary>
    /// <remarks>This field is not disclosed to the clients</remarks>
    public int MaxClients { get; set; } = 15;

    /// <summary>The number of times a client can declare panic before deemed unstable and disconnected.</summary>
    public int MaxPanicsPerClient { get; set; } = 5;
        
    /// <summary>The interval at which to ping clients.</summary>
    /// <remarks>Set to 0 to disable pinging</remarks>
    public int PingIntervalMs { get; set; } = 10000;

    /// <summary>The maximum time to wait for a pong before disconnecting a client.</summary>
    /// <remarks>Must be lower than <see cref="PingIntervalMs"/>. Irrelevant if <see cref="PingIntervalMs"/> is 0 or less.</remarks>
    public int PingTimeoutMs { get; set; } = 8000;

    /// <summary>The maximum time to wait before cancelling an async read operation after receiving the first byte.</summary>
    /// <remarks>The provided cancellation token is used only for the first byte. After that a new one is created.</remarks>
    public int ReceiveTimeoutMs { get; set; } = 500;

    /// <summary>Import an instance that was exported using <see cref="GetBytes()"/>.</summary>
    public void Update(byte[] data) {

        //4Version, 1ConnectionTestTries, 1EncryptionEnabled

        int version = BinaryPrimitives.ReadInt32BigEndian(data);

        if (version != CurrentVersion)
            throw new ServerVersionException(version);

        Version = version;
        ConnectionTestTries = data[4];
        EncryptionEnabled = data[5] == 1;

    }

    /// <summary>Export this instance as bytes. Use <see cref="GetBytes"/> to import.</summary>
    public byte[] GetBytes() {

        var stream = new MemoryStream(4 + 1 + 1);

        byte[] versionBuffer = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(versionBuffer, Version);

        byte encryptionBuffer = EncryptionEnabled ? (byte)1 : (byte)0;
            
        //4Version, 1ConnectionTestTries, 1EncryptionEnabled
        stream.Write(versionBuffer);
        stream.WriteByte(ConnectionTestTries);
        stream.WriteByte(encryptionBuffer);

        return stream.ToArray();

    }


    [Serializable]
    #pragma warning disable CS1591
    public class ServerVersionException(int serverVersion) : Exception($"The server you have connected to runs on version {serverVersion}, while your client is version {CurrentVersion}") {}
    #pragma warning restore CS1591


    /// <summary>Represents settings that are unknown and/or not yet retrieved</summary>
    /// <value><code>
    /// public static ServerSettings None => new() {
    ///     Version = -1
    /// };
    /// </code></value>
    public static ServerSettings None => new(null) {
        Version = -1
    };


}

/// <summary>
/// Represents a package that was/will be sent over a network.
/// </summary>
#nullable enable
public readonly struct Package {

    #region Fields
#pragma warning disable CS1591
    /// <summary>The type of data stored in <see cref="Data"/></summary>
    public enum DataTypes : byte {
        Empty,
        String,
        Byte,
        Blob
    }

    public enum PackageTypes : byte {
        None,
        Error,
        DisconnectRequest,
        Disconnect,
        Data,

        Auth_Info,
        Auth_Request,
        Auth_Salt,
        Auth_IV,
        Auth_Challenge,
        Auth_Response,
        Auth_Success,
        Auth_Failure,

        EncrRequest,
        EncrIV,
        EncrSalt,

        TestRequest,
        Test,
        TestTrySuccess,
        TestTryFailure,

        Ping,
        Pong,
        Panic

    }
#pragma warning restore CS1591

    /// <summary>The type of <c>Data</c></summary>
    public readonly DataTypes DataType { get; } = DataTypes.Empty;

    /// <summary>The purpose of the package</summary>
    public readonly PackageTypes PackageType { get; } = PackageTypes.None;

    /// <summary>The data without the header</summary>
    public readonly byte[]? Data { get; } = null;

    /// <summary>Shorthand for <c>Data.Length</c></summary>
    public readonly int DataLength { get; } = 0;

    internal readonly bool IsInternalPackage => PackageType != PackageTypes.Data;

    /// <summary>
    /// Returns true if <see cref="PackageType"/> == None.
    /// </summary>
    public readonly bool IsEmpty => PackageType == PackageTypes.None;

    internal readonly TaskCompletionSource? TaskCompletion;
    /// <summary>
    /// Waits for this package to be sent.
    /// </summary>
    /// <returns>A task that finishes when the package was sent</returns>
    /// <exception cref="InvalidOperationException">This instance does not have a task assigned.</exception>
    public async Task Await() {

        if (TaskCompletion == null)
            throw new InvalidOperationException("This instance did not assign a task.");

        await TaskCompletion.Task;

    }

    /// <summary>
    /// Notifies that this package was sent.
    /// </summary>
    public void TryNotifySent() => TaskCompletion?.SetResult();

    #endregion
    #region Operators
    /// <returns>true if the left package contents are the same as the right's; otherwise false.</returns>
    public static bool operator ==(Package left, Package right) => left.Equals(right);
    /// <returns>false if the left package contents are the same as the right's; otherwise true.</returns>
    public static bool operator !=(Package left, Package right) => !left.Equals(right);
    #endregion
    #region Ctor

    /// <summary>
    /// Create an empty package instance.
    /// </summary>
    /// <remarks><see cref="Data"/> will be null.</remarks>
    internal Package(PackageTypes type, bool useTask = false) {
        PackageType = type;
        DataType = DataTypes.Empty;
        Data = null;
        DataLength = 0;

        if (useTask)
            TaskCompletion = new();

    }

    /// <summary>
    /// Create an instance and copy <paramref name="data"/> to it.
    /// </summary>
    internal Package(PackageTypes packageType, DataTypes dataType, byte[]? data, bool copyData = true, bool useTask = false) {

        PackageType = packageType;
        DataType = dataType;

        if (data == null || data.Length < 1) {
            Data = null;
            DataLength = 0;
        } else {
            Data = new byte[data.Length];
            if (copyData)
                Array.Copy(data, Data, data.Length);
            else
                Data = data;
            DataLength = data.Length;
        }

        if (useTask)
            TaskCompletion = new();
    }

    #endregion

    /// <summary>
    /// Returns an empty package of type <see cref="PackageTypes.None"/>. Shorthand for <c>new Package()</c>.
    /// </summary>
    public static Package Empty => new();
    /// <summary>
    /// Returns an error package. Shorthand for <c>new Package(PackageTypes.Error)</c>.
    /// </summary>
    public static Package Error => new(PackageTypes.Error);

    /// <summary>
    /// Create a byte array that represents this package.
    /// </summary>
    public byte[] GetBytes() {

        byte[] bytes = new byte[1 + 1 + 4 + DataLength];

        bytes[0] = (byte)PackageType;
        bytes[1] = (byte)DataType;

        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(2), DataLength);

        if (Data != null) //same as DataLength > 0 (DataLength == 0 means Data = null), but this avoids compiler warning
            Array.Copy(Data, 0, bytes, 6, Data.Length);

        return bytes;

    }

    #region Overrides
    /// <summary>Check if the fields in this instance are equal to the fields in <paramref name="obj"/></summary>
    /// <returns>true if <paramref name="obj"/> is of type Package and DataType, PackageType and Data are the same; otherwise false.</returns>
    public override bool Equals(object? obj) {

        if (obj == null)
            return false;

        if (obj is Package o) {

            if (DataType != o.DataType)
                return false;

            if (PackageType != o.PackageType)
                return false;

            if (Data == null)
                return o.Data == null;
            if (o.Data == null)
                return false;

            return Enumerable.SequenceEqual(Data, o.Data);
        }

        return false;

    }
    /// <summary>Gets the hash code for this instance</summary>
    public override int GetHashCode() => base.GetHashCode();
    #endregion

}
#nullable restore

internal abstract class PackageHandler(NetworkStream targetStream) {

    private NetworkStream Stream { get; set; } = targetStream;

    protected virtual CancellationToken TimeoutToken => new CancellationTokenSource(500).Token;


    private ConcurrentQueue<Package> QueueOut;

    private Task ObtainHandlerTask;
    private CancellationTokenSource ObtainHandlerCancel;
    private ManualResetEventSlim ObtainHandlerPause;
    private SemaphoreSlimUsing ObtainHandlerSync;

    private Task DispatchHandlerTask;
    private CancellationTokenSource DispatchHandlerCancel;
    private ManualResetEventSlim DispatchHandlerPause;
    private SemaphoreSlimUsing DispatchHandlerSync;


    public enum Errors : byte {
        None,
        ReadTimeout,
        CannotWrite,
        CannotRead,
        Disconnected,
        ErrorPackage,
        UnexpectedPackage,
        PingTimeout,
        IncorrectPackage
    }

    public enum OperationResult : byte {
        /// <summary>Everything succeeded.</summary>
        Succeeded,
        /// <summary>No error occured, but the operation was not successful.</summary>
        Failed,
        /// <summary>An error occured.</summary>
        Error,
        /// <summary>The stream is not connected.</summary>
        Disconnected
    }

    #region Abstract/Virtual

    /// <summary>
    /// Override to handle internal packages. This is awaited in the obtain thread.
    /// </summary>
    protected abstract Task OnReceivedInternalPackage(Package package);
    /// <summary>
    /// Override to handle data packages. This is called in the obtain thread.
    /// </summary>
    protected abstract void OnReceivedDataPackage(Package package);

    /// <summary>
    /// Override to handle errors. Return true if base should resume obtaining/dispatching threads; otherwise false.
    /// </summary>
    protected abstract Task OnError(Errors error);

    /// <summary>
    /// Cancels all threads and closes the underlying stream.
    /// </summary>
    public virtual void Close() {

        _ = StopAllAsync();
        Stream?.Close();
        Stream?.Dispose();

    }

    #endregion

    #region ObtainHandler
    private async Task ObtainHandler() {

        ObtainHandlerCancel = new();
        ObtainHandlerPause = new(true);
        ObtainHandlerSync = new();

        while (!ObtainHandlerCancel.IsCancellationRequested) {

            try {
                ObtainHandlerPause.Wait(ObtainHandlerCancel.Token);
            } catch (OperationCanceledException) {
                return;
            }

            //Note: There is a possible edge case where PauseReceive() is called between the pause check and entering the sync.
            //In this state, this thread is not paused and also not in the sync so PauseReceive() enters the sync and assumes that this thread has paused.
            //To avoid this, don't await the sync (which would mean awaiting PauseReceive() and receiving in this thread), but try to enter ONCE.
            //If PauseReceive() is in the sync, this line returns false, and the pause state is rechecked. PauseReceive() can then continue normally.
            if (!ObtainHandlerSync.Wait(0))
                continue;

            Package incoming;
            try {

                //The using block here is crucial so that even when an exception is thrown, the semaphore is left before continuing. OnError WILL call "await PauseAllAsync"
                //which also awaits entering this semaphore.
                using (ObtainHandlerSync) {
                    incoming = await ObtainPackageAsync(ObtainHandlerCancel.Token);
                }

            } catch (OperationCanceledException) {

                break;

            } catch (InvalidOperationException) {

                await OnError(Errors.CannotRead);
                continue;

            } catch (TcpMsUnexpectedPackageException) {

                await OnError(Errors.ErrorPackage);
                continue;

            } catch (TcpMsTimeoutException) {

                await OnError(Errors.ErrorPackage);
                continue;

            }

            if (incoming.IsInternalPackage)
                await OnReceivedInternalPackage(incoming);
            else
                OnReceivedDataPackage(incoming); 


        }

        ObtainHandlerCancel.Dispose();
        ObtainHandlerPause.Dispose();
        ObtainHandlerSync.ActualDispose();

    }

    private bool IsObtainNull() => ObtainHandlerCancel == null || ObtainHandlerCancel.IsCancellationRequested;

    /// <summary>
    /// Starts the receiving thread and receiving packages.
    /// </summary>
    public void StartObtain() {

        if (!IsDispatchNull())
            return;

        ObtainHandlerTask = Task.Run(ObtainHandler);
    }

    /// <summary>
    /// Wait for the current package to finish sending and pause the receiving thread.
    /// </summary>
    public async Task PauseObtainAsync() {

        if (IsObtainNull())
            return;

        if (!ObtainHandlerPause.IsSet)
            return;

        //tell the receiving thread to pause
        ObtainHandlerPause.Reset();

        //wait for the thread to finish sending the package. Also see the note in ObtainHandler().
        await ObtainHandlerSync.WaitAsync();
        ObtainHandlerSync.Release();

    }

    public void ResumeObtain() {

        if (IsDispatchNull())
            return;

        ObtainHandlerPause.Set();
    }

    /// <summary>
    /// Stops all receiving threads and awaits them.
    /// </summary>
    /// <returns>A task that finishes when all threads have finished.</returns>
    public async Task StopObtainAsync() {

        if (IsObtainNull())
            return;

        //cancel the receive handler
        await ObtainHandlerCancel.CancelAsync();

        //wait for the receive handler to finish, then wait for the queues to clear
        await ObtainHandlerTask;

    }

    private async Task FlushObtainAsync() {
        try {
            byte[] buffer = new byte[16];
            while (await Stream.ReadAsync(buffer.AsMemory()) > 0);
        } finally { }
    }
    #endregion

    #region DispatchHandler

    private async Task DispatchHandler() {

        QueueOut = new();
        DispatchHandlerCancel = new();
        DispatchHandlerPause = new(true);
        DispatchHandlerSync = new();

        while (!DispatchHandlerCancel.IsCancellationRequested) {

            try {
                DispatchHandlerPause.Wait(DispatchHandlerCancel.Token);
            } catch (OperationCanceledException) {
                return;
            }

            //same edge case as described in ObtainHandler()
            if (!DispatchHandlerSync.Wait(0))
                continue;

            if (QueueOut.TryDequeue(out Package outgoing)) {

                try {
                    //same situation as in ObtainHandler()
                    using (DispatchHandlerSync) {
                        await DispatchPackageAsync(outgoing);
                    }

                } catch (InvalidOperationException) {

                    await OnError(Errors.CannotWrite);
                    continue;

                }
            }


        }

        //No QueueOut.Clear() since we might want to manually handle it
        DispatchHandlerCancel.Dispose();
        DispatchHandlerPause.Dispose();
        DispatchHandlerSync.ActualDispose();

    }

    private bool IsDispatchNull() => DispatchHandlerCancel == null || DispatchHandlerCancel.IsCancellationRequested;

    /// <summary>
    /// Puts a package in the dispatching queue.
    /// </summary>
    /// <exception cref="ObjectDisposedException"/>
    public void Send(Package package) {
        QueueOut.Enqueue(package);
    }

    /// <summary>
    /// Starts dispatching packages in the queue.
    /// </summary>
    public void StartDispatch() {

        if (!IsDispatchNull())
            return;

        DispatchHandlerTask = Task.Run(DispatchHandler);

    }

    /// <summary>
    /// Waits for the package to finish dispatching and pauses the dispatching thread.
    /// </summary>
    public async Task PauseDispatchAsync() {

        if (IsDispatchNull())
            return;

        if (!DispatchHandlerPause.IsSet)
            return;

        DispatchHandlerPause.Reset();

        await DispatchHandlerSync.WaitAsync();
        DispatchHandlerSync.Release();
    }

    /// <summary>
    /// Resumes the dispatching thread.
    /// </summary>
    public void ResumeDispatch() {

        if (IsDispatchNull())
            return;

        DispatchHandlerPause.Set();
    }

    /// <summary>
    /// Stops the dispatching thread and awaits it.
    /// </summary>
    /// <returns>A task that finishes when all remaining packages were sent.</returns>
    public async Task StopDispatchAsync() {

        if (IsDispatchNull())
            return;

        await DispatchHandlerCancel.CancelAsync();
        await DispatchHandlerTask;

    }

    /// <summary>
    /// Stops the dispatching thread and manually dispatches all remaining packages.
    /// </summary>
    /// <remarks>If the stream is closed at any point, the queue is cleared and the task finishes.</remarks>
    /// <returns>A task that finishes when the dispatching thread has stopped and all remaining packages were dispatched.</returns>
    public async Task StopAndDispatchRest() {

        await StopDispatchAsync();

        while (!QueueOut.IsEmpty) {

            if (QueueOut.TryDequeue(out Package outgoing)) {

                try {

                    await DispatchPackageAsync(outgoing);

                } catch (InvalidOperationException) {

                    QueueOut.Clear();
                    return;

                }
            }

        }

    }

    #endregion

    /// <summary>
    /// Starts the obtain and dispatch threads.
    /// </summary>
    public virtual void StartAll() {
        StartDispatch();
        StartObtain();
    }

    /// <summary>
    /// Pause the obtain and dispatch threads.
    /// </summary>
    public virtual async Task PauseAllAsync() => await Task.WhenAll(PauseDispatchAsync(), PauseObtainAsync());

    /// <summary>
    /// Resume the obtain and dispatch threads.
    /// </summary>
    public virtual void ResumeAll() {
        ResumeDispatch();
        ResumeObtain();
    }

    /// <summary>
    /// Stops the obtain and dispatch threads and awaits their completion.
    /// </summary>
    public virtual async Task StopAllAsync() {
        await Task.WhenAll(StopDispatchAsync(), StopObtainAsync());
    }


    #region Dispatch/Obtain
    /// <summary>
    /// Dispatches a package.
    /// </summary>
    /// <returns>A task that finishes when the package was dispatched.</returns>
    /// <exception cref="InvalidOperationException"/>
    public async Task DispatchPackageAsync(Package package) {

        try {
            await Stream.WriteAsync(package.GetBytes());
        } catch {
            throw new InvalidOperationException();
        }

    }

    /// <summary>Obtains any package.</summary>
    /// <exception cref="InvalidOperationException">Could not read bytes.</exception>
    /// <exception cref="OperationCanceledException">The first byte timed out.</exception>
    /// <exception cref="TcpMsTimeoutException">A byte after the first one timed out.</exception>
    /// <exception cref="TcpMsErrorPackageException">Received an error package.</exception>
   public async Task<Package> ObtainPackageAsync(CancellationToken cancellationToken = default) {


        try {

            byte[] packageTypeBuffer = new byte[1];
            try {
                await Stream.ReadAsync(packageTypeBuffer, cancellationToken == default ? TimeoutToken : cancellationToken);
            } catch (OperationCanceledException) {
                throw;
            }
            Package.PackageTypes packageType = (Package.PackageTypes)packageTypeBuffer[0];

            TcpMsErrorPackageException.ThrowIfError(packageType);

            byte[] dataTypeBuffer = new byte[1];
            await Stream.ReadAsync(dataTypeBuffer, TimeoutToken); //don't let cancellationToken interrupt after the first byte
            Package.DataTypes dataType = (Package.DataTypes)dataTypeBuffer[0];

            byte[] dataLengthBuffer = new byte[4];
            await Stream.ReadAsync(dataLengthBuffer, TimeoutToken);
            int dataLength = BinaryPrimitives.ReadInt32BigEndian(dataLengthBuffer);

            byte[] dataBuffer;
            if (dataLength > 0) {
                dataBuffer = new byte[dataLength];
                await Stream.ReadAsync(dataBuffer, TimeoutToken);
            } else {
                dataBuffer = null;
            }

            return new(packageType, dataType, dataBuffer, false);

        } catch (OperationCanceledException) {
            throw new TcpMsTimeoutException();
        } catch {
            throw new InvalidOperationException("Could not read from stream.");
        }
    }

    /// <exception cref="InvalidOperationException">Could not read bytes.</exception>
    /// <exception cref="TcpMsTimeoutException">A byte timed out.</exception>
    /// <exception cref="TcpMsErrorPackageException">Received an error package.</exception>
    /// <exception cref="TcpMsUnexpectedPackageException">Received an unexpected package.</exception>
    public async Task<Package> ObtainExpectedPackageAsync(Package.PackageTypes expectedType, CancellationToken cancellationToken = default) {

        try {

            byte[] packageTypeBuffer = new byte[1];
            try {
                await Stream.ReadAsync(packageTypeBuffer, cancellationToken == default ? TimeoutToken : cancellationToken);
            } catch (OperationCanceledException) {
                throw new TcpMsTimeoutException();
            }
            Package.PackageTypes packageType = (Package.PackageTypes)packageTypeBuffer[0];

            TcpMsErrorPackageException.ThrowIfError(packageType);
            TcpMsUnexpectedPackageException.ThrowIfUnexpected(expectedType, packageType);

            byte[] dataTypeBuffer = new byte[1];
            await Stream.ReadAsync(dataTypeBuffer, TimeoutToken);
            Package.DataTypes dataType = (Package.DataTypes)dataTypeBuffer[0];

            byte[] dataLengthBuffer = new byte[4];
            await Stream.ReadAsync(dataLengthBuffer, TimeoutToken);
            int dataLength = BinaryPrimitives.ReadInt32BigEndian(dataLengthBuffer);

            byte[] dataBuffer;
            if (dataLength > 0) {
                dataBuffer = new byte[dataLength];
                await Stream.ReadAsync(dataBuffer, TimeoutToken);
            } else {
                dataBuffer = null;
            }

            return new(packageType, dataType, dataBuffer, false);

        } catch (OperationCanceledException) {

            throw new TcpMsTimeoutException();

        } catch {
            throw new InvalidOperationException("Could not read from stream.");
        }
    }

    /// <exception cref="InvalidOperationException">Could not read bytes.</exception>
    /// <exception cref="TcpMsTimeoutException">A byte timed out.</exception>
    /// <exception cref="TcpMsErrorPackageException">Received an error package.</exception>
    /// <exception cref="TcpMsUnexpectedPackageException">Received an unexpected package.</exception>
    public async Task<Package> ObtainExpectedPackageAsync(Package.PackageTypes[] expectedTypes, CancellationToken cancellationToken = default) {

        try {

            byte[] packageTypeBuffer = new byte[1];
            try {
                await Stream.ReadAsync(packageTypeBuffer, cancellationToken == default ? TimeoutToken : cancellationToken);
            } catch (OperationCanceledException) {
                throw new TcpMsTimeoutException();
            }
            Package.PackageTypes packageType = (Package.PackageTypes)packageTypeBuffer[0];

            TcpMsErrorPackageException.ThrowIfError(packageType);
            TcpMsUnexpectedPackageException.ThrowIfUnexpected(expectedTypes, packageType);

            byte[] dataTypeBuffer = new byte[1];
            await Stream.ReadAsync(dataTypeBuffer, TimeoutToken);
            Package.DataTypes dataType = (Package.DataTypes)dataTypeBuffer[0];

            byte[] dataLengthBuffer = new byte[4];
            await Stream.ReadAsync(dataLengthBuffer, TimeoutToken);
            int dataLength = BinaryPrimitives.ReadInt32BigEndian(dataLengthBuffer);

            byte[] dataBuffer;
            if (dataLength > 0) {
                dataBuffer = new byte[dataLength];
                await Stream.ReadAsync(dataBuffer, TimeoutToken);
            } else {
                dataBuffer = null;
            }

            return new(packageType, dataType, dataBuffer, false);

        } catch (OperationCanceledException) {

            throw new TcpMsTimeoutException();

        } catch {
            throw new InvalidOperationException("Could not read from stream.");
        }

    }

    #endregion

}


/// <summary>
/// Thrown when a TcpMs instance receives an unexpected package.
/// </summary>
[Serializable]
internal class TcpMsUnexpectedPackageException(string expected, string receivedPackage) : Exception($"Received package of type \"{receivedPackage}\", but expected \"{expected}\"") {

    /// <summary>
    /// Throws an exception if <paramref name="expected"/> != None and <paramref name="expected"/> != <paramref name="received"/>
    /// </summary>
    public static void ThrowIfUnexpected(Package.PackageTypes expected, Package.PackageTypes received) {

        if (expected != Package.PackageTypes.None && received != expected)
            throw new TcpMsUnexpectedPackageException(expected.ToString(), received.ToString());

    }

    public static void ThrowIfUnexpected(Package.PackageTypes[] expected, Package.PackageTypes received) {

        if (expected == null || expected.Length == 0)
            throw new ArgumentException("You need to expect at least one package type!");

        if (!expected.Any(k => k == received)) {

            StringBuilder expectedString = new();

            if (expected.Length == 1) {
                expectedString.Append(expected[0].ToString());
            } else {

                for (int i = 0; i < expected.Length - 1; i++) {
                    expectedString.Append(expected[i].ToString());
                    expectedString.Append(", ");
                }
                expectedString.Append("or");
                expectedString.Append(expected[^1].ToString());

            }
            
            throw new TcpMsUnexpectedPackageException(expectedString.ToString(), received.ToString());
        }

    }

}

/// <summary>
/// Thrown when a TcpMs instance receives an error package.
/// </summary>
[Serializable]
internal class TcpMsErrorPackageException() : Exception("Received an error package.") {
    /// <summary>
    /// Throws an excpetion if <paramref name="packageType"/> is <see cref="Package.PackageTypes.Error"/>.
    /// </summary>
    [DoesNotReturn]
    public static void ThrowIfError(Package.PackageTypes packageType) {
        if (packageType == Package.PackageTypes.Error)
            throw new TcpMsErrorPackageException();
    }
}

/// <summary>
/// Thrown when a TcpMs instance timed out while obtaining a package.
/// </summary>
[Serializable]
internal class TcpMsTimeoutException() : Exception("Timed out waiting for package.") { }
