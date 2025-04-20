using AlvinSoft.TcpMs;
using AlvinSoft.TcpMs.Packages;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net;

namespace AlvinSoft.TcpMsTest;

[TestClass]
public class AlvinSoftTcpTests {

#pragma warning disable CS8618
    private static TcpMsServer TestServer;
    private static TcpMsClient TestClient;
#pragma warning restore CS8618

    [TestMethod("Server/Client Test No Encryption No Ping")]
    public async Task Test1() {

        ServerSettings settings = new(null) {
            EncryptionEnabled = false,
            PingIntervalMs = 0
        };

        TestServer = new(IPAddress.Any, 19910, settings);
        await TestServer.StartAsync();

        TestClient = new("127.0.0.1", 19910);
        Assert.IsTrue(await TestClient.TryConnectAsync());

        await TestSendReceive();
        await TestSendReceive();
        await TestSendReceive();
        await TestSendReceive();

    }

    [TestMethod("Server/Client Test Encryption")]
    public async Task Test2() {

        ServerSettings settings = new("password");

        TestServer = new(IPAddress.Any, 19911, settings);
        await TestServer.StartAsync();

        TestClient = new("127.0.0.1", 19911);
        Assert.IsTrue(await TestClient.TryConnectAsync("password"));

        await TestSendReceive();
        await TestSendReceive();
        await TestSendReceive();
        await TestSendReceive();

    }

    [TestMethod("Client Disconnect Test")]
    public async Task Test3() {

        ServerSettings settings = new(null) {
            EncryptionEnabled = false,
            PingIntervalMs = 0
        };

        TestServer = new(IPAddress.Any, 19912, settings);
        await TestServer.StartAsync();

        TestClient = new("127.0.0.1", 19912);
        Assert.IsTrue(await TestClient.TryConnectAsync());

        TaskCompletionSource completion = new();
        void OnDisconnect(byte[] _) {
            completion.SetResult();
        }

        TestServer.ClientDisconnectedEvent += OnDisconnect;

        await TestClient.DisconnectAsync();
        await completion.Task;

        TestServer.ClientDisconnectedEvent -= OnDisconnect;
        Assert.IsTrue(TestServer.ClientCount == 0);

    }

    [TestMethod("General Test")]
    public async Task Test4() {

        string pass = "password";
        ServerSettings settings = new(pass) {
            PingIntervalMs = 1000,
            PingTimeoutMs = 500
        };

        bool serverClientConnected = false;
        bool serverClientDisconnected = false;
        bool clientConnected = false;
        bool clientDisconnected = false;
        void AllFalse() => Assert.IsTrue(!serverClientConnected && !serverClientDisconnected && !clientConnected && !clientDisconnected);

        TaskCompletionSource completion = new(), completion1 = new();
        TestServer = new(IPAddress.Any, 19913, settings);
        TestServer.ClientConnectedEvent += (byte[] _) => {
            Assert.IsTrue(TestServer.ClientCount == 1);
            serverClientConnected = true;
        };
        TestServer.ClientDisconnectedEvent += (byte[] _) => {
            Assert.IsTrue(TestServer.ClientCount == 0);
            serverClientDisconnected = true;
        };
        TestServer.ClientPanicEvent += (byte[] _) => {
            Assert.Fail("Panic");
        };

        TestClient = new("127.0.0.1", 19913);
        TestClient.ConnectedEvent += () => {
            Assert.IsTrue(TestClient.IsConnected);
            clientConnected = true;
        };
        TestClient.DisconnectedEvent += () => {
            Assert.IsFalse(TestClient.IsConnected);
            clientDisconnected = true;
        };
        TestClient.PanicEvent += () => {
            Assert.Fail("Panic");
        };

        await TestServer.StartAsync();

        //connect without password
        await TestClient.TryConnectAsync();

        Assert.IsFalse(TestClient.IsConnected);
        Assert.IsTrue(TestServer.ClientCount == 0);
        AllFalse();

        //connect with wrong password
        await TestClient.TryConnectAsync(pass + 's');

        Assert.IsFalse(TestClient.IsConnected);
        Assert.IsTrue(TestServer.ClientCount == 0);
        AllFalse();

        //connect with correct password
        await TestClient.TryConnectAsync(pass);

        Assert.IsTrue(serverClientConnected);
        Assert.IsTrue(clientConnected);
        clientConnected = false;
        serverClientConnected = false;
        AllFalse();

        //test data exchange
        await TestSendReceive();

        //test disconnect
        void del1(byte[] _) => completion.SetResult();
        TestServer.ClientDisconnectedEvent += del1;

        await TestClient.DisconnectAsync();
        await Task.WhenAny(Task.Delay(1000), completion.Task);

        TestServer.ClientDisconnectedEvent -= del1;

        Assert.IsTrue(clientDisconnected);
        Assert.IsTrue(serverClientDisconnected);
        clientDisconnected = false;
        serverClientDisconnected = false;
        AllFalse();

        //reconnect
        await TestClient.TryConnectAsync(pass);

        Assert.IsTrue(clientConnected);
        Assert.IsTrue(serverClientConnected);
        clientConnected = false;
        serverClientConnected = false;
        AllFalse();

        //disconnect from server
        completion = new();
        TestClient.DisconnectedEvent += completion.SetResult;

        await TestServer.DisconnectClient(TestServer.ClientIDs.First());
        await Task.WhenAny(Task.Delay(1000), completion.Task);

        TestClient.DisconnectedEvent -= completion.SetResult;

        await Task.Delay(100);
        Assert.IsTrue(clientDisconnected);
        Assert.IsTrue(serverClientDisconnected);
        clientDisconnected = false;
        serverClientDisconnected = false;
        AllFalse();

        //reconnect
        await TestClient.TryConnectAsync(pass);

        Assert.IsTrue(clientConnected);
        Assert.IsTrue(serverClientConnected);
        clientConnected = false;
        serverClientConnected = false;

        //test ping timeout
        completion = new();
        TestServer.ClientDisconnectedEvent += del1;

        TestClient.Close();
        await Task.WhenAny(Task.Delay(settings.PingIntervalMs + settings.PingTimeoutMs + 100), completion.Task); //wait for ping timeout

        TestServer.ClientDisconnectedEvent -= del1;

        Assert.IsTrue(clientDisconnected);
        Assert.IsTrue(serverClientDisconnected);
        clientDisconnected = false;
        serverClientDisconnected = false;
        AllFalse();


    }

