using System;
using System.Linq;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using AlvinSoft.Cryptography;

namespace AlvinSoft.TcpMs;

//"Backend"
partial class TcpMsServer {

    #region Fields

    private TcpListener Listener = new(address, port);
    private ServerSettings Settings = settings;
    private AesEncryption Encryption;
    private readonly ConcurrentDictionary<byte[], Client> Clients = [];

    /// <summary>The IP that the server listens to</summary>
    public IPAddress IP { get; } = address;
    /// <summary>The port that the server uses</summary>
    public ushort Port { get; } = port;

    /// <summary>true if the server is actively listening to connected clients; otherwise false</summary>
    public bool IsListening => ServerCancellationTokenSource != null && !ServerCancellationTokenSource.IsCancellationRequested;
    private CancellationTokenSource ServerCancellationTokenSource;
    /// <summary>true if the server is actively accepting new connections; otherwise false</summary>
    public bool IsAllowingConnections => ListenerCancellationTokenSource != null && !ListenerCancellationTokenSource.IsCancellationRequested;
    private CancellationTokenSource ListenerCancellationTokenSource;

    /// <summary>A token used for timeouts that is cancelled after 2 seconds.</summary>
    protected static CancellationToken TimeoutToken => new CancellationTokenSource(
#if DEBUG
        20000
#else
        2000
#endif
        ).Token;

    #endregion

    internal Client GetClient(byte[] id) => Clients[id];

    /// <summary>Throw an exception if <see cref="IsListening"/> is false</summary>
    protected void EnsureIsListening() {
        
        if (!IsListening)
            throw new TcpMsProtocolException("The server is not listening");
    }

    /// <summary>true if the connected client count is less than <see cref="ServerSettings.MaxClients"/>; otherwise false</summary>
    private bool ClientCountOk() => Clients.Count < Settings.MaxClients;

    private async Task ListenerLoop() {

        if (!ClientCountOk())
            return;

        ListenerCancellationTokenSource = new();

        try {
            while (ClientCountOk() && !ListenerCancellationTokenSource.IsCancellationRequested) {

                TcpClient client = await Listener.AcceptTcpClientAsync(ListenerCancellationTokenSource.Token); //accept connection

                if (client != null)
                    _ = Task.Run(() => ConnectionHandler(client)); //handle connection
            }
        } catch { }

        ListenerCancellationTokenSource?.Cancel();

    }

    private async Task ConnectionHandler(TcpClient tcpClient) {

        Client client = new(GenerateID(), Settings, tcpClient);
        Clients.TryAdd(client.ID, client);

        try {

            await SendServerSettings(client, Settings);

            if (Settings.EncryptionEnabled) {

                if (!await AuthenticateClient(client))
                    CloseClient(client);

                await SendEncryption(client);
            }

            await VerifyConnectionAsync(client);

            if (Settings.PingIntervalMs > 0)
                _ = new RepeatingAction(TimeSpan.FromMilliseconds(Settings.PingIntervalMs), async () => await Ping(client), client.CancelTokenSource.Token);

            OnClientConnected(client.ID);
        } catch {
            CloseClient(client);
        }

        start:
        try {

            while (IsListening && client.IsConnected) {

                Package package;
                try {
                    package = await client.ReceivePackageAsync(cancellationToken: client.CancelTokenSource.Token);
                } catch {
                    continue;
                }

                switch (package.PackageType) {

                    case Package.PackageTypes.Disconnect: {
                        HandleDisconnect(client);
                    }
                    return;

                    case Package.PackageTypes.Pong: {
                        HandlePong(client);
                    }
                    break;

                    case Package.PackageTypes.Test: {
                        await VerifyConnectionAsync(client);
                    }
                    break;

                    case Package.PackageTypes.Panic: {
                        await HandlePanic(client);
                    }
                    break;

                    case Package.PackageTypes.Data: {
                        HandleDataPackage(client, package);
                    }
                    break;

                    default: {
                        await SendError(client);
                    }
                    break;

                }

            }

            HandleDisconnect(client);

        } catch (Exception e) {

            if (client.IsConnected) {
                //something went wrong so send an error, but we can't know what is wrong, so restart the loop and allow the client to declare panic if needed
                await SendError(client);

                //errors like this still aren't tolerated, so consider this a panic
                client.CallPanic();
                if (client.PanicCount > Settings.MaxPanicsPerClient)
                    HandleDisconnect(client);

                //the loop will stop if the client is disconnected
                goto start;

            } else {
                HandleDisconnect(client);
            }

        }

    }

