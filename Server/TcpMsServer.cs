using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace AlvinSoft.TcpMs;

//"Frontend"
/// <summary>Allows simple usage of a TCP server to send/receive data while implementing many useful features. Use <see cref="TcpMsClient"/> to connect to a server.</summary>
/// <remarks>Creates a new instance and assigns the address and port for the server. Does not start the server.</remarks>
/// <param name="address">The IP address that the server listens to</param>
/// <param name="port">The TCP port that the server uses</param>
/// <param name="settings">The settings that the server uses to send and process data</param>
public partial class TcpMsServer(IPAddress address, ushort port, ServerSettings settings) {

    #region Events
    #pragma warning disable CS1591

    public delegate void ClientConnected(byte[] clientId);
    public delegate void ClientDisconnected(byte[] clientId);
    public delegate void ClientPanic(byte[] clientId);

    public event ClientConnected OnClientConnectedEvent;
    public event ClientDisconnected OnClientDisconnectedEvent;
    public event ClientPanic OnClientPanicEvent;

    private void OnClientConnected(byte[] clientId) {
        OnClientConnectedEvent?.Invoke(clientId);
    }

    private void OnClientDisconnected(byte[] clientId) {
        OnClientDisconnectedEvent?.Invoke(clientId);
    }

    private void OnClientPanic(byte[] clientId) {
        OnClientPanicEvent?.Invoke(clientId);
    }


    public delegate void BoolReceived(byte[] clientId, bool data);
    public delegate void IntReceived(byte[] clientId, int data);
    public delegate void StringReceived(byte[] clientId, string data);
    public delegate void BlobReceived(byte[] clientId, byte[] data);
    public delegate void DataReceived(byte[] clientId, object data, Type type);


    public event BoolReceived OnBoolReceivedEvent;
    public event IntReceived OnIntReceivedEvent;
    public event StringReceived OnStringReceivedEvent;
    public event BlobReceived OnBlobReceivedEvent;
    public event DataReceived OnDataReceivedEvent;

    private void OnBoolReceived(byte[] clientId, bool data) {
        OnBoolReceivedEvent?.Invoke(clientId, data);
        OnDataReceivedEvent?.Invoke(clientId, data, typeof(bool));
    }

    private void OnIntReceived(byte[] clientId, int data) {
        OnIntReceivedEvent?.Invoke(clientId, data);
        OnDataReceivedEvent?.Invoke(clientId, data, typeof(int));
    }

    private void OnStringReceived(byte[] clientId, string data) {
        OnStringReceivedEvent?.Invoke(clientId, data);
        OnDataReceivedEvent?.Invoke(clientId, data, typeof(string));
    }

    private void OnBlobReceived(byte[] clientId, byte[] data) {
        OnBlobReceivedEvent?.Invoke(clientId, data);
        OnDataReceivedEvent?.Invoke(clientId, data, typeof(byte[]));
    }

    #pragma warning restore CS1591
    #endregion

    /// <summary>Starts the server and starts accepting connections</summary>
    public void Start() {

        if (Settings.EncryptionEnabled) {
            Encryption = new() {
                Password = Settings.Password
            };
        }

        Listener = new(IP, Port);
        Listener.Start();
        ServerCancellationTokenSource = new();

        EnsureIsListening();

        StartAcceptConnections();

    }

    /// <summary>Disconnects all clients and closes the server</summary>
    public void Stop() {

        ServerCancellationTokenSource?.Cancel();
        ListenerCancellationTokenSource?.Cancel();

        List<Task> tasks = [];

        //send disconnects in parallel
        foreach (Client client in Clients.Values)
            tasks.Add(client.SendPackageAsync(new Package(Package.PackageTypes.Disconnect)));

        Task.WaitAll([.. tasks], TimeoutCancellationToken);

        foreach (Client client in Clients.Values)
            client.Close();

        Close(false);

    }

    /// <returns>A task that finishes when all the clients were disconnected and the server has stopped</returns>
    public async Task StopAsync() {

        ServerCancellationTokenSource?.Cancel();
        ListenerCancellationTokenSource?.Cancel();

        List<Task> tasks = [];

        //send disconnects in parallel
        foreach (Client client in Clients.Values)
            tasks.Add(client.SendPackageAsync(new Package(Package.PackageTypes.Disconnect)));

        await Task.WhenAll(tasks);

        foreach (Client client in Clients.Values)
            client.Close();

        Close(false);

    }

