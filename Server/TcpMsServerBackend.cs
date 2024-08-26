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
using System.Security.Cryptography;

namespace AlvinSoft.TcpMs;

//"Backend"
partial class TcpMsServer {

    #region Fields

    private TcpListener Listener;
    private ServerSettings Settings;
    private AesEncryption Encryption;
    private ConcurrentDictionary<byte[], Client> Clients; 

    /// <summary>The IP that the server listens to</summary>
    public IPAddress IP { get; internal set; }
    /// <summary>The port that the server uses</summary>
    public ushort Port { get; internal set; }

    /// <summary>true if this instance has called <see cref="StartAsync(IPAddress, ushort, ServerSettings)"/> and is not stopped; otherwise false.</summary>
    public bool IsStarted { get; internal set; }

    private Task ListenerLoopTask;
    private CancellationTokenSource ListenerLoopCancel;

    #endregion

    private class Client(byte[] id, TcpClient client, TcpMsServer server) : PackageHandler(client.GetStream()) {

        private readonly TcpMsServer ServerInstance = server;
        private ServerSettings Settings => ServerInstance.Settings;

        internal byte[] ID { get; } = id;
        internal TcpClient TcpClient = client;

        protected override CancellationToken TimeoutToken => new CancellationTokenSource(ServerInstance.Settings.ReceiveTimeoutMs).Token;


        #region Panic
        internal int PanicCount { get; private set; } = 0;
        internal int IncrementPanic() => PanicCount++;
        #endregion

        #region Ping
        private bool pingStatus;
        private readonly object pingSync = new();
        internal bool PongStatus {
            get {
                lock (pingSync)
                    return pingStatus;
            }
            set {
                lock (pingSync)
                    pingStatus = value;
            }
        }

        internal CancellationTokenSource PingCancel;
        internal void StartPing() {

            if (Settings.PingIntervalMs < 1)
                return;

            if (Settings.PingTimeoutMs >= Settings.PingIntervalMs)
                throw new ArgumentException("The ping interval cannot be lower than the ping timeout.", nameof(Settings.PingTimeoutMs));

            try {

                PingCancel = new();

                Task.Run(async () => {

                    while (!PingCancel.IsCancellationRequested) {

                        await Task.Delay(Settings.PingIntervalMs - Settings.PingTimeoutMs, PingCancel.Token);
                        Send(new Package(Package.PackageTypes.Ping));

                        await Task.Delay(Settings.PingTimeoutMs, PingCancel.Token);
                        if (PongStatus == false) {
                            await OnError(Errors.PingTimeout);
                        }

                    }

                }, PingCancel.Token);

            } catch (OperationCanceledException) {
                return;
            } finally {
                PingCancel?.Dispose();
            }

        }
        internal void StopPing() => PingCancel?.Cancel();
        #endregion

        #region Overrides
        protected override void OnReceivedDataPackage(Package package) {

            byte[] data = package.Data;
            ServerInstance.DecryptIfNecessary(ref data);

            switch (package.DataType) {

                case Package.DataTypes.Bool: {

                    bool value = BitConverter.ToBoolean(data);
                    ServerInstance.OnDataReceived(ID, value, Package.DataTypes.Bool);
                }
                break;

                case Package.DataTypes.Byte: {

                    ServerInstance.OnDataReceived(ID, data[0], Package.DataTypes.Byte);

                }
                break;

                case Package.DataTypes.Short: {

                    short value = BinaryPrimitives.ReadInt16BigEndian(data);
                    ServerInstance.OnDataReceived(ID, value, Package.DataTypes.Short);

                }
                break;

                case Package.DataTypes.Int: {

                    int value = BinaryPrimitives.ReadInt32BigEndian(data);
                    ServerInstance.OnDataReceived(ID, value, Package.DataTypes.Int);

                }
                break;

                case Package.DataTypes.Long: {

                    long value = BinaryPrimitives.ReadInt64BigEndian(data);
                    ServerInstance.OnDataReceived(ID, value, Package.DataTypes.Long);

                }
                break;

                case Package.DataTypes.String: {

                    string value = Encoding.Unicode.GetString(data);
                    ServerInstance.OnDataReceived(ID, value, Package.DataTypes.String);

                }
                break;

                case Package.DataTypes.Blob: {

                    ServerInstance.OnDataReceived(ID, data, Package.DataTypes.Blob);

                }
                break;

            }
        }