    /// <summary>Tries to authenticate the client and sends an error package if something fails</summary>
    /// <returns>true if everything succeeded; otherwise false</returns>
    private async Task<bool> AuthenticateClient(Client client) {

        try {

            RSAEncryption rsa = new();

            //send rsa public key, which the client uses to encrypt the password
            byte[] publicKey = rsa.Key.ExportPublicKey();
            await client.SendPackageAsync(new Package(Package.PackageTypes.AuthRSAPublicKey, Package.DataTypes.Blob, publicKey, false));

            //receive encrypted password
            var encryptedPassword = await client.ReceivePackageAsync(Package.PackageTypes.AuthEncryptedPassword, cancellationToken: TimeoutToken);

            //decrypt password
            string password = rsa.DecryptString(encryptedPassword.Data);

            //compare password
            if (password != null && Encryption.Password.Equals(password)) {
                await client.SendPackageAsync(new Package(Package.PackageTypes.AuthSuccess));
                return true;
            } else {
                await SendError(client);
                return false;
            }
        } catch {
            await SendError(client);
            return false;
        }

    }

    /// <summary>Tries to send the server's encryption to a client and sends an error package if something fails.</summary>
    private async Task<bool> SendEncryption(Client client) {

        try {

            //read rsa key, so the iv and salt can be encrypted
            var clientRsaPubKeyPackage = await client.ReceivePackageAsync(Package.PackageTypes.EncrRSAPublicKey, cancellationToken: TimeoutToken);

            RSAEncryption rsa = new(RSAKey.ImportPublicKey(clientRsaPubKeyPackage.Data));

            //encrypt and send iv
            byte[] encIv = rsa.EncryptBytes(Encryption.IV);
            await client.SendPackageAsync(new Package(Package.PackageTypes.EncrIV, Package.DataTypes.Blob, encIv, false));

            //encrypt and send salt
            byte[] encSalt = rsa.EncryptBytes(Encryption.Salt);
            await client.SendPackageAsync(new Package(Package.PackageTypes.EncrSalt, Package.DataTypes.Blob, encSalt, false));

            return true;

        } catch {
            await SendError(client);
            return false;
        }

    }

    /// <summary>Send a ping package to the client and create a timed action that handles the timeout</summary>
    private async Task Ping(Client client) {

        EnsureIsListening();

        await client.SendPackageAsync(new Package(Package.PackageTypes.Ping));

        _ = new TimedAction(TimeSpan.FromMilliseconds(Settings.PingTimeoutMs),
            () => {
                if (client.PingStatus == true)
                    client.PingStatus = false;
                else
                    HandleDisconnect(client);
            },
            client.CancelTokenSource.Token);

    }

    /// <summary>Send a client the server's settings</summary>
    private async Task SendServerSettings(Client client, ServerSettings settings) {

        EnsureIsListening();

        byte[] settingsData = settings.GetBytes();
        await client.SendPackageAsync(new Package(Package.PackageTypes.NewSettings, Package.DataTypes.Blob, settingsData, false));

    }

    /// <summary>Sends a validation package and uses a simple technique to check if the client understands the data. The validation happens <see cref="ServerSettings.ConnectionTestTries"/> times.</summary>
    /// <returns>true if all validations succeeded; otherwise false</returns>
    private async Task<bool> ValidateConnection(Client client) {

        EnsureIsListening();

        try {

            await client.WriteSync.WaitAsync();
            await client.ReadSync.WaitAsync();

            for (int i = 0; i < Settings.ConnectionTestTries; i++) {

                //generate random bytes of random length from 1 to 5
                byte[] test = RandomGen.GetBytes(Random.Shared.Next(1, 6));

                //encrypt test
                byte[] data;
                if (Settings.EncryptionEnabled)
                    data = Encryption.EncryptBytes(test);
                else
                    data = test;

                //send the test package
                await client.SendPackageAsync(new Package(Package.PackageTypes.Test, Package.DataTypes.Blob, data, false), false);

                //read the response
                Package response = await client.ReceivePackageAsync(Package.PackageTypes.Test, useSync: false, cancellationToken: TimeoutToken);

                //decrypt if neccessary
                if (Settings.EncryptionEnabled)
                    data = Encryption.DecryptBytes(response.Data);
                else
                    data = response.Data;

                if (data.Length == test.Length && data.Any(k => test.Any(l => l == k))) {
                    await client.SendPackageAsync(new Package(Package.PackageTypes.TestTrySuccess), false);
                    continue;
                } else {
                    await SendError(client, false);
                    return false;
                }

            }

        } catch {
            await SendError(client);
            return false;

        } finally {

            client.WriteSync.Release();
            client.ReadSync.Release();

        }

        return true;

    }

