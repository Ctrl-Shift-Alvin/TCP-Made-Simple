using System;
using System.Buffers.Binary;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AlvinSoft.Cryptography;

namespace AlvinSoft.TcpMs;

//"Backend"
partial class TcpMsClient {

    /// <summary>The hostname used to connect to the server.</summary>
    public string Hostname => hostname;
    /// <summary>The port used to connect to the server.</summary>
    public ushort Port => port;
    internal Client Client { get; set; }

    /// <summary>The server's settings used to communicate. Do not change this manually, the client handles this automatically.</summary>
    public ServerSettings Settings = ServerSettings.None;
    /// <summary>The encryption instance used to encrypt/decrypt data for/from the server. null if encryption is not used.</summary>
    public AesEncryption Encryption { get; protected set; } = null;

    /// <summary>Shorthand for <c>this.Client.IsConnected</c></summary>
    public bool IsConnected => Client.IsConnected;

    /// <summary>A token used for timeouts that is cancelled after 2 seconds.</summary>
    protected static CancellationToken TimeoutToken => new CancellationTokenSource(2000).Token;

    private void EnsureIsConnected() {
        ArgumentNullException.ThrowIfNull(Client, nameof(Client));
        ArgumentNullException.ThrowIfNull(Client.Tcp, nameof(Client.Tcp));
        if (!Client.IsConnected)
            throw new ArgumentException("Client is not connected");
    }

    /// <summary>Connects to the server and authenticates if neccessary. If this succeeds, the listener loop starts.</summary>
    /// <returns>true if the connection and authentication succeeded; otherwise false.</returns>
    private async Task<bool> ConnectionHandler() {

        EnsureIsConnected();

        try {


            Package settingsPackage = await Client.ReceivePackageAsync(Package.PackageTypes.NewSettings, TimeoutToken);
            Settings.Update(settingsPackage.Data);

            if (Settings.EncryptionEnabled) {

                if (!await Authenticate()) {
                    await DisconnectAsync();
                    return false;
                }
                await ReceiveEncryption();

            }

            if (await ValidateConnection()) {
                _ = Task.Run(ListenerLoop);
                OnConnected();
                return true;
            } else {
                Close();
                return false;
            }


        } catch {

            Close();
            return false;

        }


    }

    private async Task ListenerLoop() {

        EnsureIsConnected();

        try {
            Package buffer;
            while (IsConnected) {

                buffer = await Client.ReceivePackageAsync(cancellationToken: Client.CancelTokenSource.Token);

                switch (buffer.PackageType) {

                    case Package.PackageTypes.Disconnect: {
                        HandleDisconnect();
                    } return;

                    case Package.PackageTypes.NewSettings: {
                        HandleNewSettings(buffer);
                    } break;

                    case Package.PackageTypes.Test: {
                        if (!await ValidateConnection(buffer))
                            await SendPanic();
                    } break;

                    case Package.PackageTypes.Ping: {
                        await HandlePing();
                    } break;

                    case Package.PackageTypes.Panic: {
                        await HandlePanic();
                    } break;

                    case Package.PackageTypes.Data: {
                        HandleData(buffer);
                    } break;

                    default: {
                        await SendPanic();
                    } break;
                
                }

            }
        } catch {

            //if the client is still connected but something went wrong, sent a panic request
            if (IsConnected) {

                await SendPanic(); //calls HandleDisconnect() if not successful

                //if panic worked, restart the listener
                if (IsConnected)
                    _ = Task.Run(ListenerLoop);

            } else {
                HandleDisconnect();
            }

        }

    }

