using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AlvinSoft.Cryptography;
using AlvinSoft.TcpMs.Packages;

namespace AlvinSoft.TcpMs;

//"Backend"
partial class TcpMsClient {

    #region Fields
    /// <summary>The hostname used to connect to the server.</summary>
    public string Hostname => hostname;
    /// <summary>The port used to connect to the server.</summary>
    public ushort Port => port;
    internal Client ClientInstance { get; set; }

    /// <summary>The server's settings used to communicate.</summary>
    public ServerSettings Settings { get; internal set; } = ServerSettings.None;
    /// <summary>The encryption instance used to encrypt/decrypt data for/from the server. null if encryption is not used.</summary>
    public AesEncryption Encryption { get; protected set; } = null;

    /// <summary>A token used for timeouts that is cancelled after 2 seconds.</summary>
    protected static CancellationToken TimeoutToken => new CancellationTokenSource(
#if DEBUG
        20000
#else
        2000
#endif
        ).Token;

    #endregion

    internal class Client(TcpMsClient tcpMsClient, TcpClient tcpClient) : PackageHandler(tcpClient.GetStream()) {

        private TcpMsClient TcpMsClientInstance { get; } = tcpMsClient;
        private TcpClient TcpClientInstance { get; } = tcpClient;

        private ServerSettings Settings => TcpMsClientInstance.Settings;

        private AesEncryption Encryption => TcpMsClientInstance.Encryption;

        #region Overrides
        protected override CancellationToken TimeoutToken => new CancellationTokenSource(TcpMsClientInstance.Settings.ReceiveTimeoutMs).Token;

        protected override async Task OnReceivedInternalPackage(Package package) {

            switch (package.PackageType) {

                case Package.PackageTypes.Disconnect: {
                    TcpMsClientInstance.HandleDisconnect();
                }
                break;

                case Package.PackageTypes.Test: {

                    await PauseAllAsync();

                    var validationResult = await Manual_ValidateConnection();
                    if (validationResult != OperationResult.Succeeded) {

                        _ = OnError(validationResult == OperationResult.Disconnected ? Errors.Disconnected : Errors.UnexpectedPackage);

                    }

                    ResumeAll();

                }
                break;

                case Package.PackageTypes.Ping: {
                    await PauseDispatchAsync();
                    await DispatchPackageAsync(new Package(Package.PackageTypes.Pong));
                    ResumeDispatch();
                }
                break;

                

            }

        }

        protected override void OnReceivedDataPackage(Package package) {

            Debug.WriteLine("TcpMsClient: received data package");

            byte[] data = package.Data;
            TcpMsClientInstance.DecryptIfNeccessary(ref data);

            switch (package.DataType) {


                case Package.DataTypes.Byte: {

                    TcpMsClientInstance.OnBlobReceived(data);

                }
                break;

                case Package.DataTypes.String: {

                    TcpMsClientInstance.OnStringReceived(Encoding.Unicode.GetString(data));

                }
                break;

                case Package.DataTypes.Blob: {

                    TcpMsClientInstance.OnBlobReceived(data);

                }
                return;

                default: {
                    _ = OnError(Errors.UnexpectedPackage);
                }
                return;

            }

        }

        protected override async Task OnError(Errors error) {

            switch (error) {

                case Errors.ReadTimeout: {

                    await PauseAllAsync();
                    if (await Manual_HandlePanic() == OperationResult.Succeeded) {
                        ResumeAll();
                    } else {
                        TcpMsClientInstance.HandleDisconnect();
                    }

                }
                return;

                case Errors.CannotWrite: {

                    TcpMsClientInstance.HandleDisconnect();

                }
                return;

                case Errors.CannotRead: {

                    TcpMsClientInstance.HandleDisconnect();

                }
                return;

                case Errors.ErrorPackage: {

                    await PauseAllAsync();
                    if (await Manual_HandlePanic() == OperationResult.Succeeded) {
                        ResumeAll();
                    } else {
                        TcpMsClientInstance.HandleDisconnect();
                    }


                }
                return;

                case Errors.UnexpectedPackage: {

                    await PauseAllAsync();
                    if (await Manual_HandlePanic() == OperationResult.Succeeded) {
                        ResumeAll();
                    } else {
                        TcpMsClientInstance.HandleDisconnect();
                    }

                }
                return;

            }

            TcpMsClientInstance.HandleDisconnect();

        }
        #endregion

