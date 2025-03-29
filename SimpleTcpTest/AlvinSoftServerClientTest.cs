using Microsoft.VisualStudio.TestTools.UnitTesting;
using AlvinSoft.TcpMs;
using AlvinSoft.TcpMs.Packages;

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

        TestServer = new(System.Net.IPAddress.Any, 19910, settings);
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

        TestServer = new(System.Net.IPAddress.Any, 19911, settings);
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

        TestServer = new(System.Net.IPAddress.Any, 19912, settings);
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


    [TestCleanup]
    public void Cleanup() {
        TestServer?.Close();
        TestClient?.Close();
        Dbg.OutputAll();
    }

    private static async Task TestSendReceive() {

        static byte[] RandomBytes(int length) {
            byte[] buffer = new byte[length];
            Random.Shared.NextBytes(buffer);
            return buffer;
        }

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

}
