using System;
using System.IO;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Threading;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
public class ServerSettings() {

    #region Public_Fields
    /// <summary>The current server version</summary>
    public const int CurrentVersion = 0;

    /// <summary>-1 for empty settings</summary>
    public int Version = CurrentVersion;

    /// <summary>The amount of test iterations when testing a client's connection.</summary>
    public byte ConnectionTestTries = 3;

    /// <summary>Encrypt the data in the packages.</summary>
    public bool EncryptionEnabled = true;

    #endregion

    /// <summary>The password used for encryption.</summary>
    /// <remarks>Irrelevant if <see cref="EncryptionEnabled"/> is false</remarks>
    public string Password;

    /// <summary>The maximum amount of clients allowed at the same time.</summary>
    /// <remarks>This field is not disclosed to the clients</remarks>
    public int MaxClients = 15;

    /// <summary>The number of times a client can declare panic before deemed unstable and disconnected.</summary>
    public int MaxPanicsPerClient = 5;
        
    /// <summary>The interval at which to ping clients.</summary>
    /// <remarks>Set to 0 to disable pinging</remarks>
    public int PingIntervalMs = 10000;

    /// <summary>The maximum time to wait for a pong before disconnecting a client.</summary>
    /// <remarks>Must be lower than <see cref="PingIntervalMs"/>. Irrelevant if <see cref="PingIntervalMs"/> is 0 or less.</remarks>
    public int PingTimeoutMs = 8000;

    /// <summary>The maximum time to wait before cancelling an async read operation after receiving the first byte.</summary>
    /// <remarks>The provided cancellation token is used only for the first byte. After that a new one is created.</remarks>
    public int ReceiveTimeoutMs = 2000;

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
    public static ServerSettings None => new() {
        Version = -1
    };


}

/// <summary>Represents a client that is connected to a server</summary>
internal class Client(byte[] id, ServerSettings settings, TcpClient client) {
    /// <summary>The client's unique ID</summary>
    internal byte[] ID { get; } = id;

    /// <summary>The tcp client that is used for communication.</summary>
    internal TcpClient Tcp = client;

    /// <summary>The stream that is used to send data.</summary>
    internal NetworkStream Stream = client.GetStream();

    /// <summary>Checks if the underlying TcpClient is still connected</summary>
    /// <returns>true if the client is still connected; otherwise false</returns>
    public bool IsConnected => Tcp.Connected;

    internal int PanicCount { get; private set; } = 0;


    internal bool pingStatus;
    internal object pingSync = new();
    internal bool PingStatus {
        get {
            lock (pingSync)
                return pingStatus;
        }
        set {
            lock (pingSync)
                pingStatus = value;
        }
    }

    internal CancellationTokenSource CancelTokenSource { get; } = new CancellationTokenSource();
    internal CancellationToken TimeoutToken => new CancellationTokenSource(settings.ReceiveTimeoutMs).Token;

    internal void SendPackage(Package package) => Stream.Write(package.GetBytes());
    internal async Task SendPackageAsync(Package package) => await Stream.WriteAsync(package.GetBytes());

    internal Package ReceivePackage(Package.PackageTypes expectedType = Package.PackageTypes.None) {

        Package.PackageTypes packageType = (Package.PackageTypes)Stream.ReadByte();

        if (packageType != Package.PackageTypes.None && packageType != expectedType) {
            TcpMsProtocolException.ThrowUnexpectedPackage(expectedType, packageType);
            DiscardPackageRest();
        }

        Package.DataTypes dataType = (Package.DataTypes)Stream.ReadByte();

        byte[] dataLengthBuffer = new byte[4];
        Stream.Read(dataLengthBuffer);
        int dataLength = BinaryPrimitives.ReadInt32BigEndian(dataLengthBuffer);

        byte[] dataBuffer = new byte[dataLength];
        Stream.Read(dataBuffer);

        return new(packageType, dataType, dataBuffer, false);

    }
    internal async Task<Package> ReceivePackageAsync(Package.PackageTypes expectedType = Package.PackageTypes.None, CancellationToken cancellationToken = default) {

        byte[] packageTypeBuffer = new byte[1];
        await Stream.ReadAsync(packageTypeBuffer, cancellationToken);
        Package.PackageTypes packageType = (Package.PackageTypes)packageTypeBuffer[0];

        if (packageType != Package.PackageTypes.None && packageType != expectedType) {
            TcpMsProtocolException.ThrowUnexpectedPackage(expectedType, packageType);
            DiscardPackageRest();
        }

        byte[] dataTypeBuffer = new byte[1];
        await Stream.ReadAsync(dataTypeBuffer, TimeoutToken);
        Package.DataTypes dataType = (Package.DataTypes)dataTypeBuffer[0];

        byte[] dataLengthBuffer = new byte[4];
        await Stream.ReadAsync(dataLengthBuffer, TimeoutToken);
        int dataLength = BinaryPrimitives.ReadInt32BigEndian(dataLengthBuffer);

        byte[] dataBuffer = new byte[dataLength];
        await Stream.ReadAsync(dataBuffer, TimeoutToken);

        return new(packageType, dataType, dataBuffer, false);

    }

    private void DiscardPackageRest() {
        _ = Stream.ReadByte(); //data type
        byte[] dataLengthBuffer = new byte[4];
        Stream.Read(dataLengthBuffer); //data length

        int dataLength = BinaryPrimitives.ReadInt32BigEndian(dataLengthBuffer);

        Stream.Read(new byte[dataLength], 0, dataLength); //data
    }

