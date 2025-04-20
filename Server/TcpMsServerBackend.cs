using System;
using System.Linq;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using AlvinSoft.Cryptography;
using AlvinSoft.TcpMs.Packages;
using System.Collections.Generic;

namespace AlvinSoft.TcpMs {

    //"Backend"
    /// <summary>
    /// Allows simple usage of a TCP server to send/receive data to/from multiple <see cref="TcpListener"/> while implementing many useful features.
    /// </summary>
    partial class TcpMsServer {

        

        #region Fields

        private TcpListener Listener { get; set; }
        private ServerSettings Settings { get; }
        private AesEncryption Encryption { get; set; }
        private ConcurrentDictionary<byte[], Client> Clients { get; set; }

        /// <summary>The IP that the server listens to.</summary>
        public IPAddress IP { get; internal set; }
        /// <summary>The port that the server uses.</summary>
        public ushort Port { get; internal set; }

        /// <summary>true if this instance has called <see cref="StartAsync()"/> and is not stopped; otherwise false.</summary>
        public bool IsStarted { get; internal set; }
        /// <summary>The currently connected client count.</summary>
        public int ClientCount => Clients.Count;

        /// <summary>The IDs of all currently connected clients.</summary>
        /// <remarks>No specific order is guaranteed!</remarks>
        public IEnumerable<byte[]> ClientIDs => Clients.Keys;

        private Task ListenerLoopTask;
        private CancellationTokenSource ListenerLoopCancel;

        #endregion

        private class Client : PackageHandler {

            public Client(byte[] id, TcpClient client, TcpMsServer server) : base(client.GetStream()) {
                ServerInstance = server;
                ID = id;
                ReadableID = Convert.ToBase64String(id);
                TcpClient = client;
            }

            private TcpMsServer ServerInstance { get; }
            private ServerSettings Settings => ServerInstance.Settings;

            internal byte[] ID { get; }
            internal string ReadableID { get; }
            internal TcpClient TcpClient { get; }

            protected override CancellationToken TimeoutToken => new CancellationTokenSource(ServerInstance.Settings.ReceiveTimeoutMs).Token;


            #region Panic
            internal int PanicCount { get; private set; } = 0;
            internal int IncrementPanic() => PanicCount++;
            #endregion

            #region Ping
            private volatile bool pingStatus;
            internal bool PongStatus { get => pingStatus; set => pingStatus = value; }

            private CancellationTokenSource PingCancel;
            internal void StartPing() {

                if (Settings.PingIntervalMs < 1)
                    return;

                Task.Run(async () => {

                    PingCancel = new CancellationTokenSource();

                    while (!PingCancel.IsCancellationRequested) {

                        await Task.Delay(Settings.PingIntervalMs - Settings.PingTimeoutMs, PingCancel.Token);

                        if (PongStatus) //do not send ping if a data package was already received
                            continue;

                        await SendAsync(new Package(Package.PackageTypes.Ping));

                        Dbg.Log($"TcpMsServer.Client ID={ReadableID}: Sent ping");
                        await Task.Delay(Settings.PingTimeoutMs, PingCancel.Token);

                        if (PongStatus == false) {
                            await OnError(Errors.PingTimeout);
                        }

                    }
                    PingCancel.Dispose();
                });

            }
            internal void StopPing() {
                try {
                    PingCancel?.Cancel();
                } catch (ObjectDisposedException) { }
            }
            #endregion

            #region Overrides
            protected override void OnReceivedDataPackage(Package package) {

                Dbg.Log($"TcpMsServer ID={ReadableID}: received data package");

                byte[] data = package.Data;
                ServerInstance.DecryptIfNecessary(ref data);

                if (data == null || data.Length == 0)
                    return;

                switch (package.DataType) {


                    case Package.DataTypes.Byte: {

                        if (data.Length > 1)
                            goto default;

                        ServerInstance.OnBlobReceived(ID, data);

                    }
                    break;

                    case Package.DataTypes.String: {

                        string value = Encoding.Unicode.GetString(data);
                        ServerInstance.OnStringReceived(ID, value);

                    }
                    break;

                    case Package.DataTypes.Blob: {

                        ServerInstance.OnBlobReceived(ID, data);

                    }
                    break;

                    default: {
                        _ = OnError(Errors.UnexpectedPackage);
                    }
                    break;

                }
                PongStatus = true; //do not need to ping if receiving packages from client

            }

