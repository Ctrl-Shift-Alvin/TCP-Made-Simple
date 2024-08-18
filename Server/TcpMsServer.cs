using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace AlvinSoft.TcpMs;

//"Frontend"
/// <summary>Allows simple usage of a TCP server to send/receive data while implementing many useful features. Use <see cref="TcpMsClient"/> to connect to a server.</summary>
[UnsupportedOSPlatform("browser")]
public partial class TcpMsServer {

    #region Events
#pragma warning disable CS1591

    public delegate void ClientEvent(byte[] clientId);

    public event ClientEvent ClientConnected;
    public event ClientEvent ClientDisconnected;
    public event ClientEvent ClientPanic;

    private void OnClientConnected(byte[] clientId) => ClientConnected?.Invoke(clientId);
    private void OnClientDisconnected(byte[] clientId) => ClientDisconnected?.Invoke(clientId);
    private void OnClientPanic(byte[] clientId) => ClientPanic?.Invoke(clientId);

    public delegate void DataEvent(byte[] clientId, object data, Package.DataTypes type);
    public event DataEvent DataReceived;

    private void OnDataReceived(byte[] clientId, object data, Package.DataTypes type) => DataReceived?.Invoke(clientId, data, type);

#pragma warning restore CS1591
    #endregion

    #region Start/Stop
    /// <summary>Starts the server and starts accepting connections.</summary>
    public async Task StartAsync(IPAddress address, ushort port, ServerSettings settings) {

        IP = address;
        Port = port;
        Settings = settings;
        Listener = new(address, port);
        Clients = [];

        if (Settings.EncryptionEnabled) {
            Encryption = new() {
                Password = Settings.Password
            };
        }

        Listener.Start();

        await StartAcceptConnectionsAsync();

        IsStarted = true;

    }

    /// <summary>Disconnects all clients and closes the server.</summary>
    /// <returns>A task that finishes when all clients were disconnected.</returns>
    public async Task StopAsync() {

        ListenerLoopCancel?.Cancel();

        List<Task> tasks = [];

        foreach (Client client in Clients.Values)
            tasks.Add(DisconnectClient(client));

        await Task.WhenAll([.. tasks]);

        Close();

    }

    /// <summary>Closes the server forcefully.</summary>
    /// <remarks>Use <see cref="StopAsync()"/> to tell all clients that the server is stopping and stop the server gracefully.</remarks>
    public void Close() {

        ListenerLoopCancel?.Cancel();

        if (Clients != null && !Clients.IsEmpty) {
            foreach (Client client in Clients.Values)
                client.StopAsync();
        }

        Encryption?.Dispose();
        Clients?.Clear();
        Listener?.Stop();
        Listener?.Dispose();

    }
    #endregion

    /// <summary>Starts allowing clients to connect to the server.</summary>
    /// <remarks>If the listener loop was canceled but hasn't finished yet, it is awaited first.</remarks>
    public async Task StartAcceptConnectionsAsync() {

        if (ListenerLoopCancel == null) {

            ListenerLoopTask = Task.Run(ListenerLoop);

        } else if (ListenerLoopCancel.IsCancellationRequested) {

            await ListenerLoopTask;
            ListenerLoopTask = Task.Run(ListenerLoop);

        }


    }

    /// <summary>Stops allowing clients to connect to the server and wait for the listener thread to finish.</summary>
    /// <remarks>Use a discard to omit waiting for the listener thread to finish.</remarks>
    /// <returns>A task that finishes when the listener thread has finished.</returns>
    public async Task StopAcceptConnections() {
        ListenerLoopCancel?.Cancel();
        if (ListenerLoopTask != null)
            await ListenerLoopTask;
    }

    /// <summary>Test all client's connections using test packages and call panic on the relevant client for each failed test</summary>
    /// <returns>A task that finishes when all client's connections were either verified or terminated</returns>
    public async Task VerifyAllConnectionsAsync() {

        List<Task> tasks = [];
        foreach (Client client in Clients.Values)
            tasks.Add(VerifyConnectionAsync(client));

        await Task.WhenAll(tasks);
    }

    #region Client_Methods

    /// <summary>
    /// Disconnect a client gracefully.
    /// </summary>
    /// <param name="id">The id of the client</param>
    public async Task DisconnectClient(byte[] id) => await DisconnectClient(TryGetClient(id));
    private async Task DisconnectClient(Client client) {
        client.Send(new Package(Package.PackageTypes.Disconnect));
        await client.StopAndDispatchRest();
        RemoveClient(client);
    }