    /// <summary>Closes this connection and disposes of it</summary>
    internal void Close() {
        CancelTokenSource.Cancel();
        Stream = null;
        Tcp.Close();
        Tcp.Dispose();
    }

    /// <summary>Increment <see cref="PanicCount"/></summary>
    internal void CallPanic() => PanicCount++;
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
        Bool,
        String,
        Byte,
        Short,
        Int,
        Long,
        Blob
    }

    public enum PackageTypes : byte {
        None,
        Error,
        Disconnect,
        SubpackageHeader,
        Subpackage,
        Data,
        NewSettings,
        AuthRSAPublicKey,
        AuthEncryptedPassword,
        AuthSuccess,
        EncrRSAPublicKey,
        EncrIV,
        EncrSalt,
        Test,
        Ping,
        Pong,
        Panic

    }
#pragma warning restore CS1591

    /// <summary>The type of <c>Data</c></summary>
    public DataTypes DataType { get; }

    /// <summary>The purpose of the package</summary>
    public PackageTypes PackageType { get; }

    /// <summary>The data without the header</summary>
    public byte[]? Data { get; } = null;

    /// <summary>Shorthand for <c>Data.Length</c></summary>
    public int DataLength => Data == null ? 0 : Data.Length;

    #endregion
    #region Operators
    /// <returns>true if the left package contents are the same as the right's; otherwise false.</returns>
    public static bool operator ==(Package left, Package right) => left.Equals(right);
    /// <returns>false if the left package contents are the same as the right's; otherwise true.</returns>
    public static bool operator !=(Package left, Package right) => !left.Equals(right);
    #endregion

    #region Constructors
    /// <summary>
    /// Create an empty package instance.
    /// </summary>
    /// <remarks><see cref="Data"/> will be null.</remarks>
    public Package(PackageTypes type) {
        PackageType = type;
        DataType = DataTypes.Empty;
        Data = null;
    }

    /// <summary>
    /// Create an instance and copy <paramref name="data"/> to it.
    /// </summary>
    public Package(PackageTypes packageType, DataTypes dataType, byte[]? data, bool copyData = true) {

        PackageType = packageType;
        DataType = dataType;

        if (data == null || data.Length == 0) {
            Data = null;
        } else {
            Data = new byte[data.Length];
            if (copyData)
                Array.Copy(data, Data, data.Length);
            else
                Data = data;
        }
    }

    /// <summary>
    /// Create an instance and copy the unicode bytes of <paramref name="data"/> to it.
    /// </summary>
    public Package(PackageTypes packageType, string data) : this(packageType, DataTypes.String, Encoding.Unicode.GetBytes(data)) { }
    /// <summary>
    /// Create an instance and copy 1 or 0 to it based on <paramref name="data"/>
    /// </summary>
    /// <remarks><see cref="Data"/> will be of length 1.</remarks>
    public Package(PackageTypes packageType, bool data) {

        PackageType = packageType;
        DataType = DataTypes.Int;

        Data = BitConverter.GetBytes(data);

    }

    /// <summary>
    /// Create an instance and copy <paramref name="data"/> to it.
    /// </summary>
    /// <remarks><see cref="Data"/> will be of length 1.</remarks>
    public Package(PackageTypes packageType, byte data) {

        PackageType = packageType;
        DataType = DataTypes.Byte;

        Data = [data];

    }

    /// <summary>
    /// Create an instance and copy the big endian representiation of <paramref name="data"/> to it.
    /// </summary>
    /// <remarks><see cref="Data"/> will be of length 2.</remarks>
    public Package(PackageTypes packageType, short data) {

        PackageType = packageType;
        DataType = DataTypes.Short;

        Data = new byte[2];
        BinaryPrimitives.WriteInt16BigEndian(Data, data);

    }

    /// <summary>
    /// Create an instance and copy the big endian representiation of <paramref name="data"/> to it.
    /// </summary>
    /// <remarks><see cref="Data"/> will be of length 4.</remarks>
    public Package(PackageTypes packageType, int data) {

        PackageType = packageType;
        DataType = DataTypes.Int;

        Data = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(Data, data);

    }

    /// <summary>
    /// Create an instance and copy the big endian representiation of <paramref name="data"/> to it.
    /// </summary>
    /// <remarks><see cref="Data"/> will be of length 8.</remarks>
    public Package(PackageTypes packageType, long data) {

        PackageType = packageType;
        DataType = DataTypes.Long;

        Data = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(Data, data);

    }

    #endregion

    /// <summary>
    /// Create a byte array that represents this package.
    /// </summary>
    public byte[] GetBytes() {

        byte[] bytes = new byte[1 + 1 + 4 + DataLength];

        bytes[0] = (byte)PackageType;
        bytes[1] = (byte)DataType;

        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(2), DataLength);

        if (Data != null) //same as DataLength > 0, but this avoids compiler warning
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


#pragma warning disable CS9113
/// <summary>
/// Thrown when a TcpMs instance breaks protocol.
/// </summary>
/// <param name="message">The message to display</param>
[Serializable]
public class TcpMsProtocolException(string message) : Exception {
    internal static void ThrowUnexpectedPackage(Package.PackageTypes expectedPackage, Package.PackageTypes receivedPackage) {
        string errorMessage = $"Received package of type {receivedPackage}, but expected {expectedPackage}";
        throw new TcpMsProtocolException(errorMessage);
    }
}
#pragma warning restore CS9113
