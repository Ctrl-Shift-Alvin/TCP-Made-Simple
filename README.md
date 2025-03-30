# TCP Made Simple (TcpMs) for Unity

*This branch is ideally functionally identical to master, but can vary in implementation minimally, since it uses .NET standard 2.1. I created it to be used in Unity projects. It's also probably a bit slower.*

I made this library, since I couldn't seem to find one that suited my needs.
These are the key points:
- Focus on simplicity
- Event-based (subscribe to events, such as to handle received data)
- Send data in string or byte[] format
- Very multithreaded (separate threads for sending, receiving, etc.)
- Optional (but recommended) encryption with [AS-Encryption-Tools](https://github.com/Ctrl-Shift-Alvin/AS-Encryption-Tools)
- Optional password-based authentication (only enabled together with encryption)
- Basic connection tests
- Basic error handling
- Ping functionality
- Basic version control

## Server Settings
You should first decide on which server settings to use, or simply use the default.
These are all fields you can configure, and their defaults:
```
public const int CurrentVersion = 0;
public int Version { get; internal set; } = CurrentVersion;
public byte ConnectionTestTries { get; set; } = 3;
public bool EncryptionEnabled { get; set; } = true;
public SecurePassword Password { get; set; } = new SecurePassword(password ?? string.Empty);
public int MaxClients { get; set; } = 15;
public int MaxPanicsPerClient { get; set; } = 5;
public int PingIntervalMs { get; set; } = 10000;
public int PingTimeoutMs { get; set; } = 8000;
public int ReceiveTimeoutMs { get; set; } = 500;
public bool UseErrorHandling { get; set; } = true;
```
Just create a new ```ServerSettings``` instance and use it for *both* the server and client (settings are generally not validated, and could cause problems if different between server and client):
```
ServerSettings settings = new ServerSettings(); //default, without password, therefore without encryption
ServerSettings settings = new ServerSettings("myPassword"); //default, with password and encryption
```

## Creating a Server
Create a new ```TcpMsServer``` instance, then start it:

```
TcpMsServer myServer = new TcpMsServer(System.Net.IPAddress.Any, 1010, settings); //create the server on port 1010, and listen to all IPs
await TestServer.StartAsync();
```

## Connecting to a server
Create a new ```TcpMsClient``` instance, then connect to a server:

```
TcpMsClient myClient = new TcpMsClient("127.0.0.1", 1010); //connect to localhost on port 1010
await TestClient.TryConnectAsync("myPassword"); //returns true if (optionally) the password are correct and the connection succeeds
```

When a client connects to a server, it is assigned a unique client ID. By default, the client does not receive this ID.

## Authentication
The authentication process consists of a set of challenges between the server and client. Both the server and client create a new AES-512 instance and use the password in their ServerSettings instance to derive a key (the IV and salt are randomly generated each time). These instances are always unique.

Firstly, the server sends over its new IV and salt to the client. The client then uses its derived key, in combination with the received IV and salt, to create an AES-512 instance. The server then sends a set of challenges to the client, after which the client attempts to decrypt each challenge, and sends back the hash of the decrypted challenge. The server then compares the received hashes to the hashes of its challenge set.

If the server approves all challenges, the process happens again, but the other way. This avoids man-in-the-middle attacks, by verifying the identity of both the server and client. It all boils down to both sides having the same password (AKA same AES key). If this is not the case, the challenge will be incorrectly decrypted, and an incorrect hash will be sent back. Furthermore, if the server approves the challenges without having the correct password, it will also fail the challenges, and the client will know the server is compromised and disconnect.

## Data Encryption
After the authentication process is completed, the server will send over its actual IV and salt of the AES-512 instance that is used to encrypt and decrypt the data traffic. The client then creates the AES instance it will use to encrypt and decrypt all data, using the received IV, salt, and the derived key from the password. From this point forward, outgoing data is encrypted, sent, received, and decrypted using an identical AES instance on both sides.

## Handling Events
Both the server and client expose certain events that fire in different situations.

### Client
```ConnectedEvent```: Fires when the client successfully connected (and authenticated).
```DisconnectedEvent```: Fires when the client disconnected by any means (unless the client was forcefully closed).
```PanicEvent```: Fires when an internal error occured and was resolved. Possibly use to update the server to the current state, in case data was lost.

### Server
```ClientConnectedEvent(byte[] clientId)```: Fires when a client successfully connected (and authenticated)
```ClientDisconnectedEvent(byte[] clientId)```: Fires when a client disconnected by any means
```ClientPanicEvent(byte[] clientId)```: Fires when an internal error occured and was resolved. Possibly use to update the server to the current state, in case data was lost.

## Sending Data
Sending data is pretty straightforward, but slightly different on the server and client.

### Client
A client can send data in two ways:

```SendByte(byte data)```, ```SendString(string data)``` and ```SendBlob(byte[] data)``` queue the data to be sent, without a way to ensure if and when it was sent.

```SendByteAsync(byte data)```, ```SendStringAsync(string data)``` and ```SendBlobAsync(byte[] data)``` queue the data to be sent, and return a Task that completes when the data was successfully *sent*.

### Server
A server can *broadcast* data to all clients, or send data to a specific client. Similarly to the client class, the server has the same methods, but with different overloads:

```SendByte(byte[] clientId, byte data)```, ```SendString(byte[] clientId, string data)``` and ```SendBlob(byte[] clientId, byte[] data)``` queue data to be sent the client with the corresponding client ID. (+ their async equivalents, that await the sending of the data)

```BroadcastByte(byte data)```, ```BroadcastString(string data)``` and ```BroadcastBlob(byte[] data)``` queue data to be sent to all clients. (+ their async equivalents, that await the sending of the data to each client)


## Receiving Data
A server and client both receive and handle data using events.

### Client
A client exposes the following events that fire depending on which method the server used to send the data: ```StringReceivedEvent(string data)``` and ```BlobReceivedEvent(byte[] data)```. 

```SendByte``` and ```SendBlob``` both trigger ```BlobReceivedEvent``` on the receiving end, while ```SendString``` triggers ```StringReceivedEvent```.

### Server
A server exposes the same events, which function *identically*, but with an extra parameter containing the sender's unique client ID:
```StringReceivedEvent(byte[] clientId, string data)``` and ```BlobReceivedEvent(byte[] clientId, byte[] data)```

## Other properties and methods
There are a few other exposed properties and methods that I'm too lazy to write. Though, I did make sure to name them conveniently.