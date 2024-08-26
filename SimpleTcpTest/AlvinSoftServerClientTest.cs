using System.Text;

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
        server.Start();

        TcpMsClient client = new("127.0.0.1", 2020);
        Assert.IsTrue(await client.TryConnectAsync("password"));

        //await TestSendReceive(server, client);

        await client.VerifyConnectionAsync();

        await client.DisconnectAsync();
        await server.StopAsync();

    }

    [TestMethod("Server/Client Test Encryption")]
    public async Task Test2() {

        ServerSettings settings = new("password");

        TcpMsServer server = new(System.Net.IPAddress.Any, 2020, settings);
        server.Start();

        TcpMsClient client = new("127.0.0.1", 2020);
        Assert.IsFalse(await client.TryConnectAsync("Password"));
        Assert.IsTrue(await client.TryConnectAsync("password"));

        await TestSendReceive(server, client);

        //Assert.IsTrue(await client.VerifyConnectionAsync());

        await client.DisconnectAsync();
        await server.StopAsync();

    }


    private static async Task TestSendReceive(TcpMsServer server, TcpMsClient client) {

        static byte[] RandomBytes(int length) {
            byte[] buffer = new byte[length];
            Random.Shared.NextBytes(buffer);
            return buffer;
        }

        //test server send/client receive
        bool[] received = new bool[7];
        object[] datas =
        [
            Random.Shared.Next(0, 2) == 0,
            (byte)Random.Shared.Next(0, byte.MaxValue + 1),
            (short)Random.Shared.Next(0, short.MaxValue + 1),
            Random.Shared.Next(),
            Random.Shared.NextInt64(),
            Encoding.Unicode.GetString(RandomBytes(Random.Shared.Next(0, 33))),
            RandomBytes(Random.Shared.Next(6, 65))
        ];

        client.OnDataReceivedEvent += (data, type) => {

            switch (type) {

                case Package.DataTypes.Bool: {
                    Assert.AreEqual((bool)data, (bool)datas[0]);
                    received[0] = true;
                }
                break;

                case Package.DataTypes.Byte: {
                    Assert.AreEqual((byte)data, (byte)datas[1]);
                    received[1] = true;
                }
                break;

                case Package.DataTypes.Short: {
                    Assert.AreEqual((short)data, (short)datas[2]);
                    received[2] = true;
                }
                break;

                case Package.DataTypes.Int: {
                    Assert.AreEqual((int)data, (int)datas[3]);
                    received[3] = true;
                }
                break;

                case Package.DataTypes.Long: {
                    Assert.AreEqual((long)data, (long)datas[4]);
                    received[4] = true;
                }
                break;

                case Package.DataTypes.String: {
                    Assert.AreEqual((string)data, (string)datas[5]);
                    received[5] = true;
                }
                break;

                case Package.DataTypes.Blob: {
                    CollectionAssert.AreEqual((byte[])data, (byte[])datas[6]);
                    received[6] = true;
                }
                break;

            }

        };

        await server.BroadcastBoolAsync((bool)datas[0]);
        await server.BroadcastByteAsync((byte)datas[1]);
        await server.BroadcastShortAsync((short)datas[2]);
        await server.BroadcastIntAsync((int)datas[3]);
        await server.BroadcastLongAsync((long)datas[4]);
        await server.BroadcastStringAsync((string)datas[5]);
        await server.BroadcastBlobAsync((byte[])datas[6]);

        await Task.Delay(1000);

        Assert.IsTrue(received.All(k => k == true));


        //test client send/server receive
        received = new bool[7];
        datas =
        [
            Random.Shared.Next(0, 2) == 0,
            (byte)Random.Shared.Next(0, byte.MaxValue + 1),
            (short)Random.Shared.Next(0, short.MaxValue + 1),
            Random.Shared.Next(),
            Random.Shared.NextInt64(),
            Encoding.Unicode.GetString(RandomBytes(Random.Shared.Next(0, 33))),
            RandomBytes(Random.Shared.Next(6, 65))
        ];

        server.OnDataReceivedEvent += (_, data, type) => {

            switch (type) {

                case Package.DataTypes.Bool: {
                    Assert.AreEqual((bool)data, (bool)datas[0]);
                    received[0] = true;
                }
                break;

                case Package.DataTypes.Byte: {
                    Assert.AreEqual((byte)data, (byte)datas[1]);
                    received[1] = true;
                }
                break;

                case Package.DataTypes.Short: {
                    Assert.AreEqual((short)data, (short)datas[2]);
                    received[2] = true;
                }
                break;

                case Package.DataTypes.Int: {
                    Assert.AreEqual((int)data, (int)datas[3]);
                    received[3] = true;
                }
                break;

                case Package.DataTypes.Long: {
                    Assert.AreEqual((long)data, (long)datas[4]);
                    received[4] = true;
                }
                break;

                case Package.DataTypes.String: {
                    Assert.AreEqual((string)data, (string)datas[5]);
                    received[5] = true;
                }
                break;

                case Package.DataTypes.Blob: {
                    CollectionAssert.AreEqual((byte[])data, (byte[])datas[6]);
                    received[6] = true;
                }
                break;

            }

        };

        await client.SendBoolAsync((bool)datas[0]);
        await client.SendByteAsync((byte)datas[1]);
        await client.SendShortAsync((short)datas[2]);
        await client.SendIntAsync((int)datas[3]);
        await client.SendLongAsync((long)datas[4]);
        await client.SendStringAsync((string)datas[5]);
        await client.SendBlobAsync((byte[])datas[6]);

        await Task.Delay(1000);

        Assert.IsTrue(received.All(k => k == true));

    }

}