            protected override async Task OnReceivedInternalPackage(Package package) {

                async Task HandleOperation(Task<OperationResult> task) {

                    await PauseAllAsync();

                    switch (await task) {

                        case OperationResult.Disconnected: {

                            _ = OnError(Errors.Disconnected);

                        }
                        break;

                        case OperationResult.Failed: {

                            _ = OnError(Errors.IncorrectPackage);

                        }
                        break;

                        case OperationResult.Error: {

                            _ = OnError(Errors.ErrorPackage);

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

                        //This task is running in the obtain handler, so do not await StopObtain here, which "await StopAll()" would do.
                        await StopDispatchAsync();
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
                        _ = OnError(Errors.UnexpectedPackage);
                    }
                    break;

                }

            }

            protected override async Task OnError(Errors error) {

                async Task HandlePanic() {

                    await PauseAllAsync();

                    var result = await Manual_HandlePanic();

                    if (result == true) {
                        ResumeAll();
                    } else {
                        await StopAllAsync();
                        ServerInstance.RemoveClient(this);
                    }


                }

                switch (error) {

                    case Errors.ReadTimeout: {

                        await HandlePanic();

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

                        await HandlePanic();

                    }
                    return;


                    case Errors.PingTimeout: {

                        await HandlePanic();

                    }
                    return;

                    case Errors.IncorrectPackage: {

                        await HandlePanic();

                    }
                    return;

                }

            }

            public override void StartAll() {
                base.StartAll();
                StartPing();
            }

            public override Task StopAllAsync() {
                StopPing();
                return base.StopAllAsync();
            }

            public override void Close() {
                StopPing();
                base.Close();
            }
            #endregion

            //"Manual" means that the packages aren't sent using the queue, but directly. This means that the sending (and receiving) thread MUST be paused or stopped.
            #region Manual_Handlers

            /// <summary>Tries to authenticate the client.</summary>
            internal async Task<bool> Manual_AuthenticateClient() {

                try {

                    //CLIENT CHALLENGE -------------
                    //send info package with 255 if no encryption is used, and anything else to authenticate
                    if (Settings.EncryptionEnabled) {
                        await DispatchPackageAsync(new Package(Package.PackageTypes.Auth_Info, Package.DataTypes.Blob, 0));
                    } else {
                        await DispatchPackageAsync(new Package(Package.PackageTypes.Auth_Info, Package.DataTypes.Blob, byte.MaxValue));
                        return true;
                    }

                    AesEncryption encryptionOut = new AesEncryption() {
                        Password = Settings.Password
                    };
                    byte[] challengeOut = Rdm.GetBytes(32);
                    byte[] encryptedChallengeOut = encryptionOut.EncryptBytes(challengeOut);
                    byte[] challengeOutHash;
                    using (SHA512 sha = SHA512.Create()) {
                        challengeOutHash = sha.ComputeHash(challengeOut);
                    }

                    await DispatchPackageAsync(new Package(Package.PackageTypes.Auth_Salt, Package.DataTypes.Blob, encryptionOut.Salt));
                    await DispatchPackageAsync(new Package(Package.PackageTypes.Auth_IV, Package.DataTypes.Blob, encryptionOut.IV));
                    await DispatchPackageAsync(new Package(Package.PackageTypes.Auth_Challenge, Package.DataTypes.Blob, encryptedChallengeOut));

                    Package response = await ObtainExpectedPackageAsync(Package.PackageTypes.Auth_Response);

                    if (Enumerable.SequenceEqual(challengeOutHash, response.Data)) {
                        await DispatchPackageAsync(new Package(Package.PackageTypes.Auth_Success));
                    } else {
                        await DispatchPackageAsync(new Package(Package.PackageTypes.Auth_Failure));
                        return false;
                    }

                    //SERVER CHALLENGE -------------
                    Package saltIn = await ObtainExpectedPackageAsync(Package.PackageTypes.Auth_Salt);
                    Package ivIn = await ObtainExpectedPackageAsync(Package.PackageTypes.Auth_IV);
                    Package encryptedChallengeIn = await ObtainExpectedPackageAsync(Package.PackageTypes.Auth_Challenge);

                    AesEncryption encryptionIn = new AesEncryption(Settings.Password, saltIn.Data, ivIn.Data);
                    byte[] challengeIn = encryptionIn.DecryptBytes(encryptedChallengeIn.Data);
                    if (challengeIn == null) {
                        //decryption failed
                        await DispatchPackageAsync(new Package(Package.PackageTypes.Auth_Failure));
                        return false;
                    }
                    byte[] challengeInHash;
                    using (SHA512 sha = SHA512.Create()) {
                        challengeInHash = sha.ComputeHash(challengeIn);
                    }

                    await DispatchPackageAsync(new Package(Package.PackageTypes.Auth_Response, Package.DataTypes.Blob, challengeInHash));

                    response = await ObtainExpectedPackageAsync(new Package.PackageTypes[] { Package.PackageTypes.Auth_Success, Package.PackageTypes.Auth_Failure });
                    return response.PackageType == Package.PackageTypes.Auth_Success;


                } catch (TcpMsErrorPackageException) {

                    return false;

                } catch (TcpMsUnexpectedPackageException) {

                    await Manual_DispatchErrorAsync();
                    return false;

                } catch (TcpMsTimeoutException) {

                    return false;

                } catch (InvalidOperationException) {

                    return false;

                }

            }

            /// <summary>Tries to send the server's encryption to a client.</summary>
            internal async Task<OperationResult> Manual_SendEncryption() {

                try {

                    //send iv
                    await DispatchPackageAsync(new Package(Package.PackageTypes.EncrIV, Package.DataTypes.Blob, ServerInstance.Encryption.IV, true));

                    //send salt
                    await DispatchPackageAsync(new Package(Package.PackageTypes.EncrSalt, Package.DataTypes.Blob, ServerInstance.Encryption.Salt, true));

                    return OperationResult.Succeeded;

                } catch (TcpMsUnexpectedPackageException) {

                    await Manual_DispatchErrorAsync();
                    return OperationResult.Error;

                } catch (TcpMsErrorPackageException) {

                    return OperationResult.Error;

                } catch (InvalidOperationException) {

                    return OperationResult.Disconnected;

                } catch (TcpMsTimeoutException) {

                    return OperationResult.Disconnected;

                }

            }

            /// <summary>Sends a validation package and uses a simple technique to check if the client understands the data. The validation happens <see cref="ServerSettings.ConnectionTestTries"/> times.</summary>
            internal async Task<OperationResult> Manual_ValidateConnection() {

                try {

                    await DispatchPackageAsync(new Package(Package.PackageTypes.TestRequest));

                    for (int i = 0; i < Settings.ConnectionTestTries; i++) {

                        //generate random bytes of random length from 1 to 5
                        byte[] test = Rdm.GetBytes(Rdm.Next(1, 6));

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

                    await Manual_DispatchErrorAsync();
                    return OperationResult.Error;

                } catch (TcpMsTimeoutException) {

                    return OperationResult.Disconnected;

                } catch (InvalidOperationException) {

                    return OperationResult.Disconnected;

                }

                return OperationResult.Succeeded;

            }

            /// <summary>Checks max panic count, announces panic and tries to rejoin client. If anything fails, a disconnect is dispatched and returns <see langword="false"/>.</summary>
            internal async Task<bool> Manual_HandlePanic() {

                try {

                    if (PanicCount >= Settings.MaxPanicsPerClient) {
                        await Manual_DispatchDisconnect();
                        return false;
                    }

                    IncrementPanic();

                    await DispatchPackageAsync(new Package(Package.PackageTypes.Panic));

                    //wait for the client to "reset" by clearing its buffer, so it "finds" the next package header
                    await Task.Delay(100);

                    if (!await Manual_JoinClient()) {

                        await Manual_DispatchDisconnect();
                        return false;

                    }


                } catch (InvalidOperationException) {

                    await Manual_DispatchDisconnect();
                    return false;

                }

                ServerInstance.OnClientPanic(ID);
                return true;
            }

            /// <summary>Sends an error package to the client.</summary>
            /// <returns>A task that returns true when the package was successfully sent; otherwise false.</returns>
            internal async Task<bool> Manual_DispatchErrorAsync() {
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

                if (await Manual_AuthenticateClient() == false)
                    return false;

                Dbg.Log($"TcpMsServer.Client ID={ReadableID}: authenticated client");

                if (Settings.EncryptionEnabled) {

                    if (await Manual_SendEncryption() != OperationResult.Succeeded)
                        return false;

                    Dbg.Log($"TcpMsServer.Client ID={ReadableID}: sent encryption");

                }

                if (await Manual_ValidateConnection() != OperationResult.Succeeded)
                    return false;

                Dbg.Log($"TcpMsServer.Client ID={ReadableID}: validated client connection");

                return true;

            }

            internal async Task Manual_DispatchDisconnect() {

                try {
                    await DispatchPackageAsync(new Package(Package.PackageTypes.Disconnect));
                } finally { }

            }


            #endregion

        }

        private Client TryGetClient(byte[] id) {

            if (Clients.TryGetValue(id, out var client))
                return client;
            else
                throw new ArgumentException("Client with this ID was not found.", nameof(id));
        }

        /// <summary>true if the connected client count is less than <see cref="ServerSettings.MaxClients"/>; otherwise false</summary>
        private bool ClientCountOk() => Clients.Count < Settings.MaxClients;

        private async Task ListenerLoop() {

            if (!ClientCountOk())
                return;

            ListenerLoopCancel = new CancellationTokenSource();

            Dbg.Log("TcpMsServer: Started listener loop");

            while (ClientCountOk() && !ListenerLoopCancel.IsCancellationRequested) {

                TcpClient client;
                if (!Listener.Pending())
                    continue;

                client = await Listener.AcceptTcpClientAsync(); //accept connection
                Dbg.Log("TcpMsServer: Accepted client connection");

                if (client != null)
                    _ = Task.Run(() => ConnectionHandler(client)); //handle connection
            }

            Dbg.Log("TcpMsServer: Exited listener loop");

        }

        private async Task ConnectionHandler(TcpClient tcpClient) {

            Client client = new Client(GenerateID(), tcpClient, this);

            Dbg.Log($"TcpMsServer: Started connection handler with ID {client.ReadableID}");

            if (!await client.Manual_JoinClient()) {

                Dbg.Log($"TcpMsServer ID={client.ReadableID}: failed to join client");

                client.Close();
                return;
            }

            Dbg.Log($"TcpMsServer ID={client.ReadableID}: client joined");

            Clients.TryAdd(client.ID, client);
            OnClientConnected(client.ID);

            client.StartAll();

            Dbg.Log($"TcpMsServer ID={client.ReadableID}: started obtain/dispatch loops");

        }

        /// <summary>Tries to remove <paramref name="client"/> from <see cref="Clients"/>. Calls <see cref="OnClientDisconnected(byte[])"/> if necessary.</summary>
        private void RemoveClient(Client client) {
            if (Clients.TryRemove(client.ID, out _)) {
                OnClientDisconnected(client.ID);
                Dbg.Log($"TcpMsServer: Removed client with ID {client.ReadableID}");
            }
        }

        private byte[] GenerateID(int length = 16) {

            byte[] id = Rdm.GetBytes(length);

            while (Clients.Any(k => k.Key == id))
                id = Rdm.GetBytes(length);

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
}