    /// <summary>Sends an error package to the client</summary>
    private async Task SendError(Client client, bool useSync = true) {

        EnsureIsListening();

        await client.SendPackageAsync(new Package(Package.PackageTypes.Error), useSync);

    }

    /// <summary>Closes the client and removes it from the clients list.</summary>
    private void CloseClient(Client client) {
        client.Close();
        Clients.TryRemove(client.ID, out _);
    }

    /// <summary>Closes a connected client.</summary>
    /// <remarks>Closes the client, removes it from the clients list and calls <c>OnClientDisconnected</c>.</remarks>
    private void HandleDisconnect(Client client) {
        CloseClient(client);
        OnClientDisconnected(client.ID);
    }

    /// <summary>Set a clients pong status to true</summary>
    private static void HandlePong(Client client) {

        client.PingStatus = true;

    }

    /// <summary>Processes data packages and calls the relevant event</summary>
    private void HandleDataPackage(Client client, Package package) {

        byte[] data = package.Data;
        DecryptIfNecessary(ref data);

        switch (package.DataType) {

            case Package.DataTypes.Bool: {

                bool value = BitConverter.ToBoolean(data);
                OnDataReceived(client.ID, value, Package.DataTypes.Bool);

            } break;

            case Package.DataTypes.Byte: {

                OnDataReceived(client.ID, data[0], Package.DataTypes.Byte);

            }
            break;

            case Package.DataTypes.Short: {

                short value = BinaryPrimitives.ReadInt16BigEndian(data);
                OnDataReceived(client.ID, value, Package.DataTypes.Short);

            }
            break;

            case Package.DataTypes.Int: {

                int value = BinaryPrimitives.ReadInt32BigEndian(data);
                OnDataReceived(client.ID, value, Package.DataTypes.Int);

            } break;

            case Package.DataTypes.Long: {

                long value = BinaryPrimitives.ReadInt64BigEndian(data);
                OnDataReceived(client.ID, value, Package.DataTypes.Long);

            }
            break;

            case Package.DataTypes.String: {

                string value = Encoding.Unicode.GetString(data);
                OnDataReceived(client.ID, value, Package.DataTypes.String);

            } break;

            case Package.DataTypes.Blob: {

                OnDataReceived(client.ID, data, Package.DataTypes.Blob);

            } break;
        
        }

    }

    /// <summary>Handles a panic request sent by the client</summary>
    /// <remarks>
    /// Sends a panic package, waits 100ms, sends the server settings, reauthenticates the client and resends the encryption, then validates the connection.
    /// Panic is recalled until the max panic count is reached or the validation succeedes.
    /// <c>OnClientPanic</c> is called only after the validation succeedes; otherwise the client is disconnected.
    /// </remarks>
    private async Task HandlePanic(Client client) {

        start: {

            if (client.PanicCount >= Settings.MaxPanicsPerClient) {
                await client.SendPackageAsync(new Package(Package.PackageTypes.Disconnect));
                HandleDisconnect(client);
                return;
            }

            client.CallPanic();

            await client.SendPackageAsync(new Package(Package.PackageTypes.Panic));

            //Wait for the client to "reset" by clearing its buffer, so it "finds" the next package header.
            await Task.Delay(100);

            await SendServerSettings(client, Settings);
            await AuthenticateClient(client);
            await SendEncryption(client);

        }

        if (await ValidateConnection(client) == false)
            goto start;

        OnClientPanic(client.ID);
    }

    private byte[] GenerateID(int length = 16) {

        byte[] id = new byte[length];
        Random.Shared.NextBytes(id);
        while (Clients.Any(k => k.Key == id))
            Random.Shared.NextBytes(id);

        return id;
    }

    private bool EncryptIfNecessary(ref byte[] buffer) {
        if (Settings.EncryptionEnabled) {
            buffer = Encryption.EncryptBytes(buffer);
            return true;
        }
        return false;
    }

    private bool DecryptIfNecessary(ref byte[] buffer) {

        if (Settings.EncryptionEnabled) {
            buffer = Encryption.DecryptBytes(buffer);
            return true;
        }
        return false;
    }

}