    /// <summary>Closes the server</summary>
    /// <remarks>Use <see cref="Stop"/> to tell all clients that the server is stopping and stop the server gracefully</remarks>
    public void Close(bool cancelTokens) {

        if (cancelTokens) {
            ServerCancellationTokenSource?.Cancel();
            ListenerCancellationTokenSource?.Cancel();
        }

        Clients.Clear();
        Listener.Stop();
        Listener.Dispose();

    }

    /// <summary>Starts allowing clients to connect to the server.</summary>
    public void StartAcceptConnections() {

        if (!IsAllowingConnections)
            Task.Run(ListenerLoop);

    }

    /// <summary>Stops allowing clients to connect to the server.</summary>
    /// <remarks>Does not disconnect any client</remarks>
    public void StopAcceptConnections() {
        ListenerCancellationTokenSource?.Cancel();
    }

    /// <summary>Updates the server settings and tells all clients about the change</summary>
    /// <param name="newSettings">The new settings to be applied</param>
    public void UpdateServerSettings(ServerSettings newSettings) {

        List<Task> tasks = [];

        foreach(Client client in Clients.Values) {
            tasks.Add(SendServerSettings(client, newSettings));
        }

        Task.WaitAll([.. tasks]);

    }

    /// <summary>Test a client's connection using test packages and call panic for each failed test</summary>
    /// <returns>A task that finishes when the client's connection was either verified or terminated</returns>
    public async Task VerifyConnectionAsync(byte[] clientId) => await VerifyConnectionAsync(GetClient(clientId));

    internal async Task VerifyConnectionAsync(Client client) {
        if (!await ValidateConnection(client)) {
            await HandlePanic(client);
        }
    }

    /// <summary>Test all client's connections using test packages and call panic on the relevant client for each failed test</summary>
    public void VerifyAllConnections() {

        List<Task> tasks = [];
        foreach (Client client in Clients.Values)
            tasks.Add(VerifyConnectionAsync(client));

        Task.WaitAll([.. tasks]);
    }

    /// <summary>Test all client's connections using test packages and call panic on the relevant client for each failed test</summary>
    /// <returns>A task that finishes when all client's connections were either verified or terminated</returns>
    public async Task VerifyAllConnectionsAsync() {

        List<Task> tasks = [];
        foreach (Client client in Clients.Values)
            tasks.Add(VerifyConnectionAsync(client));

        await Task.WhenAll(tasks);
    }

    /// <summary>Updates the server settings and tells all clients about the change</summary>
    /// <param name="newSettings">The new settings to be applied</param>
    /// <returns>A task that finishes when all clients were informed of the new settings</returns>
    public async Task UpdateServerSettingsAsync(ServerSettings newSettings) {

        List<Task> tasks = [];

        foreach (Client client in Clients.Values) {
            tasks.Add(SendServerSettings(client, newSettings));
        }

        await Task.WhenAll([.. tasks]);

    }

    #region Send_Methods
    /// <summary>Sends a bool package to a client</summary>
    public void SendBool(byte[] clientId, bool data) => SendBoolAsync(clientId, data).Wait();

    /// <summary>Sends an int package to a client</summary>
    public void SendInt(byte[] clientId, int data) => SendIntAsync(clientId, data).Wait();

    /// <summary>Sends a string package to a client</summary>
    /// <remarks>The string is sent in UTF-16 format</remarks>
    public void SendString(byte[] clientId, string data) => SendStringAsync(clientId, data).Wait();

    /// <summary>Sends a byte array package to a client</summary>
    public void SendBlob(byte[] clientId, byte[] data) => SendBlobAsync(clientId, data).Wait();


    /// <summary>Sends a bool package to a client</summary>
    /// <returns>A task that finishes when the data was sent</returns>
    public async Task SendBoolAsync(byte[] clientId, bool data) => await SendBoolAsync(GetClient(clientId), data);

    /// <summary>Sends an int package to a client</summary>
    /// <returns>A task that finishes when the data was sent</returns>
    public async Task SendIntAsync(byte[] clientId, int data) => await SendIntAsync(GetClient(clientId), data);

    /// <summary>Sends a string package to a client</summary>
    /// <remarks>The string is sent in UTF-16 format</remarks>
    /// <returns>A task that finishes when the data was sent</returns>
    public async Task SendStringAsync(byte[] clientId, string data) => await SendStringAsync(GetClient(clientId), data);