        protected override async Task OnReceivedInternalPackage(Package package) {

            async Task HandleOperation(Task<OperationResult> task) {

                await PauseAllAsync();

                switch (await task) {

                    case OperationResult.Disconnected: {

                        await OnError(Errors.Disconnected);

                    }
                    break;

                    case OperationResult.Failed: {

                        await OnError(Errors.IncorrectPackage);

                    }
                    break;

                    case OperationResult.Error: {

                        await OnError(Errors.ErrorPackage);

                    }
                    break;

                    case OperationResult.Succeeded: {
                        ResumeAll();
                    }
                    break;

                }

            }

            switch (package.PackageType) {

                case Package.PackageTypes.DisconnectRequest: {

                    await StopDispatchAsync();
                    //This task is running in the obtain handler, so do not await StopObtain here.
                    _ = StopObtainAsync();

                    await Manual_DispatchDisconnect();

                    ServerInstance.RemoveClient(this);

                }
                break;

                case Package.PackageTypes.Pong: {

                    PongStatus = true;

                }
                break;

                case Package.PackageTypes.TestRequest: {

                    await HandleOperation(Manual_ValidateConnection());

                }
                break;

                default: {
                    await OnError(Errors.UnexpectedPackage);
                }
                break;

            }

        }

        protected override async Task OnError(Errors error) {

            async Task HandleOperation(Task<OperationResult> task) {

                await PauseAllAsync();

                switch (await task) {

                    case OperationResult.Disconnected: {
                        await StopAll();
                        ServerInstance.RemoveClient(this);
                    }
                    return;

                    case OperationResult.Error: {
                        
                        var result = await Manual_HandlePanic();

                        if (result != OperationResult.Succeeded) {
                            await StopAll();
                            ServerInstance.RemoveClient(this);
                            return;
                        }
                        
                    }
                    break;

                    case OperationResult.Failed: {

                        var result = await Manual_HandlePanic();

                        if (result != OperationResult.Succeeded) {
                            await StopAll();
                            ServerInstance.RemoveClient(this);
                            return;
                        }

                    }
                    break;

                    case OperationResult.Succeeded:
                        break;

                }

                ResumeAll();

            }

            switch (error) {

                case Errors.ReadTimeout: {

                    await HandleOperation(Manual_HandlePanic());

                }
                return;

                case Errors.CannotWrite: {

                    ServerInstance.RemoveClient(this);

                }
                return;

                case Errors.CannotRead: {

                    ServerInstance.RemoveClient(this);

                }
                return;

                case Errors.ErrorPackage: {

                    await HandleOperation(Manual_HandlePanic());

                }
                return;


                case Errors.PingTimeout: {

                    await HandleOperation(Manual_HandlePanic());

                }
                return;

                case Errors.IncorrectPackage: {

                    await HandleOperation(Manual_HandlePanic());

                }
                return;

            }

        }

        public override void Start() {
            base.Start();
            StartPing();
        }

        public override Task StopAsync(bool awaitTasks = true) {
            StopPing();
            return base.StopAsync(awaitTasks);
        }

        public override void Close() {
            StopPing();
            base.Close();
        }
        #endregion

        //"Manual" means that the packages aren't sent using the queue, but directly. This means that the sending (and receiving) thread MUST be paused or stopped.
        #region Manual_Handlers

        /// <summary>Tries to authenticate the client.</summary>
        internal async Task<OperationResult> Manual_AuthenticateClient() {

            try {

                AesEncryption encryptionOut = new() {
                    Password = Settings.Password
                };
                byte[] challengeOut = RandomGen.GetBytes(32);
                byte[] encryptedChallengeOut = encryptionOut.EncryptBytes(challengeOut);
                byte[] challengeOutHash = SHA512.HashData(challengeOut);

                await DispatchPackageAsync(new Package(Package.PackageTypes.Auth_Salt, Package.DataTypes.Blob, encryptionOut.Salt));
                await DispatchPackageAsync(new Package(Package.PackageTypes.Auth_IV, Package.DataTypes.Blob, encryptionOut.IV));
                await DispatchPackageAsync(new Package(Package.PackageTypes.Auth_Challenge, Package.DataTypes.Blob, encryptedChallengeOut));

                Package response = await ObtainExpectedPackageAsync(Package.PackageTypes.Auth_Response);

                if (Enumerable.SequenceEqual(challengeOutHash, response.Data))
                    await DispatchPackageAsync(new Package(Package.PackageTypes.Auth_Success));
                else
                    await DispatchPackageAsync(new Package(Package.PackageTypes.Auth_Failure));

                AesEncryption encryptionIn;
                Package ivIn = await ObtainExpectedPackageAsync(Package.PackageTypes.Auth_IV);
                Package encryptedChallengeIn = await ObtainExpectedPackageAsync(Package.PackageTypes.Auth_Challenge);

                encryptionIn = new(Settings.Password, encryptionOut.Salt, ivIn.Data);
                byte[] challengeIn = encryptionIn.DecryptBytes(encryptedChallengeIn.Data);
                byte[] challengeInHash = SHA512.HashData(challengeIn);

                await DispatchPackageAsync(new Package(Package.PackageTypes.Auth_Response, Package.DataTypes.Blob, challengeInHash));

                response = await ObtainPackageAsync();
                return response.PackageType == Package.PackageTypes.Auth_Success ? OperationResult.Succeeded : OperationResult.Failed;


            } catch (TcpMsErrorPackageException) {

                return OperationResult.Error;

            } catch (TcpMsUnexpectedPackageException) {

                return OperationResult.Failed;

            } catch (TcpMsTimeoutException) {

                return OperationResult.Disconnected;

            } catch (InvalidOperationException) {

                return OperationResult.Disconnected;
            }

        }

