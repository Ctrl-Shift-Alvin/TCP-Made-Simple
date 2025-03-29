using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using AlvinSoft.TcpMs.Packages;

namespace AlvinSoft.TcpMs;

//"Frontend"
/// <summary>Allows simple usage of a TCP server to send/receive data while implementing many useful features. Use <see cref="TcpMsClient"/> to connect to a server.</summary>
[UnsupportedOSPlatform("browser")]
public partial class TcpMsServer {

    #region Events
#pragma warning disable CS1591

    public delegate void ClientEvent(byte[] clientId);

    public event ClientEvent ClientConnectedEvent;
    public event ClientEvent ClientDisconnectedEvent;
    public event ClientEvent ClientPanicEvent;

    private void OnClientConnected(byte[] clientId) => ClientConnectedEvent?.Invoke(clientId);
    private void OnClientDisconnected(byte[] clientId) => ClientDisconnectedEvent?.Invoke(clientId);
    private void OnClientPanic(byte[] clientId) => ClientPanicEvent?.Invoke(clientId);

    public delegate void BlobReceived(byte[] clientId, byte[] data);
    public event BlobReceived BlobReceivedEvent;
    private void OnBlobReceived(byte[] clientId, byte[] data) => BlobReceivedEvent?.Invoke(clientId, data);

    public delegate void StringReceived(byte[] clientId, string data);
    public event StringReceived StringReceivedEvent;
    private void OnStringReceived(byte[] clientId, string data) => StringReceivedEvent?.Invoke(clientId, data);

#pragma warning restore CS1591
    #endregion