    /// <summary>Tries to authenticate the client and sends an error package if something fails</summary>
    /// <returns>true if the authentication was successful; otherwise false.</returns>
    private async Task<bool> Authenticate() {

        EnsureIsConnected();

        Package buffer;
        try {
            if (Settings.Password == null)
                return false;

            //read public rsa key
            buffer = await Client.ReceivePackageAsync(Package.PackageTypes.AuthRSAPublicKey, TimeoutToken);

            using RsaEncryption rsa = new(new RsaEncryption.RsaPublicKey(buffer.Data));

            //encrypt and send password using public rsa key
            byte[] encryptedPassword = rsa.EncryptString(Settings.Password);
            await Client.SendPackageAsync(new Package(Package.PackageTypes.AuthEncryptedPassword, Package.DataTypes.Blob, encryptedPassword, false));

            //read result
            buffer = await Client.ReceivePackageAsync(cancellationToken: TimeoutToken);

        } catch {
            await SendError();
            return false;
        }
        return buffer.PackageType == Package.PackageTypes.AuthSuccess;

    }

    /// <summary>Reads the server's encryption and sends an error package if something goes wrong</summary>
    private async Task ReceiveEncryption() {

        EnsureIsConnected();

        try {
            using RsaEncryption rsa = new();

            //send public key that the server uses to encrypt encryption data
            byte[] rsaPublicKey = rsa.GetPublicKey().GetBytesPackage();
            await Client.SendPackageAsync(new Package(Package.PackageTypes.EncrRSAPublicKey, Package.DataTypes.Blob, rsaPublicKey, false));

            Package iv = await Client.ReceivePackageAsync(Package.PackageTypes.EncrIV, TimeoutToken);
            Package salt = await Client.ReceivePackageAsync(Package.PackageTypes.EncrSalt, TimeoutToken);

            Encryption = new(Settings.Password, salt.Data, iv.Data);
        } catch {
            await SendError();
        }
    }

    /// <summary>Reads a validation package and validates the package sent by the server. The validation happens <see cref="ServerSettings.ConnectionTestTries"/> times.</summary>
    /// <param name="firstPackage">null to validate normally; assign if the first validation package was already read</param>
    /// <returns>true if all validations succeeded; otherwise false.</returns>
    private async Task<bool> ValidateConnection(Package? firstPackage = null) {

        EnsureIsConnected();

        try {

            Package packageBuffer;

            //if a package is already provided, do the first test without "reading it again" (you would be reading the next package)
            if (firstPackage.HasValue) {

                packageBuffer = firstPackage.Value;

                byte[] data;
                if (Settings.EncryptionEnabled)
                    data = Encryption.DecryptBytes(packageBuffer.Data);
                else
                    data = packageBuffer.Data;

                byte[] response = RandomGenerator.GenerateBytes(data.Length);
                response[RandomGenerator.Next(0, response.Length)] = data[RandomGenerator.Next(0, data.Length)]; //assign random byte from data to random byte in response

                if (Settings.EncryptionEnabled)
                    data = Encryption.EncryptBytes(response);
                else
                    data = response;

                //send response
                await Client.SendPackageAsync(new Package(Package.PackageTypes.Test, Package.DataTypes.Blob, response));

                //read result
                packageBuffer = await Client.Stream.ReceivePackage(cancellationToken: TimeoutToken);
                if (packageBuffer.PackageType == Package.PackageTypes.Error)
                    return false;
            }

            //if the first run was already performed, set i = 1
            for (int i = firstPackage.HasValue ? 1 : 0; i < Settings.ConnectionTestTries; i++) {

                packageBuffer = await Client.Stream.ReceivePackage(Package.PackageTypes.Test, TimeoutToken);

                byte[] data;
                if (Settings.EncryptionEnabled)
                    data = Encryption.DecryptBytes(packageBuffer.Data);
                else
                    data = packageBuffer.Data;

                byte[] response = RandomGenerator.GenerateBytes(data.Length);
                response[RandomGenerator.Next(0, response.Length)] = data[RandomGenerator.Next(0, data.Length)]; //assign random byte from data to random byte in response

                if (Settings.EncryptionEnabled)
                    data = Encryption.EncryptBytes(response);
                else
                    data = response;

                //send response
                await Client.Stream.SendPackage(Package.DataTypes.Blob, Package.PackageTypes.Test, response, false);
                
                //read result
                packageBuffer = await Client.Stream.ReceivePackage(cancellationToken: TimeoutToken);
                if (packageBuffer.PackageType == Package.PackageTypes.Error)
                    return false;

            }

            return true;
        } catch {
            return false;
        }

    }

