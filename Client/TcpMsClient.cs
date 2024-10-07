using System;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Diagnostics;
using AlvinSoft.TcpMs.Packages;

namespace AlvinSoft.TcpMs;

/// <summary>Allows simple usage of a TCP client to send/receive data to/from a <see cref="TcpMsServer"/> while implementing many useful features.</summary>
/// <remarks>Creates a new instance and assigns the address and port that is used to connect to the server. Does not start the connection process.</remarks>
/// <param name="hostname">The hostname that the client will connect to</param>
/// <param name="port">The TCP port that the client will connect to</param>
[UnsupportedOSPlatform("browser")]
public partial class TcpMsClient(string hostname, ushort port) {

    #region Events
    #pragma warning disable CS1591

    public delegate void Connected();
    public delegate void Disconnected();
    public delegate void Panic();

    public event Connected ConnectEvent;
    public event Disconnected DisconnectEvent;
    /// <summary>
    /// Triggered when this client had an error and resolved it. Use it to resend potentially important data that was lost.
    /// </summary>
    public event Panic OnPanicEvent;

    private void OnConnected() => ConnectEvent?.Invoke();
    private void OnDisconnect() => DisconnectEvent?.Invoke();
    private void OnPanic() => OnPanicEvent?.Invoke();


    public delegate void BlobReceived(byte[] data);
    public event BlobReceived BlobReceivedEvent;
    private void OnBlobReceived(byte[] data) => BlobReceivedEvent?.Invoke(data);

    public delegate void StringReceived(string data);
    public event StringReceived StringReceivedEvent;
    private void OnStringReceived(string data) => StringReceivedEvent?.Invoke(data);

#pragma warning restore CS1591
    #endregion

    /// <summary>Try to connect to the server and authenticate.</summary>
    /// <remarks>Leave <paramref name="password"/> set to <see langword="null"/> if the server doesn't use authentication/encryption.</remarks>
    /// <returns>A task that returns true if the connection (and authentication) succeeded; otherwise false.</returns>
    public async Task<bool> TryConnectAsync(string password = null, CancellationToken cancellationToken = default) {

        Settings = ServerSettings.None;
        if (password != null)
            Settings.Password = new(password);

        TcpClient tcp = new();
        try {

            await tcp.ConnectAsync(Hostname, port, cancellationToken);

        } catch (OperationCanceledException) {

            Debug.WriteLine($"TcpMsClient: timed out connecting to server");
            return false;

        } catch {

            Debug.WriteLine($"TcpMsClient: could not connect to server");
            return false;

        }

        Debug.WriteLine($"TcpMsClient: connected to server");

        ClientInstance = new(this, tcp);

        if (await ClientInstance.Manual_JoinClient()) {

            Debug.WriteLine($"TcpMsClient: joined server");

            ClientInstance.StartAll();

            Debug.WriteLine($"TcpMsClient: started obtain/dispatch threads");

            return true;

        } else {

            Debug.WriteLine($"TcpMsClient: could not join server");
            Close();
            return false;

        }

    }

    /// <summary>Disconnects from the server gracefully. Throws exception if the client is not connected.</summary>
    /// <returns>A task that finishes when the client has disconnected</returns>
    /// <exception cref="ArgumentException"/>
    public async Task DisconnectAsync() {

        await ClientInstance.StopAllAsync();
        await ClientInstance.Manual_DispatchDisconnect();
        Close();

    }

    /// <summary>Closes the client.</summary>
    /// <remarks>Use <see cref="DisconnectAsync"/> to disconnect gracefully.</remarks>
    public void Close() {
        ClientInstance?.Close();
        Encryption?.Dispose();
    }

    #region Send_Methods

