using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using LanTransfer;

static class TransferTests
{
    static int checks;
    static string testRoot;
    static void Assert(bool value, string description)
    { if (!value) throw new Exception("FAIL: " + description); checks++; Console.WriteLine("PASS: " + description); }
    static void Quiet(ProgressInfo p) { }
    static void QuietLog(string line) { }
    static TransferClient Send(TransferServer server, string code, params string[] sources)
    {
        var client = new TransferClient();
        try { client.Send("127.0.0.1", server.Port, code, sources, Quiet, QuietLog); }
        finally { client.Dispose(); }
        return client;
    }
    static Wire Connect(TransferServer server, TcpClient socket, string code)
    {
        socket.Connect(IPAddress.Loopback, server.Port); socket.ReceiveTimeout = 15000;
        var wire = new Wire(socket.GetStream());
        if (wire.Text() != "LANTRANSFER/1") throw new Exception("Protocol mismatch");
        wire.Text(Transfer.Proof(code, wire.Text()));
        if (wire.Text() != "OK") throw new Exception("Authentication failed");
        return wire;
    }
    static void Interrupt(TransferServer server, string code, string file, int prefix, ManualResetEvent disconnect)
    {
        disconnect.Reset();
        using (var socket = new TcpClient())
        using (var wire = Connect(server, socket, code))
        {
            wire.Text("FILE"); wire.Text(Path.GetFileName(file)); wire.Number(new FileInfo(file).Length);
            wire.Text(Transfer.HashFile(file, null, null)); wire.Number(File.GetLastWriteTimeUtc(file).Ticks);
            if (wire.Text() != "READY") throw new Exception("Not ready"); wire.Number(); wire.Text();
            wire.Text("RESET"); if (wire.Text() != "GO") throw new Exception("Not GO");
            byte[] data = new byte[prefix];
            using (var input = File.OpenRead(file)) { if (input.Read(data, 0, data.Length) != data.Length) throw new Exception("Short source"); }
            wire.Stream.Write(data, 0, data.Length);
        }
        if (!disconnect.WaitOne(15000)) throw new Exception("Receiver did not close interrupted connection");
    }
    static bool Throws(Action action)
    { try { action(); return false; } catch (Exception) { return true; } }
    static void Main(string[] args)
    {
        try
        {
            testRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "test-output", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")));
            Directory.CreateDirectory(testRoot);
            Console.WriteLine("Artifacts: " + testRoot);
            string source = Path.Combine(testRoot, "source"), destination = Path.Combine(testRoot, "received");
            string batch = Path.Combine(source, "中文资料"), nested = Path.Combine(batch, "子目录");
            Directory.CreateDirectory(nested); Directory.CreateDirectory(Path.Combine(batch, "空目录"));
            File.WriteAllText(Path.Combine(nested, "说明.txt"), "大量文件 / 中文路径 / 完整校验", Encoding.UTF8);
            File.WriteAllBytes(Path.Combine(batch, "empty.bin"), new byte[0]);
            for (int i = 0; i < 1000; i++) File.WriteAllText(Path.Combine(batch, "small-" + i.ToString("D4") + ".txt"), "file " + i);
            byte[] random = new byte[16 * 1024 * 1024]; new Random(42).NextBytes(random);
            string large = Path.Combine(source, "large.bin"); File.WriteAllBytes(large, random);
            const string code = "12345678";
            using (var disconnect = new ManualResetEvent(false))
            using (var server = new TransferServer(destination, code, Quiet, delegate(string line) { if (line.StartsWith("连接结束")) disconnect.Set(); }))
            {
                server.Start(0, false);
                Assert(Throws(delegate { Send(server, "wrong123", large); }), "incorrect code rejected");
                var watch = Stopwatch.StartNew();
                TransferClient batchResult = Send(server, code, batch);
                watch.Stop();
                Assert(batchResult.Completed == 1002 && batchResult.Failed == 0, "1002 files transfer successfully");
                Assert(Directory.Exists(Path.Combine(destination, "中文资料", "空目录")), "empty directory preserved");
                foreach (string path in Directory.EnumerateFiles(batch, "*", SearchOption.AllDirectories))
                {
                    string received = Path.Combine(destination, "中文资料", path.Substring(batch.Length + 1));
                    if (Transfer.HashFile(path, null, null) != Transfer.HashFile(received, null, null)) throw new Exception("Batch checksum mismatch: " + path);
                }
                Assert(true, "all 1002 destination checksums match");
                Console.WriteLine("BATCH: " + watch.Elapsed.TotalSeconds.ToString("0.000") + " s");
                using (var canceledClient = new TransferClient())
                {
                    Assert(Throws(delegate
                    {
                        canceledClient.Send("127.0.0.1", server.Port, code, new[] { large }, delegate(ProgressInfo p) { if (p.Stage == "读取文件指纹") canceledClient.Cancel(); }, QuietLog);
                    }), "sender cancellation interrupts file hashing");
                }
                TransferClient repeat = Send(server, code, batch);
                Assert(repeat.Completed == 1002 && repeat.PayloadBytes == 0, "repeat skips completed content without payload");
                string conflict = Path.Combine(destination, "large.bin"); File.WriteAllText(conflict, "preserve me");
                TransferClient conflictResult = Send(server, code, large, Path.Combine(batch, "empty.bin"));
                Assert(conflictResult.Failed == 1 && conflictResult.Completed == 1 && File.ReadAllText(conflict) == "preserve me", "conflict preserves target and later files still transfer");
                File.Delete(conflict);
                Interrupt(server, code, large, 2 * 1024 * 1024, disconnect);
                Assert(!File.Exists(conflict), "interruption does not expose partial file as complete");
                string[] parts = Directory.GetFiles(Path.Combine(destination, ".lantransfer-cache"), "*.part");
                Assert(parts.Length == 1 && new FileInfo(parts[0]).Length == 2 * 1024 * 1024, "interruption persists exactly the received prefix");
                TransferClient resume = Send(server, code, large);
                Assert(resume.LastResumeOffset == 2 * 1024 * 1024 && resume.PayloadBytes == random.Length - 2 * 1024 * 1024, "resume transmits only remaining bytes");
                Assert(Transfer.HashFile(large, null, null) == Transfer.HashFile(conflict, null, null), "resumed file SHA-256 matches");
                File.Delete(conflict);
                Interrupt(server, code, large, 2 * 1024 * 1024, disconnect);
                parts = Directory.GetFiles(Path.Combine(destination, ".lantransfer-cache"), "*.part");
                using (var corrupt = new FileStream(parts[0], FileMode.Open, FileAccess.Write)) { corrupt.WriteByte(255); }
                TransferClient repaired = Send(server, code, large);
                Assert(repaired.LastResumeOffset == 0 && repaired.PayloadBytes == random.Length, "corrupted prefix forces a clean restart");
                Assert(Transfer.HashFile(large, null, null) == Transfer.HashFile(conflict, null, null), "restarted file SHA-256 matches");
                using (var socket = new TcpClient())
                using (var wire = Connect(server, socket, code))
                {
                    wire.Text("FILE"); wire.Text("../escape.txt"); wire.Number(0); wire.Text(Transfer.HashText("")); wire.Number(DateTime.UtcNow.Ticks);
                    Assert(wire.Text() == "ERROR", "network path traversal rejected"); wire.Text();
                    wire.Text("END"); Assert(wire.Text() == "OK", "protocol stays aligned after preflight rejection");
                }
                using (var socket = new TcpClient())
                using (var wire = Connect(server, socket, code))
                {
                    wire.Text("FILE"); wire.Text("bad-hash.bin"); wire.Number(4); wire.Text(Transfer.HashText("good")); wire.Number(DateTime.UtcNow.Ticks);
                    Assert(wire.Text() == "READY", "checksum corruption test accepted"); wire.Number(); wire.Text();
                    wire.Text("CONTINUE"); wire.Text(); wire.Stream.Write(new byte[] { 1, 2, 3, 4 }, 0, 4);
                    Assert(wire.Text().StartsWith("校验失败") && !File.Exists(Path.Combine(destination, "bad-hash.bin")), "bad payload never becomes a final file");
                    wire.Text("END"); wire.Text();
                }
                foreach (string path in new[] { "../x", "/x", "C:\\x", "x:stream", "CON", "a/NUL.txt", "x.", "x ", ".lantransfer-cache/x", "a//b" })
                    if (!Throws(delegate { Transfer.SafePath(destination, path); })) throw new Exception("Unsafe path accepted: " + path);
                Assert(true, "traversal, ADS, reserved names and path aliases blocked");
                using (var memory = new MemoryStream())
                {
                    var writer = new BinaryWriter(memory); writer.Write(Int32.MaxValue); memory.Position = 0;
                    Assert(Throws(delegate { new Wire(memory).Text(); }), "oversized control frame rejected before allocation");
                }
                using (var memory = new MemoryStream())
                {
                    var wire = new Wire(memory); long size = 8L * 1024 * 1024 * 1024 + 7;
                    wire.Number(size); memory.Position = 0; Assert(wire.Number() == size, "file-size protocol uses 64-bit values beyond 4 GiB");
                }
                if (Array.IndexOf(args, "--large") >= 0)
                {
                    string huge = Path.Combine(source, "over-4GiB.bin");
                    long hugeSize = 4L * 1024 * 1024 * 1024 + 1024 * 1024;
                    using (var file = new FileStream(huge, FileMode.Create, FileAccess.Write))
                    {
                        file.SetLength(hugeSize);
                        file.Position = 4L * 1024 * 1024 * 1024 - 8;
                        byte[] marker = Encoding.ASCII.GetBytes("crossing-4GiB-boundary");
                        file.Write(marker, 0, marker.Length);
                        file.Position = hugeSize - 1; file.WriteByte(173);
                    }
                    Console.WriteLine("LARGE: sending " + hugeSize + " bytes, including data across 4 GiB boundary...");
                    var largeClock = Stopwatch.StartNew();
                    TransferClient hugeResult = Send(server, code, huge);
                    string hugeTarget = Path.Combine(destination, Path.GetFileName(huge));
                    Assert(hugeResult.Completed == 1 && hugeResult.PayloadBytes == hugeSize && new FileInfo(hugeTarget).Length == hugeSize, "real >4 GiB file transfers successfully");
                    // Receiver only commits the target after checking its complete SHA-256.
                    using (var file = File.OpenRead(hugeTarget))
                    {
                        file.Position = 4L * 1024 * 1024 * 1024 - 8;
                        byte[] boundary = new byte["crossing-4GiB-boundary".Length]; file.Read(boundary, 0, boundary.Length);
                        Assert(Encoding.ASCII.GetString(boundary) == "crossing-4GiB-boundary", "bytes crossing 4 GiB boundary preserved");
                        file.Position = hugeSize - 1; Assert(file.ReadByte() == 173, "last byte beyond 4 GiB preserved");
                    }
                    Console.WriteLine("LARGE: " + largeClock.Elapsed.TotalSeconds.ToString("0.000") + " s including source and receiver SHA-256");
                    File.Delete(huge); File.Delete(hugeTarget);
                }
                // An authenticated idle connection must be stoppable without a long wait.
                using (var socket = new TcpClient())
                using (var wire = Connect(server, socket, code))
                {
                    var stopClock = Stopwatch.StartNew(); server.Dispose();
                    Assert(stopClock.ElapsedMilliseconds < 3500, "receiver stop interrupts an idle connection promptly");
                }
            }
            var blocker = new TcpListener(IPAddress.Any, 0);
            blocker.ExclusiveAddressUse = true; blocker.Start();
            try
            {
                int occupiedPort = ((IPEndPoint)blocker.LocalEndpoint).Port;
                using (var fallbackServer = new TransferServer(Path.Combine(testRoot, "fallback"), "87654321", Quiet, QuietLog))
                {
                    fallbackServer.Start(occupiedPort, false);
                    Assert(fallbackServer.Port != occupiedPort && fallbackServer.Port > 0, "occupied TCP port automatically selects a free port");
                    var fallbackResult = Send(fallbackServer, "87654321", Path.Combine(batch, "empty.bin"));
                    Assert(fallbackResult.Completed == 1 && fallbackResult.Failed == 0, "files transfer successfully on the automatic fallback port");
                }
            }
            finally { blocker.Stop(); }
            using (var discoverServer = new TransferServer(Path.Combine(testRoot, "discovery"), "87654321", Quiet, Console.WriteLine))
            using (var secondDiscoveryServer = new TransferServer(Path.Combine(testRoot, "discovery2"), "87654321", Quiet, Console.WriteLine))
            {
                discoverServer.Start(0, true);
                secondDiscoveryServer.Start(0, true);
                Assert(discoverServer.DiscoveryListenPort > 0 && secondDiscoveryServer.DiscoveryListenPort > 0 && discoverServer.DiscoveryListenPort != secondDiscoveryServer.DiscoveryListenPort, "multiple receivers select distinct UDP discovery ports");
                using (var udp = new UdpClient(0))
                {
                    udp.Client.ReceiveTimeout = 3000;
                    byte[] query = Encoding.UTF8.GetBytes("LANTRANSFER_DISCOVER_1");
                    udp.Send(query, query.Length, new IPEndPoint(IPAddress.Loopback, discoverServer.DiscoveryListenPort));
                    IPEndPoint peer = new IPEndPoint(IPAddress.Any, 0);
                    string reply = Encoding.UTF8.GetString(udp.Receive(ref peer));
                    Assert(reply.StartsWith("LANTRANSFER_1|" + discoverServer.Port + "|"), "UDP device discovery reports the receiver port");
                    Assert(!reply.Contains("87654321"), "device discovery does not reveal the receive code");
                    udp.Send(query, query.Length, new IPEndPoint(IPAddress.Loopback, secondDiscoveryServer.DiscoveryListenPort));
                    string secondReply = Encoding.UTF8.GetString(udp.Receive(ref peer));
                    Assert(secondReply.StartsWith("LANTRANSFER_1|" + secondDiscoveryServer.Port + "|"), "device discovery works after UDP port fallback");
                }
            }
            Console.WriteLine("SUCCESS: " + checks + " checks passed.");
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    }
}
