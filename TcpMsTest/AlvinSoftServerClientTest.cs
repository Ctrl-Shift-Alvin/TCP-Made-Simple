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

    [TestMethod("Ping Functionality Test")]
    public async Task Test3() {

        ServerSettings settings = new("password") {
            PingIntervalMs = 1000,
            PingTimeoutMs = 500
        };

        TestServer = new(IPAddress.Any, 19912, settings);
        await TestServer.StartAsync();

        TestClient = new("127.0.0.1", 19912);
        Assert.IsTrue(await TestClient.TryConnectAsync("password"));

        await Task.Delay(settings.PingIntervalMs * 4);

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

    [TestMethod("Client Disconnect Test")]
    public async Task Test4() {

        ServerSettings settings = new(null) {
            EncryptionEnabled = false,
            PingIntervalMs = 0
        };

        TestServer = new(IPAddress.Any, 19913, settings);
        await TestServer.StartAsync();

        TestClient = new("127.0.0.1", 19913);
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

    [TestMethod("Server Broadcast Test")]
    public async Task Test5() {

        ServerSettings settings = new(null) {
            EncryptionEnabled = false,
            PingIntervalMs = 0
        };

        TestServer = new(IPAddress.Any, 19914, settings);
        await TestServer.StartAsync();

        TestClient = new("127.0.0.1", 19914);
        Assert.IsTrue(await TestClient.TryConnectAsync());

        bool receivedData = false;
        byte[] sentData = RandomBytes(128);
        TaskCompletionSource completion = new();

        void clientReceiveBlobHandler(byte[] data) {
            receivedData = true;
            CollectionAssert.AreEqual(sentData, data);
            completion.SetResult();
        }
        TestClient.BlobReceivedEvent += clientReceiveBlobHandler;

        await TestServer.BroadcastBlobAsync(sentData);
        await completion.Task.WaitAsync(TimeSpan.FromMilliseconds(2000));
        Assert.IsTrue(receivedData);

        TestClient.BlobReceivedEvent -= clientReceiveBlobHandler;
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

        await TestServer.BroadcastBlobAsync(sentData);
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