    /// <summary>Queues a byte package to be sent to the server.</summary>
    /// <returns>A task that finishes when the data was sent</returns>
    public void SendByte(byte data) {

        byte[] bytes = [data];
        EncryptIfNeccessary(ref bytes);
        ClientInstance.Send(new Package(Package.PackageTypes.Data, Package.DataTypes.Byte, bytes, false));

    }
    /// <summary>Queues a string package to be sent to the server.</summary>
    /// <remarks>The string is UTF-16 encoded</remarks>
    /// <returns>A task that finishes when the data was sent</returns>
    public void SendString(string data) {

        byte[] bytes = Encoding.Unicode.GetBytes(data);
        EncryptIfNeccessary(ref bytes);
        ClientInstance.Send(new Package(Package.PackageTypes.Data, Package.DataTypes.String, bytes, false));
    }
    /// <summary>Queues a blob package to be sent to the server.</summary>
    /// <returns>A task that finishes when the data was sent</returns>
    public void SendBlob(byte[] data) {

        byte[] bytes = data;
        EncryptIfNeccessary(ref bytes);
        ClientInstance.Send(new Package(Package.PackageTypes.Data, Package.DataTypes.Blob, bytes, false));

    }

    /// <summary>Queues a byte package to be sent to the server and awaits to be dispatched.</summary>
    /// <returns>A task that finishes when the data was sent</returns>
    public async Task SendByteAsync(byte data) {

        byte[] bytes = [data];
        EncryptIfNeccessary(ref bytes);
        await ClientInstance.SendAsync(new Package(Package.PackageTypes.Data, Package.DataTypes.Byte, bytes, false, useTask: true));

    }
    /// <summary>Queues a string package to be sent to the server and awaits to be dispatched.</summary>
    /// <remarks>The string is UTF-16 encoded</remarks>
    /// <returns>A task that finishes when the data was sent</returns>
    public async Task SendStringAsync(string data) {

        byte[] bytes = Encoding.Unicode.GetBytes(data);
        EncryptIfNeccessary(ref bytes);
        await ClientInstance.SendAsync(new Package(Package.PackageTypes.Data, Package.DataTypes.String, bytes, false, useTask: true));
    }
    /// <summary>Queues a blob package to be sent to the server and awaits to be dispatched.</summary>
    /// <returns>A task that finishes when the data was sent</returns>
    public async Task SendBlobAsync(byte[] data) {

        byte[] bytes = data;
        EncryptIfNeccessary(ref bytes);
        await ClientInstance.SendAsync(new Package(Package.PackageTypes.Data, Package.DataTypes.Blob, bytes, false, useTask: true));

    }

    /// <summary>Queues a byte package to be sent to the server and awaits to be dispatched.</summary>
    /// <returns>A task that finishes when the data was sent</returns>
    public async Task SendByteAsync(byte data, CancellationToken cancellationToken) {

        byte[] bytes = [data];
        EncryptIfNeccessary(ref bytes);
        await ClientInstance.SendAsync(new Package(Package.PackageTypes.Data, Package.DataTypes.Byte, bytes, false, useTask: true), cancellationToken);

    }
    /// <summary>Queues a string package to be sent to the server and awaits to be dispatched.</summary>
    /// <remarks>The string is UTF-16 encoded</remarks>
    /// <returns>A task that finishes when the data was sent</returns>
    public async Task SendStringAsync(string data, CancellationToken cancellationToken) {

        byte[] bytes = Encoding.Unicode.GetBytes(data);
        EncryptIfNeccessary(ref bytes);
        await ClientInstance.SendAsync(new Package(Package.PackageTypes.Data, Package.DataTypes.String, bytes, false, useTask: true), cancellationToken);
    }
    /// <summary>Queues a blob package to be sent to the server and awaits to be dispatched.</summary>
    /// <returns>A task that finishes when the data was sent</returns>
    public async Task SendBlobAsync(byte[] data, CancellationToken cancellationToken) {

        byte[] bytes = data;
        EncryptIfNeccessary(ref bytes);
        await ClientInstance.SendAsync(new Package(Package.PackageTypes.Data, Package.DataTypes.Blob, bytes, false, useTask: true), cancellationToken);

    }

    #endregion

}