        #region Manual

        /// <summary>Tries to authenticate the client and sends an error package if something fails</summary>
        /// <returns><see langword="true"/> if the authentication was successful; otherwise <see langword="false"/>.</returns>
        public async Task<bool> Manual_Authenticate() {

            try {

                //receive info package, if data[0] is 255, then we don't need to authenticate
                Package infoPackage = await ObtainExpectedPackageAsync(Package.PackageTypes.Auth_Info);
                if (infoPackage.Data[0] == byte.MaxValue) {
                    Settings.EncryptionEnabled = false;
                    return true;
                } else {
                    Settings.EncryptionEnabled = true;
                }

                if (Settings.Password.IsEmpty)
                    return false;

                AesEncryption encryptionIn;
                Package saltIn = await ObtainExpectedPackageAsync(Package.PackageTypes.Auth_Salt);
                Package ivIn = await ObtainExpectedPackageAsync(Package.PackageTypes.Auth_IV);
                Package encryptedChallengeIn = await ObtainExpectedPackageAsync(Package.PackageTypes.Auth_Challenge);

                encryptionIn = new(Settings.Password, saltIn.Data, ivIn.Data);
                byte[] challengeIn = encryptionIn.DecryptBytes(encryptedChallengeIn.Data);
                byte[] challengeInHash = SHA512.HashData(challengeIn);

                await DispatchPackageAsync(new Package(Package.PackageTypes.Auth_Response, Package.DataTypes.Blob, challengeInHash));

                Package response = await ObtainPackageAsync();
                if (response.PackageType != Package.PackageTypes.Auth_Success)
                    return false;

                AesEncryption encryptionOut = new() {
                    Password = Settings.Password
                };
                byte[] challengeOut = RandomGen.GetBytes(32);
                byte[] encryptedChallengeOut = encryptionOut.EncryptBytes(challengeOut);
                byte[] challengeOutHash = SHA512.HashData(challengeOut);

                await DispatchPackageAsync(new Package(Package.PackageTypes.Auth_Salt, Package.DataTypes.Blob, encryptionOut.Salt));
                await DispatchPackageAsync(new Package(Package.PackageTypes.Auth_IV, Package.DataTypes.Blob, encryptionOut.IV));
                await DispatchPackageAsync(new Package(Package.PackageTypes.Auth_Challenge, Package.DataTypes.Blob, encryptedChallengeOut));

                response = await ObtainExpectedPackageAsync(Package.PackageTypes.Auth_Response);

                if (Enumerable.SequenceEqual(challengeOutHash, response.Data)) {
                    await DispatchPackageAsync(new Package(Package.PackageTypes.Auth_Success));
                    return true;
                } else {
                    await DispatchPackageAsync(new Package(Package.PackageTypes.Auth_Failure));
                    return false;
                }

            } catch (TcpMsErrorPackageException) {

                return false;

            } catch (TcpMsUnexpectedPackageException) {

                return false;

            } catch (TcpMsTimeoutException) {

                return false;

            } catch (InvalidOperationException) {

                return false;
            }

        }

        public async Task<OperationResult> Manual_ReceiveEncryption() {

            try {

                Package iv = await ObtainExpectedPackageAsync(Package.PackageTypes.EncrIV);
                Package salt = await ObtainExpectedPackageAsync(Package.PackageTypes.EncrSalt);

                TcpMsClientInstance.Encryption = new(Settings.Password, salt.Data, iv.Data);

                return OperationResult.Succeeded;

            } catch (InvalidOperationException) {

                return OperationResult.Disconnected;

            } catch (TcpMsTimeoutException) {

                return OperationResult.Error;

            } catch (TcpMsErrorPackageException) {

                return OperationResult.Failed;

            } catch (TcpMsUnexpectedPackageException) {

                return OperationResult.Failed;

            }
        }

