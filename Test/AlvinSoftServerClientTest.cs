using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AlvinSoft.Test;

[TestClass]
public class AlvinSoftTcpTests {

    [TestMethod("Server/Client Test No Encryption")]
    public async Task Test1() {

        ServerSettings settings = new(null) {
            EncryptionEnabled = false,
            PingIntervalMs = 0
        };

        TcpMsServer server = new(System.Net.IPAddress.Any, 2020, settings);
        await server.StartAsync();

        TcpMsClient client = new("127.0.0.1", 2020);
        Assert.IsTrue(await client.TryConnectAsync("password"));

        await TestSendReceive(server, client);

        await client.DisconnectAsync();
        await server.StopAsync();

    }

    [TestMethod("Server/Client Test Encryption")]
    public async Task Test2() {

        ServerSettings settings = new("password");

        TcpMsServer server = new(System.Net.IPAddress.Any, 2020, settings);
        await server.StartAsync();

        TcpMsClient client = new("127.0.0.1", 2020);
        Assert.IsFalse(await client.TryConnectAsync("Password"));
        Assert.IsTrue(await client.TryConnectAsync("password"));

        await TestSendReceive(server, client);

        await client.DisconnectAsync();
        await server.StopAsync();

    }


    private static async Task TestSendReceive(TcpMsServer server, TcpMsClient client) {

        static byte[] RandomBytes(int length) {
            byte[] buffer = new byte[length];
            Random.Shared.NextBytes(buffer);
            return buffer;
        }

        //test server send/client receive ----------------------------------------
        bool receivedData = false;
        byte[] sentData = RandomBytes(128);

        client.BlobReceivedEvent += (data) => {

            receivedData = true;
            CollectionAssert.AreEqual(sentData, data);

        };

        server.BroadcastBlob(sentData);

        await Task.Delay(100);

        Assert.IsTrue(receivedData);
        

        //test client send/server receive --------------------------------------
        receivedData = false;
        sentData = RandomBytes(128);

        server.BlobReceivedEvent += (_, data) => {

            receivedData = true;
            CollectionAssert.AreEqual(sentData, data);

        };

        await Task.Delay(100);

        Assert.IsTrue(receivedData);

    }

}