    #endregion

    #region Send_Methods
    /// <summary>Sends a bool package to a client</summary>
    public void SendBool(byte[] clientId, bool data) => SendBool(TryGetClient(clientId), data);

    /// <summary>Sends a byte package to a client</summary>
    public void SendByte(byte[] clientId, byte data) => SendByte(TryGetClient(clientId), data);

    /// <summary>Sends a <see cref="short"/> package to a client</summary>
    public void SendShort(byte[] clientId, short data) => SendShort(TryGetClient(clientId), data);

    /// <summary>Sends an int package to a client</summary>
    public void SendInt(byte[] clientId, int data) => SendInt(TryGetClient(clientId), data);

    /// <summary>Sends a <see cref="long"/> package to a client</summary>
    public void SendLong(byte[] clientId, long data) => SendLong(TryGetClient(clientId), data);

    /// <summary>Sends a string package to a client</summary>
    /// <remarks>The string is sent in UTF-16 format</remarks>
    public void SendString(byte[] clientId, string data) => SendString(TryGetClient(clientId), data);

    /// <summary>Sends a byte array package to a client</summary>
    public void SendBlob(byte[] clientId, byte[] data) => SendBlob(TryGetClient(clientId), data);


    private void SendBool(Client client, bool data) {
        byte[] parsedData = BitConverter.GetBytes(data);
        EncryptIfNecessary(ref parsedData);
        client.Send(new Package(Package.PackageTypes.Data, Package.DataTypes.Bool, parsedData));
    }

    private void SendByte(Client client, byte data) {

        byte[] parsedData = [data];
        EncryptIfNecessary(ref parsedData);
        client.Send(new Package(Package.PackageTypes.Data, Package.DataTypes.Byte, parsedData));
    }

    private void SendShort(Client client, short data) {

        byte[] parsedData = new byte[2];
        BinaryPrimitives.WriteInt16BigEndian(parsedData, data);
        EncryptIfNecessary(ref parsedData);
        client.Send(new Package(Package.PackageTypes.Data, Package.DataTypes.Short, parsedData));
    }

    private void SendInt(Client client, int data) {

        byte[] parsedData = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(parsedData, data);
        EncryptIfNecessary(ref parsedData);
        client.Send(new Package(Package.PackageTypes.Data, Package.DataTypes.Int, parsedData));
    }

    private void SendLong(Client client, long data) {

        byte[] parsedData = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(parsedData, data);
        EncryptIfNecessary(ref parsedData);
        client.Send(new Package(Package.PackageTypes.Data, Package.DataTypes.Long, parsedData));
    }

    private void SendString(Client client, string data) {

        byte[] parsedData = Encoding.Unicode.GetBytes(data);
        EncryptIfNecessary(ref parsedData);
        client.Send(new Package(Package.PackageTypes.Data, Package.DataTypes.String, parsedData));
    }

    private void SendBlob(Client client, byte[] data) {

        EncryptIfNecessary(ref data);
        client.Send(new Package(Package.PackageTypes.Data, Package.DataTypes.Blob, data));
    }
    #endregion

    #region Broadcast_Methods

    /// <summary>Sends a bool package to all clients.</summary>
    public void BroadcastBool(bool data) {

        foreach (Client client in Clients.Values)
            SendBool(client.ID, data);

    }

    /// <summary>Sends a byte package to all clients.</summary>
    public void BroadcastByte(byte data) {

        foreach (Client client in Clients.Values)
            SendByte(client, data);

    }

    /// <summary>Sends a <see cref="short"/> package to all clients.</summary>
    public void BroadcastShort(short data) {

        foreach (Client client in Clients.Values)
            SendShort(client, data);

    }

    /// <summary>Sends an int package to all clients.</summary>
    public void BroadcastInt(int data) {

        foreach (Client client in Clients.Values)
            SendInt(client, data);

    }

    /// <summary>Sends a <see cref="long"/> package to all clients.</summary>
    public void BroadcastLong(long data) {

        foreach (Client client in Clients.Values)
            SendLong(client, data);

    }

    /// <summary>Sends a string package to all clients.</summary>
    public void BroadcastString(string data) {

        foreach (Client client in Clients.Values)
            SendString(client, data);

    }

    /// <summary>Sends a byte array package to all clients.</summary>
    public void BroadcastBlob(byte[] data) {

        foreach (Client client in Clients.Values)
            SendBlob(client, data);

    }

    #endregion

}