using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Net.Sockets;
using System.Buffers.Binary;
using AlvinSoft.Cryptography;

namespace AlvinSoft.TcpMs;

/// <summary>Allows simple usage of a TCP client to send/receive data to/from a <see cref="TcpMsServer"/> while implementing many useful features.</summary>
/// <remarks>Creates a new instance and assigns the address and port that is used to connect to the server. Does not start the connection process.</remarks>
/// <param name="hostname">The hostname that the client will connect to</param>
/// <param name="port">The TCP port that the client will connect to</param>
public partial class TcpMsClient(string hostname, ushort port) {

    #region Events
    #pragma warning disable CS1591

    public delegate void Connected();
    public delegate void Disconnected();
    public delegate void Panic();

    public event Connected OnConnectEvent;
    public event Disconnected OnDisconnectEvent;
    public event Panic OnPanicEvent;

    private void OnConnected() => OnConnectEvent?.Invoke();
    private void OnDisconnect() => OnDisconnectEvent?.Invoke();
    private void OnPanic() => OnPanicEvent?.Invoke();


    public delegate void BoolReceived(bool data);
    public delegate void IntReceived(int data);
    public delegate void StringReceived(string data);
    public delegate void BlobReceived(byte[] data);
    public delegate void DataReceived(object data, Type type);


    public event BoolReceived OnBoolReceivedEvent;
    public event IntReceived OnIntReceivedEvent;
    public event StringReceived OnStringReceivedEvent;
    public event BlobReceived OnBlobReceivedEvent;
    public event DataReceived OnDataReceivedEvent;

    private void OnBoolReceived(bool data) {
        OnBoolReceivedEvent?.Invoke(data);
        OnDataReceivedEvent?.Invoke(data, typeof(bool));
    }

    private void OnIntReceived(int data) {
        OnIntReceivedEvent?.Invoke(data);
        OnDataReceivedEvent?.Invoke(data, typeof(int));
    }

    private void OnStringReceived(string data) {
        OnStringReceivedEvent?.Invoke(data);
        OnDataReceivedEvent?.Invoke(data, typeof(string));
    }

    private void OnBlobReceived(byte[] data) {
        OnBlobReceivedEvent?.Invoke(data);
        OnDataReceivedEvent?.Invoke(data, typeof(byte[]));
    }

    #pragma warning restore CS1591
    #endregion
    
    /// <summary>Try to connect to the server and authenticate.</summary>
    /// <param name="password">The password used for validation, null if no password provided.</param>
    /// <returns>true if the connection and authentication succeeded; otherwise false.</returns>
    public bool TryConnect(string password = null) {

        Settings = ServerSettings.None;
        Settings.Password = new(password);

        try {

            TcpClient tcp = new();
            tcp.Connect(Hostname, port);

            Client = new(null, Settings, tcp);

            Task<bool> connection = ConnectionHandler();
            connection.Wait();
            return connection.Result;

        } catch {
            return false;
        }

    }

    /// <summary>Try to connect to the server and authenticate.</summary>
    /// <returns>A task that returns true if the connection and authentication succeeded; otherwise false.</returns>
    public async Task<bool> TryConnectAsync(string password = null, CancellationToken cancellationToken = default) {

        Settings = ServerSettings.None;
        Settings.Password = new(password);

        try {


            TcpClient tcp = new();
            await tcp.ConnectAsync(Hostname, port, cancellationToken);

            Client = new(null, Settings, tcp);

            return await ConnectionHandler();

        } catch {
            Close();
            return false;
        }

    }

    /// <summary>Disconnects from the server gracefully. Throws exception if the client is not connected.</summary>
    /// <exception cref="ArgumentException"/>
    public void Disconnect() {

        EnsureIsConnected();

        Client.SendPackage(new Package(Package.PackageTypes.Disconnect));

        HandleDisconnect();

    }
    /// <summary>Disconnects from the server gracefully. Throws exception if the client is not connected.</summary>
    /// <returns>A task that finishes when the client has disconnected</returns>
    /// <exception cref="ArgumentException"/>
    public async Task DisconnectAsync() {

        EnsureIsConnected();

        await Client.SendPackageAsync(new Package(Package.PackageTypes.Disconnect));
    }

    /// <summary>Closes the server.</summary>
    /// <remarks>Use <see cref="Disconnect"/> or <see cref="DisconnectAsync"/> to disconnect gracefully.</remarks>
    public void Close() {
        Client?.Close();
    }

    /// <summary>Test this connection until a test is successful, using test packages, and call panic for each failed test</summary>
    /// <returns>A task that finishes when the client's connection was either verified or terminated</returns>
    public async Task<bool> VerifyConnectionAsync() {
        return await ValidateConnection();
    }

    #region Send_Methods

    /// <summary>Send a bool package</summary>
    public void SendBool(bool data) {
        SendBoolAsync(data).Wait();
    }

    /// <summary>Send an int package</summary>
    public void SendInt(int data) {
        SendIntAsync(data).Wait();
    }

    /// <summary>Send a string package</summary>
    /// <remarks>The string is UTF-16 encoded</remarks>
    public void SendString(string data) {
        SendStringAsync(data).Wait();
    }

    /// <summary>Send a byte array package</summary>
    public void SendBlob(byte[] data) {
        SendBlobAsync(data).Wait();
    }

    /// <summary>Send a bool package</summary>
    /// <returns>A task that finishes when the data was sent</returns>
    public async Task SendBoolAsync(bool data) {

        EnsureIsConnected();

        byte[] bytes = BitConverter.GetBytes(data);
        await Client.SendPackageAsync(new Package(Package.PackageTypes.Data, Package.DataTypes.Bool, bytes, false));
        
    }

    /// <summary>Send an int package</summary>
    /// <returns>A task that finishes when the data was sent</returns>
    public async Task SendIntAsync(int data) {

        EnsureIsConnected();

        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, data);
        await Client.SendPackageAsync(new Package(Package.PackageTypes.Data, Package.DataTypes.Int, bytes, false));

    }

    /// <summary>Send a string package</summary>
    /// <remarks>The string is UTF-16 encoded</remarks>
    /// <returns>A task that finishes when the data was sent</returns>
    public async Task SendStringAsync(string data) {

        EnsureIsConnected();

        byte[] bytes = Encoding.Unicode.GetBytes(data);
        await Client.SendPackageAsync(new Package(Package.PackageTypes.Data, Package.DataTypes.String, bytes, false));
    }

    /// <summary>Send a byte array package</summary>
    /// <returns>A task that finishes when the data was sent</returns>
    public async Task SendBlobAsync(byte[] data) {

        EnsureIsConnected();

        await Client.SendPackageAsync(new Package(Package.PackageTypes.Data, Package.DataTypes.Blob, data));
    }

    #endregion

}