    /// <summary>Sends a byte array package to a client</summary>
    /// <returns>A task that finishes when the data was sent</returns>
    public async Task SendBlobAsync(byte[] clientId, byte[] data) => await SendBlobAsync(GetClient(clientId), data);


    internal async Task SendBoolAsync(Client client, bool data) {

        EnsureIsListening();

        byte[] parsedData = BitConverter.GetBytes(data);
        EncryptIfNeccessary(ref parsedData);
        await client.SendPackageAsync(new(Package.PackageTypes.Data, Package.DataTypes.Bool, parsedData));
    }

    internal async Task SendIntAsync(Client client, int data) {

        EnsureIsListening();

        byte[] parsedData = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(parsedData, data);
        EncryptIfNeccessary(ref parsedData);
        await client.SendPackageAsync(new(Package.PackageTypes.Data, Package.DataTypes.Int, parsedData));
    }

    internal async Task SendStringAsync(Client client, string data) {

        EnsureIsListening();

        byte[] parsedData = Encoding.Unicode.GetBytes(data);
        EncryptIfNeccessary(ref parsedData);
        await client.SendPackageAsync(new(Package.PackageTypes.Data, Package.DataTypes.String, parsedData));
    }

    internal async Task SendBlobAsync(Client client, byte[] data) {

        EnsureIsListening();

        EncryptIfNeccessary(ref data);
        await client.SendPackageAsync(new(Package.PackageTypes.Data, Package.DataTypes.Blob, data));
    }
    #endregion

    #region Broadcast_Methods

    /// <summary>Send a bool package to all clients</summary>
    public void BroadcastBool(bool data) {

        EnsureIsListening();

        List<Task> tasks = [];

        foreach (Client client in Clients.Values)
            tasks.Add(SendBoolAsync(client.ID, data));

        Task.WaitAll([.. tasks]);

    }

    /// <summary>Send an int package to all clients</summary>
    public void BroadcastInt(int data) {

        EnsureIsListening();

        List<Task> tasks = [];

        foreach (Client client in Clients.Values)
            tasks.Add(SendIntAsync(client, data));

        Task.WaitAll([.. tasks]);
    }

    /// <summary>Send a string package to all clients</summary>
    public void BroadcastString(string data) {

        EnsureIsListening();

        List<Task> tasks = [];

        foreach (Client client in Clients.Values)
            tasks.Add(SendStringAsync(client, data));

        Task.WaitAll([.. tasks]);
    }

    /// <summary>Send a byte array package to all clients</summary>
    public void BroadcastBlob(byte[] data) {

        EnsureIsListening();

        List<Task> tasks = [];

        foreach (Client client in Clients.Values)
            tasks.Add(SendBlobAsync(client, data));

        Task.WaitAll([.. tasks]);
    }


    /// <summary>Send a bool package to all clients</summary>
    /// <returns>A task that finishes when the package was sent to all clients</returns>
    public async Task BroadcastBoolAsync(bool data) {

        EnsureIsListening();

        List<Task> tasks = [];

        foreach (Client client in Clients.Values)
            tasks.Add(SendBoolAsync(client, data));

        await Task.WhenAll([.. tasks]);
    }

    /// <summary>Send an int package to all clients</summary>
    /// <returns>A task that finishes when the package was sent to all clients</returns>
    public async Task BroadcastIntAsync(int data) {

        EnsureIsListening();

        List<Task> tasks = [];

        foreach (Client client in Clients.Values)
            tasks.Add(SendIntAsync(client, data));

        await Task.WhenAll([.. tasks]);
    }

    /// <summary>Send a string package to all clients</summary>
    /// <returns>A task that finishes when the package was sent to all clients</returns>
    public async Task BroadcastStringAsync(string data) {

        EnsureIsListening();

        List<Task> tasks = [];

        foreach (Client client in Clients.Values)
            tasks.Add(SendStringAsync(client, data));

        await Task.WhenAll([.. tasks]);
    }

    /// <summary>Send a byte array package to all clients</summary>
    /// <returns>A task that finishes when the package was sent to all clients</returns>
    public async Task BroadcastBlobAsync(byte[] data) {

        EnsureIsListening();

        List<Task> tasks = [];

        foreach (Client client in Clients.Values)
            tasks.Add(SendBlobAsync(client, data));

        await Task.WhenAll([.. tasks]);
    }

    #endregion


}