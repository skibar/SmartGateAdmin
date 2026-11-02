
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace smartgate_wpf_adm.Services
{
    public sealed class TcpTransport : IDisposable
    {
        private TcpClient? _client;
        private NetworkStream? _stream;
        private CancellationTokenSource? _receiveCancellation;

        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly object _otaLock = new();

        private TaskCompletionSource<string>? _otaResponseWaiter;
        private int _uploadingFirmware;

        public event Action<string>? DataReceived;
        public event Action<string>? ErrorOccurred;

        public bool IsConnected =>
            _client?.Connected == true && _stream != null;

        public async Task ConnectAsync(
            string host,
            int port,
            string token)
        {
            Disconnect();

            var client = new TcpClient();

            try
            {
                await client.ConnectAsync(host, port)
                    .WaitAsync(TimeSpan.FromSeconds(5));

                NetworkStream stream = client.GetStream();
                stream.ReadTimeout = 5000;

                // Läs välkomstmeddelandet.
                using var timeout =
                    new CancellationTokenSource(
                        TimeSpan.FromSeconds(5));

                string? greeting =
                    await ReadLineAsync(stream, timeout.Token);

                if (string.IsNullOrWhiteSpace(greeting))
                {
                    throw new IOException(
                        "SmartGate skickade inget välkomstmeddelande.");
                }

                // Autentisera.
                byte[] authBytes =
                    Encoding.UTF8.GetBytes($"AUTH {token}\n");

                await stream.WriteAsync(
                    authBytes.AsMemory(),
                    timeout.Token);

                string? authResponse =
                    await ReadLineAsync(stream, timeout.Token);

                if (!string.Equals(
                    authResponse,
                    "OK AUTHENTICATED",
                    StringComparison.Ordinal))
                {
                    throw new IOException(
                        $"SmartGate avvisade autentiseringen: {authResponse}");
                }

                client.NoDelay = true;

                _client = client;
                _stream = stream;
                _receiveCancellation =
                    new CancellationTokenSource();

                _ = ReceiveLoopAsync(
                    stream,
                    _receiveCancellation.Token);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public void Send(string data)
        {
            if (Volatile.Read(ref _uploadingFirmware) != 0)
            {
                throw new InvalidOperationException(
                    "Kan inte skicka textkommandon under firmwareöverföring.");
            }

            _writeLock.Wait();

            try
            {
                if (!IsConnected || _stream == null)
                {
                    throw new InvalidOperationException(
                        "Ingen TCP-anslutning är aktiv.");
                }

                if (Volatile.Read(ref _uploadingFirmware) != 0)
                {
                    throw new InvalidOperationException(
                        "Firmwareöverföring pågår.");
                }

                byte[] bytes = Encoding.UTF8.GetBytes(data);
                _stream.Write(bytes, 0, bytes.Length);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public async Task UploadFirmwareAsync(
            string filePath,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.CompareExchange(
                ref _uploadingFirmware, 1, 0) != 0)
            {
                throw new InvalidOperationException(
                    "En firmwareöverföring pågår redan.");
            }

            bool commandSent = false;
            TaskCompletionSource<string>? waiter = null;

            try
            {
                if (!IsConnected || _stream == null)
                {
                    throw new InvalidOperationException(
                        "Anslut till SmartGate via Wi-Fi först.");
                }

                using var file = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    useAsync: true);

                long fileSize = file.Length;

                if (fileSize <= 0 || fileSize > uint.MaxValue)
                {
                    throw new InvalidOperationException(
                        "Firmwarefilens storlek är ogiltig.");
                }

                // Beräkna MD5 utan att läsa hela filen till minnet.
                using var md5 = MD5.Create();

                byte[] hash = await md5.ComputeHashAsync(
                    file, cancellationToken);

                string md5Hex = Convert.ToHexString(hash)
                    .ToLowerInvariant();

                file.Position = 0;

                NetworkStream stream = _stream;

                // Vänta på READY innan binärdata skickas.
                waiter = CreateOtaWaiter();

                string header =
                    $"OTA_BEGIN {fileSize} {md5Hex}\n";

                await WriteBytesAsync(
                    stream,
                    Encoding.ASCII.GetBytes(header),
                    cancellationToken);

                commandSent = true;

                string ready = await waiter.Task.WaitAsync(
                    TimeSpan.FromSeconds(15),
                    cancellationToken);

                ClearOtaWaiter(waiter);
                waiter = null;

                if (ready != "READY")
                {
                    throw new IOException(
                        $"SmartGate avvisade firmwareöverföringen: {ready}");
                }

                // Vänta på slutligt svar medan filen skickas.
                waiter = CreateOtaWaiter();

                byte[] buffer = new byte[4096];
                long sent = 0;
                int lastPercent = -1;

                while (sent < fileSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int wanted = (int)Math.Min(
                        buffer.Length,
                        fileSize - sent);

                    int read = await file.ReadAsync(
                        buffer.AsMemory(0, wanted),
                        cancellationToken);

                    if (read == 0)
                    {
                        throw new EndOfStreamException(
                            "Firmwarefilen tog slut före överföringen.");
                    }

                    await WriteBytesAsync(
                        stream,
                        buffer.AsMemory(0, read),
                        cancellationToken);

                    sent += read;

                    int percent = (int)(sent * 100L / fileSize);

                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress?.Report(percent);
                    }
                }

                string result = await waiter.Task.WaitAsync(
                    TimeSpan.FromMinutes(3),
                    cancellationToken);

                ClearOtaWaiter(waiter);
                waiter = null;

                if (result != "OK OTA")
                {
                    throw new IOException(
                        $"SmartGate kunde inte installera firmware: {result}");
                }
            }
            catch
            {
                // Efter att OTA-kommandot skickats är det säkrast
                // att stänga anslutningen vid fel eller avbrott.
                if (commandSent)
                {
                    Disconnect();
                }

                throw;
            }
            finally
            {
                if (waiter != null)
                {
                    ClearOtaWaiter(waiter);
                }

                Interlocked.Exchange(ref _uploadingFirmware, 0);
            }
        }

        private async Task WriteBytesAsync(
            NetworkStream stream,
            ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken)
        {
            await _writeLock.WaitAsync(cancellationToken);

            try
            {
                if (!ReferenceEquals(stream, _stream))
                {
                    throw new IOException(
                        "TCP-anslutningen har stängts.");
                }

                await stream.WriteAsync(bytes, cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private TaskCompletionSource<string> CreateOtaWaiter()
        {
            lock (_otaLock)
            {
                if (_otaResponseWaiter != null)
                {
                    throw new InvalidOperationException(
                        "Väntar redan på ett OTA-svar.");
                }

                _otaResponseWaiter =
                    new TaskCompletionSource<string>(
                        TaskCreationOptions.RunContinuationsAsynchronously);

                return _otaResponseWaiter;
            }
        }

        private void ClearOtaWaiter(
            TaskCompletionSource<string> waiter)
        {
            lock (_otaLock)
            {
                if (ReferenceEquals(_otaResponseWaiter, waiter))
                {
                    _otaResponseWaiter = null;
                }
            }
        }

        private void HandleReceivedLine(string line)
        {
            DataReceived?.Invoke(line);

            TaskCompletionSource<string>? waiter = null;

            lock (_otaLock)
            {
                if (_otaResponseWaiter != null &&
                    (line == "READY" ||
                     line == "OK OTA" ||
                     line.StartsWith(
                         "ERR ",
                         StringComparison.OrdinalIgnoreCase)))
                {
                    waiter = _otaResponseWaiter;
                }
            }

            waiter?.TrySetResult(line);
        }

        private async Task ReceiveLoopAsync(
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            var lineBuffer = new List<byte>();
            byte[] buffer = new byte[1024];

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    int count = await stream.ReadAsync(
                        buffer.AsMemory(),
                        cancellationToken);

                    if (count == 0)
                    {
                        FailOtaWaiter(
                            new IOException(
                                "TCP-anslutningen stängdes av SmartGate."));

                        break;
                    }

                    for (int i = 0; i < count; i++)
                    {
                        byte b = buffer[i];

                        if (b == (byte)'\n')
                        {
                            if (lineBuffer.Count > 0 &&
                                lineBuffer[^1] == (byte)'\r')
                            {
                                lineBuffer.RemoveAt(
                                    lineBuffer.Count - 1);
                            }

                            string line = Encoding.UTF8.GetString(
                                lineBuffer.ToArray());

                            lineBuffer.Clear();

                            if (line.Length > 0)
                            {
                                HandleReceivedLine(line);
                            }
                        }
                        else
                        {
                            lineBuffer.Add(b);

                            if (lineBuffer.Count > 4096)
                            {
                                lineBuffer.Clear();

                                ErrorOccurred?.Invoke(
                                    "TCP-svarsrad var längre än tillåtet.");
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal frånkoppling.
            }
            catch (ObjectDisposedException)
            {
                // Strömmen stängdes vid frånkoppling.
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    ErrorOccurred?.Invoke(ex.Message);
                }
            }
        }

        private void FailOtaWaiter(Exception ex)
        {
            TaskCompletionSource<string>? waiter;

            lock (_otaLock)
            {
                waiter = _otaResponseWaiter;
            }

            waiter?.TrySetException(ex);
        }


        private static async Task<string?> ReadLineAsync(
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            var bytes = new List<byte>();
            byte[] oneByte = new byte[1];

            while (true)
            {
                int count = await stream.ReadAsync(
                    oneByte.AsMemory(),
                    cancellationToken);

                if (count == 0)
                {
                    return bytes.Count == 0
                        ? null
                        : Encoding.UTF8.GetString(bytes.ToArray());
                }

                if (oneByte[0] == (byte)'\n')
                {
                    if (bytes.Count > 0 &&
                        bytes[^1] == (byte)'\r')
                    {
                        bytes.RemoveAt(bytes.Count - 1);
                    }

                    return Encoding.UTF8.GetString(bytes.ToArray());
                }

                bytes.Add(oneByte[0]);

                if (bytes.Count > 4096)
                {
                    throw new IOException(
                        "TCP-svarsrad var längre än tillåtet.");
                }
            }
        }

        public void Disconnect()
        {
            _receiveCancellation?.Cancel();
            _receiveCancellation?.Dispose();
            _receiveCancellation = null;

            _stream?.Dispose();
            _client?.Close();
            _client?.Dispose();

            _stream = null;
            _client = null;
        }

        public void Dispose()
        {
            Disconnect();
            _writeLock.Dispose();
        }
    }
}