        /// <summary>Tries to send the server's encryption to a client.</summary>
        internal async Task<OperationResult> Manual_SendEncryption() {

            try {

                //read rsa key, so the iv and salt can be encrypted
                var clientRsaPubKeyPackage = await ObtainExpectedPackageAsync(Package.PackageTypes.EncrRSAPublicKey);

                RSAEncryption rsa = new(RSAKey.ImportPublicKey(clientRsaPubKeyPackage.Data));

                //encrypt and send iv
                byte[] encIv = rsa.EncryptBytes(ServerInstance.Encryption.IV);
                await DispatchPackageAsync(new Package(Package.PackageTypes.EncrIV, Package.DataTypes.Blob, encIv, false));

                //encrypt and send salt
                byte[] encSalt = rsa.EncryptBytes(ServerInstance.Encryption.Salt);
                await DispatchPackageAsync(new Package(Package.PackageTypes.EncrSalt, Package.DataTypes.Blob, encSalt, false));

                return OperationResult.Succeeded;

            } catch (TcpMsUnexpectedPackageException) {

                return OperationResult.Error;

            } catch (TcpMsErrorPackageException) {

                return OperationResult.Error;

            } catch (InvalidOperationException) {

                return OperationResult.Disconnected;

            } catch (TcpMsTimeoutException) {

                return OperationResult.Disconnected;

            }

        }

        /// <summary>Tries to send server's settings to a client.</summary>
        internal async Task<OperationResult> Manual_DispatchServerSettings(ServerSettings settings) {

            byte[] settingsData = settings.GetBytes();
            try {

                await DispatchPackageAsync(new Package(Package.PackageTypes.Settings, Package.DataTypes.Blob, settingsData, false));

                if (Settings.EncryptionEnabled && Settings.Password != settings.Password) {
                    return await Manual_SendEncryption();
                }

                return OperationResult.Succeeded;

            } catch (InvalidOperationException) {
                return OperationResult.Disconnected;
            }

        }

        /// <summary>Sends a validation package and uses a simple technique to check if the client understands the data. The validation happens <see cref="ServerSettings.ConnectionTestTries"/> times.</summary>
        internal async Task<OperationResult> Manual_ValidateConnection() {

            try {

                await DispatchPackageAsync(new Package(Package.PackageTypes.TestRequest));

                for (int i = 0; i < Settings.ConnectionTestTries; i++) {

                    //generate random bytes of random length from 1 to 5
                    byte[] test = RandomGen.GetBytes(Random.Shared.Next(1, 6));

                    //encrypt test
                    byte[] data;
                    if (Settings.EncryptionEnabled)
                        data = ServerInstance.Encryption.EncryptBytes(test);
                    else
                        data = test;

                    //send the test package
                    await DispatchPackageAsync(new Package(Package.PackageTypes.Test, Package.DataTypes.Blob, data, false));

                    //read the response
                    Package response = await ObtainExpectedPackageAsync(Package.PackageTypes.Test);

                    //decrypt if neccessary
                    if (Settings.EncryptionEnabled)
                        data = ServerInstance.Encryption.DecryptBytes(response.Data);
                    else
                        data = response.Data;

                    if (data.Length == test.Length && data.Any(k => test.Any(l => l == k))) {
                        await DispatchPackageAsync(new Package(Package.PackageTypes.TestTrySuccess));
                        continue;
                    } else {
                        await DispatchPackageAsync(new Package(Package.PackageTypes.TestTryFailure));
                        return OperationResult.Failed;
                    }

                }

            } catch (TcpMsErrorPackageException) {
                return OperationResult.Error;
            } catch (TcpMsUnexpectedPackageException) {
                return OperationResult.Error;
            } catch (TcpMsTimeoutException) {
                return OperationResult.Disconnected;
            } catch (InvalidOperationException) {
                return OperationResult.Disconnected;
            }

            return OperationResult.Succeeded;

        }

