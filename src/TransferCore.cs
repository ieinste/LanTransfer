using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace LanTransfer
{
    public sealed class ProgressInfo
    {
        public string FileName, Stage;
        public long Done, Length;
        public int Files;
        public ProgressInfo(string name, string stage, long done, long length, int files)
        { FileName = name; Stage = stage; Done = done; Length = length; Files = files; }
    }

    // Frames have an explicit length limit. File payloads are streamed separately.
    public sealed class Wire : IDisposable
    {
        public readonly Stream Stream;
        private readonly BinaryReader reader;
        private readonly BinaryWriter writer;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        public Wire(Stream stream) { Stream = stream; reader = new BinaryReader(stream); writer = new BinaryWriter(stream); }
        public void Text(string value)
        {
            byte[] bytes = Utf8.GetBytes(value);
            if (bytes.Length > 32768) throw new IOException("协议字段过长。");
            byte[] frame = new byte[4 + bytes.Length];
            Buffer.BlockCopy(BitConverter.GetBytes(bytes.Length), 0, frame, 0, 4);
            Buffer.BlockCopy(bytes, 0, frame, 4, bytes.Length);
            Stream.Write(frame, 0, frame.Length);
        }
        public string Text()
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > 32768) throw new IOException("无效协议字段。");
            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException();
            return Utf8.GetString(bytes);
        }
        public void Number(long value) { writer.Write(value); writer.Flush(); }
        public long Number() { return reader.ReadInt64(); }
        public void Dispose() { Stream.Dispose(); }
    }

    public sealed class TransferServer : IDisposable
    {
        private readonly string root, code;
        private readonly Action<ProgressInfo> progress;
        private readonly Action<string> log;
        private readonly object gate = new object();
        private TcpListener listener;
        private TcpClient active;
        private UdpClient discovery;
        private Thread worker, discoveryWorker;
        private volatile bool stopped;
        private volatile bool cancelCurrent;
        public int Port { get; private set; }
        public int DiscoveryListenPort { get; private set; }
        public const int DefaultPort = 45871;
        public const int DiscoveryPort = 37951;
        public const int DiscoveryPortCount = 8;
        public TransferServer(string folder, string pin, Action<ProgressInfo> onProgress, Action<string> onLog)
        {
            root = Path.GetFullPath(folder); code = pin; progress = onProgress; log = onLog;
            if (pin.Length < 6) throw new ArgumentException("接收码至少 6 位。");
        }
        public void Start(int port, bool enableDiscovery)
        {
            Directory.CreateDirectory(root);
            Transfer.CheckLinks(root);
            listener = new TcpListener(IPAddress.Any, port);
            listener.ExclusiveAddressUse = true;
            try { listener.Start(8); }
            catch (SocketException ex)
            {
                listener.Stop();
                if (port == 0 || (ex.SocketErrorCode != SocketError.AddressAlreadyInUse && ex.SocketErrorCode != SocketError.AccessDenied)) throw;
                listener = new TcpListener(IPAddress.Any, 0);
                listener.ExclusiveAddressUse = true;
                listener.Start(8);
                log("接收端口 " + port + " 不可用，已自动选择可用端口。");
            }
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            worker = new Thread(Run); worker.IsBackground = true; worker.Start();
            if (enableDiscovery)
            {
                SocketException failure = null;
                for (int candidate = DiscoveryPort; candidate < DiscoveryPort + DiscoveryPortCount; candidate++)
                {
                    UdpClient socket = new UdpClient(AddressFamily.InterNetwork);
                    try
                    {
                        socket.ExclusiveAddressUse = true;
                        socket.Client.Bind(new IPEndPoint(IPAddress.Any, candidate));
                        discovery = socket; DiscoveryListenPort = candidate;
                        discoveryWorker = new Thread(DiscoverLoop); discoveryWorker.IsBackground = true; discoveryWorker.Start();
                        break;
                    }
                    catch (SocketException ex) { failure = ex; socket.Close(); }
                }
                if (discovery == null) log("设备搜索端口不可用（" + (failure == null ? "未知" : failure.NativeErrorCode.ToString()) + "），仍可使用 IP 连接。");
            }
        }
        private void DiscoverLoop()
        {
            while (!stopped)
            {
                try
                {
                    IPEndPoint peer = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = discovery.Receive(ref peer);
                    if (Encoding.UTF8.GetString(data) != "LANTRANSFER_DISCOVER_1") continue;
                    byte[] reply = Encoding.UTF8.GetBytes("LANTRANSFER_1|" + Port + "|" + Environment.MachineName);
                    discovery.Send(reply, reply.Length, peer);
                }
                catch (ObjectDisposedException) { return; }
                catch (SocketException ex) { if (!stopped) log("设备搜索服务中断，可使用 IP 连接：" + ex.Message); return; }
            }
        }
        private void Run()
        {
            while (!stopped)
            {
                TcpClient client = null;
                try
                {
                    client = listener.AcceptTcpClient();
                    lock (gate) { if (stopped) { client.Close(); return; } active = client; cancelCurrent = false; }
                    client.NoDelay = true;
                    // A non-responsive peer cannot hold the receiver indefinitely.
                    client.ReceiveTimeout = 120000; client.SendTimeout = 120000;
                    using (Wire wire = new Wire(client.GetStream()))
                    {
                        string nonce = Transfer.RandomToken(32);
                        wire.Text("LANTRANSFER/1"); wire.Text(nonce);
                        if (!Transfer.Same(wire.Text(), Transfer.Proof(code, nonce)))
                        { wire.Text("接收码不正确。"); continue; }
                        wire.Text("OK");
                        // Hashing a very large file can take minutes; cancellation closes the socket.
                        client.ReceiveTimeout = 0; client.SendTimeout = 0;
                        log("已连接：" + client.Client.RemoteEndPoint);
                        int count = 0;
                        while (!stopped)
                        {
                            string kind = wire.Text();
                            if (kind == "END") { wire.Text("OK"); log("本次接收完成。"); break; }
                            string relative = wire.Text();
                            if (kind == "DIR")
                            {
                                try { Directory.CreateDirectory(Transfer.SafePath(root, relative)); wire.Text("OK"); }
                                catch (Exception ex) { wire.Text(ex.Message); }
                            }
                            else if (kind == "FILE") ReceiveFile(wire, relative, ref count);
                            else throw new IOException("不支持的传输指令。");
                        }
                    }
                }
                catch (Exception ex) { if (!stopped) log("连接结束，未完成文件可续传：" + ex.Message); }
                finally
                {
                    if (client != null) client.Close();
                    lock (gate) { if (active == client) active = null; }
                }
            }
        }
        private void ReceiveFile(Wire wire, string relative, ref int count)
        {
            long size = wire.Number(); string hash = wire.Text(); long ticks = wire.Number();
            string path = null, partial = null;
            FileStream output = null;
            try
            {
                if (size < 0 || !Transfer.IsHash(hash)) throw new IOException("无效文件信息。");
                path = Transfer.SafePath(root, relative);
                if (File.Exists(path))
                {
                    progress(new ProgressInfo(relative, "核对已存在文件", 0, size, count));
                    if (new FileInfo(path).Length == size && Transfer.HashFile(path, CheckCurrent, null) == hash)
                    { wire.Text("SKIP"); count++; log("已存在，跳过：" + relative); return; }
                    throw new IOException("目标已存在不同内容的同名文件，请更换接收目录或先重命名：" + relative);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string tempRoot = Transfer.SafePath(root, ".lantransfer-cache", true);
                Directory.CreateDirectory(tempRoot);
                partial = Path.Combine(tempRoot, Transfer.HashText(relative.ToUpperInvariant() + "\n" + hash) + ".part");
                Transfer.CheckLinks(partial);
                output = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.SequentialScan);
                if (output.Length > size) output.SetLength(0);
                long offset = output.Length;
                progress(new ProgressInfo(relative, "核对续传数据", offset, size, count));
                string prefix = Transfer.HashStream(output, offset, CheckCurrent);
                wire.Text("READY"); wire.Number(offset); wire.Text(prefix);
                string decision = wire.Text();
                if (decision == "RESET") { offset = 0; output.SetLength(0); }
                else if (decision != "CONTINUE") throw new IOException("无效续传指令。");
                output.Position = offset;
                wire.Text("GO");
                byte[] buffer = Transfer.NewBuffer(size - offset);
                long done = offset; Stopwatch clock = Stopwatch.StartNew();
                while (done < size)
                {
                    CheckCurrent();
                    int read = wire.Stream.Read(buffer, 0, (int)Math.Min(buffer.Length, size - done));
                    if (read == 0) throw new EndOfStreamException("对方已断开。");
                    output.Write(buffer, 0, read); done += read;
                    if (clock.ElapsedMilliseconds >= 150)
                    { progress(new ProgressInfo(relative, "接收中", done, size, count)); clock.Restart(); }
                }
                output.Flush();
                progress(new ProgressInfo(relative, "校验文件", size, size, count));
                string actual = Transfer.HashStream(output, size, CheckCurrent);
                CheckCurrent();
                output.Dispose(); output = null;
                if (actual != hash)
                {
                    File.Delete(partial);
                    wire.Text("校验失败，重新发送即可重传此文件。"); log("校验失败：" + relative); return;
                }
                Transfer.CheckLinks(path);
                File.Move(partial, path); // Never overwrite an existing destination.
                try { File.SetLastWriteTimeUtc(path, new DateTime(ticks, DateTimeKind.Utc)); }
                catch (ArgumentException) { log("文件已接收，修改时间无效：" + relative); }
                catch (IOException) { log("文件已接收，无法保留修改时间：" + relative); }
                catch (UnauthorizedAccessException) { log("文件已接收，无法保留修改时间：" + relative); }
                count++; progress(new ProgressInfo(relative, "文件完成", size, size, count)); wire.Text("OK");
            }
            catch (Exception ex)
            {
                // Only preflight failures have an aligned protocol boundary.
                if (output == null && partial == null) { wire.Text("ERROR"); wire.Text(ex.Message); return; }
                throw;
            }
            finally { if (output != null) output.Dispose(); }
        }
        private void CheckCurrent() { if (stopped || cancelCurrent) throw new OperationCanceledException("已停止接收。"); }
        public void CancelCurrent()
        { lock (gate) { cancelCurrent = true; if (active != null) active.Close(); } }
        public void Dispose()
        {
            stopped = true;
            if (listener != null) listener.Stop();
            if (discovery != null) discovery.Close();
            CancelCurrent();
            if (worker != null && worker != Thread.CurrentThread) worker.Join(3000);
            if (discoveryWorker != null && discoveryWorker != Thread.CurrentThread) discoveryWorker.Join(1000);
        }
    }

    public sealed class TransferClient : IDisposable
    {
        private TcpClient client;
        private volatile bool canceled;
        public long LastResumeOffset { get; private set; }
        public long PayloadBytes { get; private set; }
        public int Completed { get; private set; }
        public int Failed { get; private set; }
        private void Check() { if (canceled) throw new OperationCanceledException("已暂停，重新发送相同文件可续传。"); }
        public void Send(string host, int port, string code, string[] sources, Action<ProgressInfo> progress, Action<string> log)
        {
            Check();
            client = new TcpClient(); client.NoDelay = true; client.SendTimeout = 120000; client.ReceiveTimeout = 120000;
            IAsyncResult connect = client.BeginConnect(host, port, null, null);
            try
            {
                if (!connect.AsyncWaitHandle.WaitOne(10000)) throw new IOException("连接超时，请检查 IP、接收状态和防火墙。");
                client.EndConnect(connect); Check();
                using (Wire wire = new Wire(client.GetStream()))
                {
                    if (wire.Text() != "LANTRANSFER/1") throw new IOException("对方不是兼容的文件互传程序。");
                    wire.Text(Transfer.Proof(code, wire.Text()));
                    string auth = wire.Text(); if (auth != "OK") throw new IOException(auth);
                    client.ReceiveTimeout = 0; client.SendTimeout = 0;
                    HashSet<string> roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string source in sources)
                    {
                        Check();
                        string full = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);
                        string name = Path.GetFileName(full);
                        if (String.IsNullOrEmpty(name) || full.EndsWith(":")) throw new IOException("请选择磁盘中的文件夹，不要直接选择整个盘符。");
                        if (!roots.Add(name)) throw new IOException("发送列表有同名根目录或文件，请分批发送：" + name);
                        SendEntry(wire, full, name, progress, log);
                    }
                    wire.Text("END"); if (wire.Text() != "OK") throw new IOException("接收方未确认完成。");
                }
            }
            finally { connect.AsyncWaitHandle.Close(); if (client != null) client.Close(); }
        }
        private void SendEntry(Wire wire, string path, string relative, Action<ProgressInfo> progress, Action<string> log)
        {
            Check(); Transfer.CheckLinks(path);
            if (Directory.Exists(path))
            {
                wire.Text("DIR"); wire.Text(relative);
                string result = wire.Text(); if (result != "OK") { Failed++; log(result); return; }
                foreach (string entry in Directory.EnumerateFileSystemEntries(path))
                    SendEntry(wire, entry, relative + "/" + Path.GetFileName(entry), progress, log);
                return;
            }
            // Lock against normal writers so hash, size and bytes describe one file version.
            using (FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan))
            {
                long size = input.Length;
                progress(new ProgressInfo(relative, "读取文件指纹", 0, size, Completed));
                string hash = Transfer.HashStream(input, size, Check);
                wire.Text("FILE"); wire.Text(relative); wire.Number(size); wire.Text(hash); wire.Number(File.GetLastWriteTimeUtc(path).Ticks);
                string state = wire.Text();
                if (state == "SKIP") { Completed++; log("跳过相同文件：" + relative); return; }
                if (state == "ERROR") { Failed++; log(wire.Text()); return; }
                if (state != "READY") throw new IOException("无效接收状态。");
                long offset = wire.Number(); string prefix = wire.Text();
                if (offset < 0 || offset > size) throw new IOException("无效续传位置。");
                progress(new ProgressInfo(relative, "核对续传数据", offset, size, Completed));
                if (Transfer.HashStream(input, offset, Check) != prefix) { offset = 0; wire.Text("RESET"); }
                else wire.Text("CONTINUE");
                LastResumeOffset = offset;
                if (wire.Text() != "GO") throw new IOException("接收方未准备好。");
                if (offset > 0) log("从 " + Transfer.Size(offset) + " 续传：" + relative);
                input.Position = offset;
                byte[] buffer = Transfer.NewBuffer(size - offset); long done = offset;
                Stopwatch clock = Stopwatch.StartNew();
                while (done < size)
                {
                    Check(); int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, size - done));
                    if (read == 0) throw new IOException("源文件在传输过程中改变。");
                    wire.Stream.Write(buffer, 0, read); done += read; PayloadBytes += read;
                    if (clock.ElapsedMilliseconds >= 150)
                    { progress(new ProgressInfo(relative, "发送中", done, size, Completed)); clock.Restart(); }
                }
                progress(new ProgressInfo(relative, "等待接收方校验", size, size, Completed));
                string result = wire.Text();
                if (result != "OK") { Failed++; log(result + " " + relative); }
                else { Completed++; progress(new ProgressInfo(relative, "文件完成", size, size, Completed)); }
            }
        }
        public void Cancel() { canceled = true; if (client != null) client.Close(); }
        public void Dispose() { Cancel(); }
    }

    public static class Transfer
    {
        public const int BufferSize = 1024 * 1024;
        public static byte[] NewBuffer(long length) { return new byte[(int)Math.Min(BufferSize, Math.Max(1, length))]; }
        public static string RandomToken(int bytes)
        { byte[] data = new byte[bytes]; using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(data); return Hex(data); }
        public static string NewCode()
        { byte[] data = new byte[4]; using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(data); return (BitConverter.ToUInt32(data, 0) % 100000000).ToString("D8"); }
        public static string Hex(byte[] data) { return BitConverter.ToString(data).Replace("-", "").ToLowerInvariant(); }
        public static bool IsHash(string hash)
        { if (hash.Length != 64) return false; foreach (char ch in hash) if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'))) return false; return true; }
        public static bool Same(string a, string b)
        { int diff = a.Length ^ b.Length; for (int i = 0; i < Math.Min(a.Length, b.Length); i++) diff |= a[i] ^ b[i]; return diff == 0; }
        public static string Proof(string code, string nonce)
        { using (HMACSHA256 mac = new HMACSHA256(Encoding.UTF8.GetBytes(code))) return Hex(mac.ComputeHash(Encoding.UTF8.GetBytes(nonce))); }
        public static string HashText(string text)
        { using (SHA256 hash = SHA256.Create()) return Hex(hash.ComputeHash(Encoding.UTF8.GetBytes(text))); }
        public static string HashFile(string path, Action check, Action<long> progress)
        { using (FileStream file = File.OpenRead(path)) return HashStream(file, file.Length, check); }
        public static string HashStream(Stream stream, long length, Action check)
        {
            stream.Position = 0;
            using (SHA256 sha = SHA256.Create())
            {
                byte[] buffer = NewBuffer(length); long done = 0;
                while (done < length)
                {
                    if (check != null) check();
                    int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, length - done));
                    if (read == 0) throw new EndOfStreamException();
                    sha.TransformBlock(buffer, 0, read, buffer, 0); done += read;
                }
                sha.TransformFinalBlock(new byte[0], 0, 0); return Hex(sha.Hash);
            }
        }
        public static string SafePath(string root, string relative) { return SafePath(root, relative, false); }
        public static string SafePath(string root, string relative, bool internalPath)
        {
            if (String.IsNullOrEmpty(relative) || Path.IsPathRooted(relative)) throw new IOException("无效文件路径。");
            string[] pieces = relative.Replace('\\', '/').Split('/');
            foreach (string part in pieces)
            {
                if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".") || part.EndsWith(" ") || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new IOException("不支持的文件名：" + relative);
                string stem = part.Split('.')[0].ToUpperInvariant();
                if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" || stem == "CLOCK$" ||
                    (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789".IndexOf(stem[3]) >= 0)) throw new IOException("Windows 保留文件名。");
            }
            if (!internalPath && pieces[0].Equals(".lantransfer-cache", StringComparison.OrdinalIgnoreCase)) throw new IOException("此目录名为程序保留。");
            string basePath = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(Path.Combine(basePath, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(basePath, StringComparison.OrdinalIgnoreCase)) throw new IOException("路径超出接收目录。");
            CheckLinks(full); return full;
        }
        public static void CheckLinks(string path)
        {
            string current = Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("不支持符号链接或目录联接：" + current);
                current = Path.GetDirectoryName(current);
            }
        }
        public static string Size(long bytes)
        { string[] units = { "B", "KB", "MB", "GB", "TB" }; double value = bytes; int i = 0; while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; } return value.ToString("0.##") + " " + units[i]; }
        public static string[] LocalAddresses()
        {
            List<string> addresses = new List<string>();
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (UnicastIPAddressInformation ip in nic.GetIPProperties().UnicastAddresses)
                    if (ip.Address.AddressFamily == AddressFamily.InterNetwork) addresses.Add(ip.Address.ToString());
            }
            return addresses.ToArray();
        }
        public static string[] Discover()
        {
            HashSet<string> peers = new HashSet<string>();
            using (UdpClient udp = new UdpClient(0))
            {
                udp.EnableBroadcast = true; udp.Client.ReceiveTimeout = 300;
                byte[] query = Encoding.UTF8.GetBytes("LANTRANSFER_DISCOVER_1");
                HashSet<string> broadcasts = new HashSet<string>(); broadcasts.Add("255.255.255.255");
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                    if (nic.OperationalStatus == OperationalStatus.Up)
                        foreach (UnicastIPAddressInformation ip in nic.GetIPProperties().UnicastAddresses)
                            if (ip.Address.AddressFamily == AddressFamily.InterNetwork && ip.IPv4Mask != null)
                            {
                                byte[] a = ip.Address.GetAddressBytes(), m = ip.IPv4Mask.GetAddressBytes();
                                for (int i = 0; i < 4; i++) a[i] |= (byte)~m[i];
                                broadcasts.Add(new IPAddress(a).ToString());
                            }
                foreach (string address in broadcasts)
                    for (int port = TransferServer.DiscoveryPort; port < TransferServer.DiscoveryPort + TransferServer.DiscoveryPortCount; port++)
                        try { udp.Send(query, query.Length, new IPEndPoint(IPAddress.Parse(address), port)); } catch (SocketException) { }
                Stopwatch clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < 2000)
                {
                    try
                    {
                        IPEndPoint peer = new IPEndPoint(IPAddress.Any, 0);
                        string reply = Encoding.UTF8.GetString(udp.Receive(ref peer));
                        string[] fields = reply.Split('|'); int port;
                        if (fields.Length == 3 && fields[0] == "LANTRANSFER_1" && Int32.TryParse(fields[1], out port) && port > 0 && port <= 65535)
                            peers.Add(peer.Address + ":" + port + "  " + fields[2]);
                    }
                    catch (SocketException) { }
                }
            }
            string[] result = new string[peers.Count]; peers.CopyTo(result); return result;
        }
    }
}