        public async Task<OperationResult> Manual_ValidateConnection() {

            try {

                Package packageBuffer;

                for (int i = 0; i < Settings.ConnectionTestTries; i++) {

                    //read test package
                    packageBuffer = await ObtainExpectedPackageAsync(Package.PackageTypes.Test);

                    //decrypt if necessary
                    byte[] data;
                    if (Settings.EncryptionEnabled)
                        data = Encryption.DecryptBytes(packageBuffer.Data);
                    else
                        data = packageBuffer.Data;

                    //create response (random bytes with length of test package)
                    byte[] response = RandomGen.GetBytes(data.Length);
                    response[Random.Shared.Next(0, response.Length)] = data[Random.Shared.Next(0, data.Length)]; //assign random byte from data to random byte in response

                    //encrypt if necessary
                    if (Settings.EncryptionEnabled)
                        data = Encryption.EncryptBytes(response);
                    else
                        data = response;

                    //send response
                    await DispatchPackageAsync(new Package(Package.PackageTypes.Test, Package.DataTypes.Blob, data, false));

                    //read result
                    packageBuffer = await ObtainExpectedPackageAsync([Package.PackageTypes.TestTrySuccess, Package.PackageTypes.TestTryFailure]);

                    if (packageBuffer.PackageType == Package.PackageTypes.TestTryFailure)
                        return OperationResult.Failed;

                } 

                return OperationResult.Succeeded;

            } catch (InvalidOperationException) {

                return OperationResult.Disconnected;

            } catch (TcpMsTimeoutException) {

                return OperationResult.Error;

            } catch (TcpMsErrorPackageException) {

                return OperationResult.Failed;

            } catch (TcpMsUnexpectedPackageException) {

                return OperationResult.Failed;

            }

        }

        public async Task<OperationResult> Manual_HandlePanic() {

            //receive settings
            if (!await Manual_JoinClient()) {

                await Manual_DispatchDisconnect();
                return OperationResult.Disconnected;

            }

            return OperationResult.Succeeded;
        }

        public async Task<bool> Manual_JoinClient() {

            if (await Manual_Authenticate() == false)
                return false;

            Debug.WriteLine($"TcpMsClient.Client: authenticated");

            if (Settings.EncryptionEnabled) {

                if (await Manual_ReceiveEncryption() != OperationResult.Succeeded)
                    return false;

                Debug.WriteLine($"TcpMsClient.Client: received encryption");
            }

            _ = await ObtainExpectedPackageAsync(Package.PackageTypes.TestRequest);
            if (await Manual_ValidateConnection() != OperationResult.Succeeded)
                return false;

            Debug.WriteLine($"TcpMsClient.Client: validated connection");

            return true;

        }


        public async Task Manual_DispatchError() {
            try {
                await DispatchPackageAsync(Package.Error);
            } catch (InvalidOperationException) { }
        }

        public async Task Manual_DispatchDisconnect() {
            try {
                await DispatchPackageAsync(new Package(Package.PackageTypes.DisconnectRequest));
            } finally { }
        }

        #endregion


    }

    #region Handlers
    /// <summary>Closes a connected client and calls <c>OnDisconnect</c></summary>
    private async void HandleDisconnect() {
        await ClientInstance.StopAllAsync();
        Close();
        OnDisconnect();
    }

    /// <summary>Updates the server settings using <paramref name="data"/>.</summary>
    private void HandleNewSettings(Package data) {
        Settings.Update(data.Data);
    }

    private void EncryptIfNeccessary(ref byte[] buffer) {
        if (Settings.EncryptionEnabled)
            buffer = Encryption.EncryptBytes(buffer);
    }

    private void DecryptIfNeccessary(ref byte[] buffer) {
        if (Settings.EncryptionEnabled)
            buffer = Encryption.DecryptBytes(buffer);
    }

    #endregion


}