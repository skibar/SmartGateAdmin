
using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Text;

namespace smartgate_wpf_adm.Services
{
    public class SerialTransport : IDisposable
    {
        private SerialPort? _serialPort;
        private readonly object _bufferLock = new();
        private readonly List<byte> _receiveBuffer = new();

        public event Action<string>? DataReceived;
        public event Action<string>? ErrorOccurred;

        public bool IsConnected => _serialPort?.IsOpen == true;

        public string[] GetAvailablePorts()
        {
            return SerialPort.GetPortNames();
        }

        public void Connect(string portName, int baudRate = 115200)
        {
            Disconnect();

            lock (_bufferLock)
            {
                _receiveBuffer.Clear();
            }

            var port = new SerialPort(portName, baudRate)
            {
                ReadTimeout = 1000,
                WriteTimeout = 1000,
                Handshake = Handshake.None,
                DtrEnable = false,
                RtsEnable = false,
                ReceivedBytesThreshold = 1
            };

            port.DataReceived += OnPortDataReceived;

            try
            {
                port.Open();
                _serialPort = port;
            }
            catch
            {
                port.DataReceived -= OnPortDataReceived;
                port.Dispose();
                throw;
            }
        }

        private void OnPortDataReceived(
            object sender,
            SerialDataReceivedEventArgs e)
        {
            if (sender is not SerialPort port)
                return;

            try
            {
                int available = port.BytesToRead;

                if (available <= 0)
                    return;

                byte[] bytes = new byte[available];
                int count = port.Read(bytes, 0, bytes.Length);

                var completedLines = new List<string>();

                lock (_bufferLock)
                {
                    for (int i = 0; i < count; i++)
                    {
                        byte b = bytes[i];

                        if (b == (byte)'\n')
                        {
                            // Ta bort CR om raden avslutas med CR+LF.
                            if (_receiveBuffer.Count > 0 &&
                                _receiveBuffer[^1] == (byte)'\r')
                            {
                                _receiveBuffer.RemoveAt(
                                    _receiveBuffer.Count - 1);
                            }

                            string line = Encoding.UTF8.GetString(
                                _receiveBuffer.ToArray());

                            _receiveBuffer.Clear();

                            if (line.Length > 0)
                                completedLines.Add(line);
                        }
                        else
                        {
                            _receiveBuffer.Add(b);
                        }
                    }
                }

                foreach (string line in completedLines)
                {
                    DataReceived?.Invoke(line);
                }
            }
            catch (Exception ex)
            {
                if (IsConnected)
                    ErrorOccurred?.Invoke(ex.Message);
            }
        }

        public void Send(string data)
        {
            if (_serialPort?.IsOpen != true)
            {
                throw new InvalidOperationException(
                    "Ingen COM-port är ansluten.");
            }

            _serialPort.Write(data);
        }

        public void Disconnect()
        {
            var port = _serialPort;
            _serialPort = null;

            if (port != null)
            {
                port.DataReceived -= OnPortDataReceived;

                try
                {
                    if (port.IsOpen)
                        port.Close();
                }
                finally
                {
                    port.Dispose();
                }
            }

            lock (_bufferLock)
            {
                _receiveBuffer.Clear();
            }
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}