    /// <summary>Sends a panic request and calls <see cref="HandlePanic"/>. Disconnects the client if unsuccessful.</summary>
    /// <returns>A task that finishes when the panic is over</returns>
    private async Task SendPanic() {

        EnsureIsConnected();

        try {
            //send panic
            await Client.Stream.SendPackage(Package.PackageTypes.Panic);

            //await response
            _ = await Client.Stream.ReceivePackage(Package.PackageTypes.Panic, TimeoutToken);

            await HandlePanic();

        } catch {

            //panic didn't work, something's broken, fuck it
            HandleDisconnect();

        }
    }

    /// <summary>Sends an error package</summary>
    private async Task SendError() {
        EnsureIsConnected();
        await Client.Stream.SendPackage(Package.PackageTypes.Error);
    }

    #region Handlers
    /// <summary>Closes a connected client and calls <c>OnDisconnect</c></summary>
    private void HandleDisconnect() {
        Close();
        OnDisconnect();
    }

    /// <summary>Sends back a pong package</summary>
    private async Task HandlePing() {
        await Client.Stream.SendPackage(Package.PackageTypes.Pong);
    }

    /// <summary>Updates the server settings using <paramref name="data"/></summary>
    private void HandleNewSettings(Package data) {
        Settings.Update(data.Data);
    }

    /// <summary>Handles a panic request</summary>
    /// <remarks>Keeps calling panic (updates settings, authenticates and rereads the encryption, then validates) until the validation is successful. When the server disconnects the client, the client is closed and <c>OnDisconnect</c> is called.</remarks>
    /// <returns>A task that finishes when the panic request is over. Check <see cref="IsConnected"/> to see if panic was successful.</returns>
    private async Task HandlePanic() {

        try {

            start: {

                EnsureIsConnected();

                Client.CallPanic();

                Client.Stream.ClearBuffer();

                //receive settings
                Package settingsPackage = await Client.Stream.ReceivePackage(Package.PackageTypes.NewSettings, TimeoutToken); //if max panics is reached, error package is sent, so this throws an exception
                Settings.Update(settingsPackage.Data);

                await Authenticate();
                await ReceiveEncryption();

            }

            if (!await ValidateConnection())
                goto start;

            OnPanic();

        } catch {
            HandleDisconnect();
        }
    }

    /// <summary>Handles data packages and invokes the relevant event</summary>
    private void HandleData(Package package) {

        void DecryptIfNeccessary(ref byte[] buffer) {
            if (Settings.EncryptionEnabled)
                buffer = Encryption.DecryptBytes(buffer);
        }

        switch (package.DataType) {

            case Package.DataTypes.Bool: {

                byte[] data = package.Data;
                DecryptIfNeccessary(ref data);

                bool value = BitConverter.ToBoolean(data);
                OnBoolReceived(value);

            }
            return;

            case Package.DataTypes.Int: {

                byte[] data = package.Data;
                DecryptIfNeccessary(ref data);

                int value = BinaryPrimitives.ReadInt32BigEndian(data);
                OnIntReceived(value);

            }
            return;

            case Package.DataTypes.String: {

                byte[] data = package.Data;
                DecryptIfNeccessary(ref data);

                string value = Encoding.Unicode.GetString(data);
                OnStringReceived(value);

            }
            return;

            case Package.DataTypes.Blob: {

                byte[] data = package.Data;
                DecryptIfNeccessary(ref data);

                OnBlobReceived(data);

            }
            return;
        
        }

    }

    #endregion


}