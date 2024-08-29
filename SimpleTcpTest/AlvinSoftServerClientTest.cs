using Microsoft.VisualStudio.TestTools.UnitTesting;
using AlvinSoft.TcpMs;
using AlvinSoft.TcpMs.Packages;

namespace AlvinSoft.TcpMsTest;

[TestClass]
public class AlvinSoftTcpTests {

    public static TcpMsServer TestServer;
    public static TcpMsClient TestClient;


    [TestMethod("Server/Client Test No Encryption")]
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

        await TestClient.DisconnectAsync();
        await TestServer.StopAsync();

    }

    [TestMethod("Server/Client Test Encryption")]
    public async Task Test2() {

        ServerSettings settings = new("password");

        TestServer = new(System.Net.IPAddress.Any, 2020, settings);
        await TestServer.StartAsync();

        TestClient = new("127.0.0.1", 2020);
        Assert.IsFalse(await TestClient.TryConnectAsync("Password"));
        Assert.IsTrue(await TestClient.TryConnectAsync("password"));

        await TestSendReceive();

        await TestClient.DisconnectAsync();
        await TestServer.StopAsync();

    }

    [TestCleanup]
    public void Cleanup() {
        TestServer?.Close();
        TestClient?.Close();
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

        TestClient.BlobReceivedEvent += (data) => {

            receivedData = true;
            CollectionAssert.AreEqual(sentData, data);

        };

        await TestServer.BroadcastBlobAsync(sentData, new CancellationTokenSource(1000).Token);

        await Task.Delay(2000);

        Assert.IsTrue(receivedData);
        

        //test client send/server receive --------------------------------------
        receivedData = false;
        sentData = RandomBytes(128);

        TestServer.BlobReceivedEvent += (_, data) => {

            receivedData = true;
            CollectionAssert.AreEqual(sentData, data);

        };

        await TestClient.SendBlobAsync(sentData, new CancellationTokenSource(1000).Token);

        await Task.Delay(100);

        Assert.IsTrue(receivedData);

    }

}
