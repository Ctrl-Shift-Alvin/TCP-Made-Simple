using Microsoft.VisualStudio.TestTools.UnitTesting;
using AlvinSoft.TcpMs;
using AlvinSoft.TcpMs.Packages;

namespace AlvinSoft.TcpMsTest;

[TestClass]
public class AlvinSoftTcpTests {

    public static TcpMsServer TestServer;
    public static TcpMsClient TestClient;

    [TestMethod("Server/Client Test No Encryption No Ping")]
    public async Task Test1() {

        ServerSettings settings = new(null) {
            EncryptionEnabled = false,
            PingIntervalMs = 0
        };

        TestServer = new(System.Net.IPAddress.Any, 2020, settings);
        await TestServer.StartAsync();

        TestClient = new("127.0.0.1", 2020);
        Assert.IsTrue(await TestClient.TryConnectAsync());

        await TestSendReceive();
        await TestSendReceive();
        await TestSendReceive();
        await TestSendReceive();

    }

    [TestMethod("Server/Client Test Encryption")]
    public async Task Test2() {

        ServerSettings settings = new("password");

        TestServer = new(System.Net.IPAddress.Any, 2020, settings);
        await TestServer.StartAsync();

        TestClient = new("127.0.0.1", 2020);
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

        TestServer = new(System.Net.IPAddress.Any, 2020, settings);
        await TestServer.StartAsync();

        TestClient = new("127.0.0.1", 2020);
        Assert.IsTrue(await TestClient.TryConnectAsync("password"));

        await Task.Delay(settings.PingIntervalMs * 4);

        Assert.IsTrue(TestServer.ClientCount == 1);

        TaskCompletionSource completion = new();
        void OnDisconnect(byte[] _) {
            completion.SetResult();
        }
        TestServer.ClientDisconnected += OnDisconnect;

        //forcefully close the client
        TestClient.Close();

        await Task.WhenAny(Task.Delay(settings.PingIntervalMs + settings.PingTimeoutMs), completion.Task);
        Assert.IsTrue(completion.Task.IsCompleted);

        TestServer.ClientDisconnected -= OnDisconnect;
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

        //test server send/client receive ----------------------------------------
        bool receivedData = false;
        byte[] sentData = RandomBytes(128);
        TaskCompletionSource completion = new();

        void clientReceiveHandler(byte[] data) {
            receivedData = true;
            CollectionAssert.AreEqual(sentData, data);
            completion.SetResult();
        }
        TestClient.BlobReceivedEvent += clientReceiveHandler;

        await TestServer.BroadcastBlobAsync(sentData);
        await completion.Task.WaitAsync(TimeSpan.FromMilliseconds(2000));
        Assert.IsTrue(receivedData);

        TestClient.BlobReceivedEvent -= clientReceiveHandler;


        //test client send/server receive --------------------------------------
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

    }

}
