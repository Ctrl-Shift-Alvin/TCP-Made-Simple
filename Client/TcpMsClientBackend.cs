using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AlvinSoft.Cryptography;

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
                case Package.PackageTypes.Settings: {

                }
                break;
                case Package.PackageTypes.Test: {

                }
                break;
                case Package.PackageTypes.Ping: {

                }
                break;


            }

        }

        protected override void OnReceivedDataPackage(Package package) {

            byte[] data = package.Data;
            TcpMsClientInstance.DecryptIfNeccessary(ref data);

            object value;

            switch (package.DataType) {

                case Package.DataTypes.Bool: {

                    value = BitConverter.ToBoolean(data);

                }
                break;

                case Package.DataTypes.Byte: {

                    value = data[0];

                }
                break;

                case Package.DataTypes.Short: {

                    value = BinaryPrimitives.ReadInt16BigEndian(data);

                }
                break;

                case Package.DataTypes.Int: {

                    value = BinaryPrimitives.ReadInt32BigEndian(data);

                }
                break;

                case Package.DataTypes.Long: {

                    value = BinaryPrimitives.ReadInt64BigEndian(data);

                }
                break;

                case Package.DataTypes.String: {

                    value = Encoding.Unicode.GetString(data);

                }
                break;

                case Package.DataTypes.Blob: {

                    TcpMsClientInstance.OnDataReceived(data, Package.DataTypes.Blob);

                }
                return;

                default: {
                    value = null;
                }
                break;
            }

            TcpMsClientInstance.OnDataReceived(value, package.DataType);
        }

        protected override async Task<bool> OnError(Errors error) {

            switch (error) {

                case Errors.ReadTimeout: {

                }
                break;

                case Errors.CannotWrite: {

                }
                return false;

                case Errors.CannotRead: {

                }
                return false;

                case Errors.ErrorPackage: {

                }
                break;

                case Errors.UnexpectedPackage: {

                }
                break;

            }

        }

        public override async Task StopAsync(bool awaitTasks = true) {
            TcpClientInstance.Close();
            await base.StopAsync(awaitTasks);
        }
        #endregion

        #region Manual

        /// <summary>Tries to authenticate the client and sends an error package if something fails</summary>
        /// <returns><see langword="true"/> if the authentication was successful; otherwise <see langword="false"/>.</returns>
        public async Task<bool> Manual_Authenticate() {

            if (Settings.Password.IsEmpty)
                return false;

            try {

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

                RSAEncryption rsa = new();

                //send public key that the server uses to encrypt encryption data
                byte[] rsaPublicKey = rsa.Key.ExportPublicKey();
                await DispatchPackageAsync(new Package(Package.PackageTypes.EncrRSAPublicKey, Package.DataTypes.Blob, rsaPublicKey, false));

                Package iv = await ObtainExpectedPackageAsync(Package.PackageTypes.EncrIV);
                Package salt = await ObtainExpectedPackageAsync(Package.PackageTypes.EncrSalt);

                TcpMsClientInstance.Encryption = new(Settings.Password, rsa.DecryptBytes(salt.Data), rsa.DecryptBytes(iv.Data));

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
                    packageBuffer = await ObtainExpectedPackageAsync(Package.PackageTypes.TestTrySuccess);

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

            try {

                Send(new Package(Package.PackageTypes.Panic));

                //receive settings
                Package settingsPackage = await Client.ReceivePackageAsync(Package.PackageTypes.Settings, cancellationToken: TimeoutToken); //if max panics is reached, error package is sent, so this throws an exception
                Settings.Update(settingsPackage.Data);

                await Manual_Authenticate();
                await ReceiveEncryption();



                if (!await ValidateConnection())

                    OnPanic();

            } catch {
                HandleDisconnect();
            }
        }

        public async Task Manual_DispatchError() {
            try {
                await DispatchPackageAsync(Package.Error);
            } catch (InvalidOperationException) { }
        }

        #endregion


    }

    #region Handlers
    /// <summary>Closes a connected client and calls <c>OnDisconnect</c></summary>
    private void HandleDisconnect() {
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