        /// <summary>Calls panic on the client and tries to rejoin the client.</summary>
        internal async Task<OperationResult> Manual_HandlePanic() {

            try {

                if (PanicCount >= Settings.MaxPanicsPerClient) {
                    await Manual_DispatchDisconnect();
                    return OperationResult.Disconnected;
                }

                IncrementPanic();

                await DispatchPackageAsync(new Package(Package.PackageTypes.Panic));

                //wait for the client to "reset" by clearing its buffer, so it "finds" the next package header
                await Task.Delay(100);

                if (!await Manual_JoinClient()) {

                    await Manual_DispatchDisconnect();
                    return OperationResult.Disconnected;

                }


            } catch (InvalidOperationException) {

                return OperationResult.Disconnected;

            }

            ServerInstance.OnClientPanic(ID);
            return OperationResult.Succeeded;
        }

        /// <summary>Sends an error package to the client.</summary>
        /// <returns>A task that returns true when the package was successfully sent; otherwise false.</returns>
        internal async Task<bool> Manual_DispatchError() {
            try {
                await DispatchPackageAsync(new Package(Package.PackageTypes.Error));
                return true;
            } catch (InvalidOperationException) {
                return false;
            }
        }

        /// <summary>
        /// Sends the server settings and if necessary, authenticates and sends encryption. Then validates once.
        /// </summary>
        /// <returns>true if everything succeeded; otherwise false.</returns>
        internal async Task<bool> Manual_JoinClient() {

            if (await Manual_DispatchServerSettings(Settings) != OperationResult.Succeeded)
                return false;

            if (Settings.EncryptionEnabled) {

                if (await Manual_AuthenticateClient() != OperationResult.Succeeded)
                    return false;

                if (await Manual_SendEncryption() != OperationResult.Succeeded)
                    return false;

                if (await Manual_ValidateConnection() != OperationResult.Succeeded)
                    return false;
            }
            return true;

        }

        internal async Task Manual_DispatchDisconnect() {

            try {
                await DispatchPackageAsync(new Package(Package.PackageTypes.Disconnect));
            } finally { }

        }


        #endregion

    }


    private Client GetClient(byte[] id) => Clients[id];

    private Client TryGetClient(byte[] id) {

        if (Clients.TryGetValue(id, out var client))
            return client;
        else
            throw new ArgumentException("Client with this ID not found.", nameof(id));
    }

    /// <summary>true if the connected client count is less than <see cref="ServerSettings.MaxClients"/>; otherwise false</summary>
    private bool ClientCountOk() => Clients.Count < Settings.MaxClients;

    private async Task ListenerLoop() {

        if (!ClientCountOk())
            return;

        ListenerLoopCancel = new();

        while (ClientCountOk() && !ListenerLoopCancel.IsCancellationRequested) {

            TcpClient client;
            try {
                client = await Listener.AcceptTcpClientAsync(ListenerLoopCancel.Token); //accept connection
            } catch (OperationCanceledException) {
                break;
            }

            if (client != null)
                _ = Task.Run(() => ConnectionHandler(client)); //handle connection
        }

    }

    private async Task ConnectionHandler(TcpClient tcpClient) {

        Client client = new(GenerateID(), tcpClient, this);

        if (!await client.Manual_JoinClient()) {
            RemoveClient(client);
            return;
        }

        Clients.TryAdd(client.ID, client);
        OnClientConnected(client.ID);

        client.Start();

    }

    private void BroadcastPackage(Package package) {
        foreach (Client client in Clients.Values)
            client.Send(package);
    }

    /// <summary>Tries to remove <paramref name="client"/> from <see cref="Clients"/>. Calls <see cref="OnClientDisconnected(byte[])"/> if necessary.</summary>
    private void RemoveClient(Client client) {
        if (Clients.TryRemove(client.ID, out _))
            OnClientDisconnected(client.ID);
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