    [TestMethod("Ping Functionality Test")]
    public async Task Test5() {

        ServerSettings settings = new("password") {
            PingIntervalMs = 1000,
            PingTimeoutMs = 500
        };

        TestServer = new(IPAddress.Any, 19912, settings);
        await TestServer.StartAsync();

        TestClient = new("127.0.0.1", 19912);
        Assert.IsTrue(await TestClient.TryConnectAsync("password"));

        Assert.IsTrue(TestServer.ClientCount == 1);

        TaskCompletionSource completion = new();
        void OnDisconnect(byte[] _) {
            completion.SetResult();
        }
        TestServer.ClientDisconnectedEvent += OnDisconnect;

        //forcefully close the client
        TestClient.Close();

        await Task.WhenAny(Task.Delay(settings.PingIntervalMs + settings.PingTimeoutMs), completion.Task);
        Assert.IsTrue(completion.Task.IsCompleted);

        TestServer.ClientDisconnectedEvent -= OnDisconnect;

    }

    [TestCleanup]
    public void Cleanup() {
        TestServer?.Close();
        TestClient?.Close();
        Dbg.OutputAll();
    }

    private static async Task TestSendReceive() {

        #region Test_Server_Send
        //blob test
        bool receivedData = false;
        byte[] sentData = RandomBytes(128);
        TaskCompletionSource completion = new();

        void clientReceiveBlobHandler(byte[] data) {
            receivedData = true;
            CollectionAssert.AreEqual(sentData, data);
            completion.SetResult();
        }
        TestClient.BlobReceivedEvent += clientReceiveBlobHandler;

        await TestServer.SendBlobAsync(TestServer.ClientIDs.First(), sentData);
        await completion.Task.WaitAsync(TimeSpan.FromMilliseconds(2000));
        Assert.IsTrue(receivedData);

        TestClient.BlobReceivedEvent -= clientReceiveBlobHandler;

        //string test
        receivedData = false;
        sentData = RandomBytes(128);
        string sentString = Convert.ToBase64String(sentData);
        completion = new();

        void clientReceiveStringHandler(string data) {
            receivedData = true;
            Assert.AreEqual(sentString, data);
            completion.SetResult();
        }
        TestClient.StringReceivedEvent += clientReceiveStringHandler;

        await TestServer.SendStringAsync(TestServer.ClientIDs.First(), sentString);
        await completion.Task.WaitAsync(TimeSpan.FromMilliseconds(2000));
        Assert.IsTrue(receivedData);

        TestClient.StringReceivedEvent -= clientReceiveStringHandler;

        #endregion

        #region Test_Server_Broadcast
        //blob test
        receivedData = false;
        sentData = RandomBytes(128);
        completion = new();

        TestClient.BlobReceivedEvent += clientReceiveBlobHandler;

        await TestServer.BroadcastBlobAsync(sentData);
        await completion.Task.WaitAsync(TimeSpan.FromMilliseconds(2000));
        Assert.IsTrue(receivedData);

        TestClient.BlobReceivedEvent -= clientReceiveBlobHandler;

        //string test
        receivedData = false;
        sentData = RandomBytes(128);
        sentString = Convert.ToBase64String(sentData);
        completion = new();

        TestClient.StringReceivedEvent += clientReceiveStringHandler;

        await TestServer.BroadcastStringAsync(sentString);
        await completion.Task.WaitAsync(TimeSpan.FromMilliseconds(2000));
        Assert.IsTrue(receivedData);

        TestClient.StringReceivedEvent -= clientReceiveStringHandler;

        #endregion

        #region Test_Client_Send
        //blob test
        receivedData = false;
        sentData = RandomBytes(128);
        completion = new();

        void serverReceiveHandler(byte[] _, byte[] data) {
            receivedData = true;
            CollectionAssert.AreEqual(sentData, data);
            completion.SetResult();
        }
        TestServer.BlobReceivedEvent += serverReceiveHandler;

        TestClient.SendBlob(sentData);
        await completion.Task.WaitAsync(TimeSpan.FromMilliseconds(2000));
        Assert.IsTrue(receivedData);

        TestServer.BlobReceivedEvent -= serverReceiveHandler;

        //string test
        receivedData = false;
        sentData = RandomBytes(128);
        sentString = Convert.ToBase64String(sentData);
        completion = new();

        void serverReceiveStringHandler(byte[] clientId, string data) {
            receivedData = true;
            Assert.AreEqual(sentString, data);
            completion.SetResult();
        }
        TestServer.StringReceivedEvent += serverReceiveStringHandler;

        await TestClient.SendStringAsync(sentString);
        await completion.Task.WaitAsync(TimeSpan.FromMilliseconds(2000));
        Assert.IsTrue(receivedData);

        TestServer.StringReceivedEvent -= serverReceiveStringHandler;

        #endregion


    }

    static byte[] RandomBytes(int length) {
        byte[] buffer = new byte[length];
        Random.Shared.NextBytes(buffer);
        return buffer;
    }
}