    #region Start/Stop
    /// <summary>Starts the server and starts accepting connections.</summary>
    public async Task StartAsync() {

        Listener = new(IP, Port);
        Clients = [];

        if (Settings.EncryptionEnabled) {
            Encryption = new() {
                Password = Settings.Password
            };
        }

        Listener.Start();
        Dbg.Log("TcpMsServer: Started listener");

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
                client.Close();
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

            Dbg.Log("TcpMsServer: Awaiting listener loop to restart");

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

    /// <summary>Queues a byte package to be sent to a client.</summary>
    /// <remarks>Acts like <see cref="SendBlob(byte[], byte[])"/> but with length 1. Try to avoid this, since every package has a header data overhead.</remarks>
    public void SendByte(byte[] clientId, byte data) => SendByte(TryGetClient(clientId), data);

    /// <summary>Queues a string package to be sent to a client.</summary>
    /// <remarks>The string is sent in UTF-16 format</remarks>
    public void SendString(byte[] clientId, string data) => SendString(TryGetClient(clientId), data);

    /// <summary>Queues a blob package to be sent to a client.</summary>
    public void SendBlob(byte[] clientId, byte[] data) => SendBlob(TryGetClient(clientId), data);

    /// <summary>Queues a byte package to be sent to a client and waits to be dispatched.</summary>
    /// <remarks>Acts like <see cref="SendBlobAsync(byte[], byte[])"/> but with length 1. Try to avoid this, since every package has a header data overhead.</remarks>
    public async Task SendByteAsync(byte[] clientId, byte data) => await SendByteAsync(TryGetClient(clientId), data);

    /// <summary>Queues a string package to be sent to a client and waits to be dispatched.</summary>
    /// <remarks>The string is sent in UTF-16 format</remarks>
    public async Task SendStringAsync(byte[] clientId, string data) => await SendStringAsync(TryGetClient(clientId), data);

    /// <summary>Queues a blob package to be sent to a client and waits to be dispatched.</summary>
    public async Task SendBlobAsync(byte[] clientId, byte[] data) => await SendBlobAsync(TryGetClient(clientId), data);

    /// <summary>Queues a byte package to be sent to a client and waits to be dispatched.</summary>
    /// <remarks>Acts like <see cref="SendBlobAsync(byte[], byte[], CancellationToken)"/> but with length 1. Try to avoid this, since every package has a header data overhead.</remarks>
    public async Task SendByteAsync(byte[] clientId, byte data, CancellationToken cancellationToken) => await SendByteAsync(TryGetClient(clientId), data, cancellationToken);

    /// <summary>Queues a string package to be sent to a client and waits to be dispatched.</summary>
    /// <remarks>The string is sent in UTF-16 format</remarks>
    public async Task SendStringAsync(byte[] clientId, string data, CancellationToken cancellationToken) => await SendStringAsync(TryGetClient(clientId), data, cancellationToken);

    /// <summary>Queues a blob package to be sent to a client and waits to be dispatched.</summary>
    public async Task SendBlobAsync(byte[] clientId, byte[] data, CancellationToken cancellationToken) => await SendBlobAsync(TryGetClient(clientId), data, cancellationToken);



    private void SendByte(Client client, byte data) {

        byte[] parsedData = [data];
        EncryptIfNecessary(ref parsedData);
        client.Send(new Package(Package.PackageTypes.Data, Package.DataTypes.Byte, parsedData, copyData: false));
    }
    private void SendString(Client client, string data) {

        byte[] parsedData = Encoding.Unicode.GetBytes(data);
        EncryptIfNecessary(ref parsedData);
        client.Send(new Package(Package.PackageTypes.Data, Package.DataTypes.String, parsedData, copyData: false));
    }
    private void SendBlob(Client client, byte[] data) {

        EncryptIfNecessary(ref data);
        client.Send(new Package(Package.PackageTypes.Data, Package.DataTypes.Blob, data));
    }

    private async Task SendByteAsync(Client client, byte data) {

        byte[] parsedData = [data];
        EncryptIfNecessary(ref parsedData);
        await client.SendAsync(new Package(Package.PackageTypes.Data, Package.DataTypes.Byte, parsedData, useTask: true));

    }
    private async Task SendStringAsync(Client client, string data) {

        byte[] parsedData = Encoding.Unicode.GetBytes(data);
        EncryptIfNecessary(ref parsedData);
        await client.SendAsync(new Package(Package.PackageTypes.Data, Package.DataTypes.String, parsedData, useTask: true));

    }
    private async Task SendBlobAsync(Client client, byte[] data) {

        EncryptIfNecessary(ref data);
        await client.SendAsync(new Package(Package.PackageTypes.Data, Package.DataTypes.Blob, data, useTask: true));

    }

    private async Task SendByteAsync(Client client, byte data, CancellationToken cancellationToken) {

        byte[] parsedData = [data];
        EncryptIfNecessary(ref parsedData);
        await client.SendAsync(new Package(Package.PackageTypes.Data, Package.DataTypes.Byte, parsedData, useTask: true), cancellationToken);

    }
    private async Task SendStringAsync(Client client, string data, CancellationToken cancellationToken) {

        byte[] parsedData = Encoding.Unicode.GetBytes(data);
        EncryptIfNecessary(ref parsedData);
        await client.SendAsync(new Package(Package.PackageTypes.Data, Package.DataTypes.String, parsedData, useTask: true), cancellationToken);

    }
    private async Task SendBlobAsync(Client client, byte[] data, CancellationToken cancellationToken) {

        EncryptIfNecessary(ref data);
        await client.SendAsync(new Package(Package.PackageTypes.Data, Package.DataTypes.Blob, data, useTask: true), cancellationToken);

    }
    #endregion

    #region Broadcast_Methods

    /// <summary>Sends a byte package to all clients.</summary>
    public void BroadcastByte(byte data) {

        foreach (Client client in Clients.Values)
            SendByte(client, data);

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

    /// <summary>Queues a byte package to be sent to all clients and waits to be dispatched.</summary>
    public async Task BroadcastByteAsync(byte data) {

        List<Task> tasks = new(Clients.Count);

        foreach (Client client in Clients.Values)
            tasks.Add(SendByteAsync(client, data));

        await Task.WhenAll(tasks);

    }
    /// <summary>Queues a string package to be sent to all clients and waits to be dispatched.</summary>
    public async Task BroadcastStringAsync(string data) {

        List<Task> tasks = new(Clients.Count);

        foreach (Client client in Clients.Values)
            tasks.Add(SendStringAsync(client, data));

        await Task.WhenAll(tasks);

    }
    /// <summary>Queues a blob package to be sent to all clients and waits to be dispatched.</summary>
    public async Task BroadcastBlobAsync(byte[] data) {

        List<Task> tasks = new(Clients.Count);

        foreach (Client client in Clients.Values)
            tasks.Add(SendBlobAsync(client, data));

        await Task.WhenAll(tasks);

    }

    /// <summary>Queues a byte package to be sent to all clients and waits to be dispatched.</summary>
    public async Task BroadcastByteAsync(byte data, CancellationToken cancellationToken) {

        List<Task> tasks = new(Clients.Count);

        foreach (Client client in Clients.Values)
            tasks.Add(SendByteAsync(client, data, cancellationToken));

        await Task.WhenAll(tasks);

    }
    /// <summary>Queues a string package to be sent to all clients and waits to be dispatched.</summary>
    public async Task BroadcastStringAsync(string data, CancellationToken cancellationToken) {

        List<Task> tasks = new(Clients.Count);

        foreach (Client client in Clients.Values)
            tasks.Add(SendStringAsync(client, data, cancellationToken));

        await Task.WhenAll(tasks);

    }
    /// <summary>Queues a blob package to be sent to all clients and waits to be dispatched.</summary>
    public async Task BroadcastBlobAsync(byte[] data, CancellationToken cancellationToken) {

        Dbg.Log($"TcpMsServer: broadcasting data to {Clients.Count} {(Clients.Count == 1 ? "client" : "clients")}");

        List<Task> tasks = new(Clients.Count);

        foreach (Client client in Clients.Values)
            tasks.Add(SendBlobAsync(client, data, cancellationToken));

        await Task.WhenAll(tasks);

    }

    #endregion

}