
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using smartgate_wpf_adm.models;
using smartgate_wpf_adm.Services;
using System.Windows.Threading;
using System.IO;
using System.Text.Json;


namespace smartgate_wpf_adm
{
    public partial class MainWindow : Window
    {
        private readonly SerialTransport _serialTransport = new();
        private readonly TcpTransport _tcpTransport = new();
        private readonly List<string> _settingsResponseLines = new();

        private GateSettings? _loadedSettings;
        private bool _isReadingSettings;
        private bool _isSavingSettings;
        private bool _settingsResponseStarted;

        private readonly DispatcherTimer _overviewRefreshTimer = new();
        private bool _overviewRefreshOnly;
        private const int MaxLogRows = 1000;

        public MainWindow()
        {
            InitializeComponent();

            LoadAppConfig();

            UpdateConnectionMode();
            // UpdateConnectionButtons();

            // prenumerera på seriella
            _serialTransport.DataReceived += OnDataReceived;
            _serialTransport.ErrorOccurred += OnSerialError;
            // prenumerera på TCP
            _tcpTransport.DataReceived += OnDataReceived;
            _tcpTransport.ErrorOccurred += OnTcpError;

            RefreshPorts();

            _overviewRefreshTimer.Interval = TimeSpan.FromSeconds(60);
            _overviewRefreshTimer.Tick += OverviewRefreshTimer_Tick;
            _overviewRefreshTimer.Start();

        }

        private void ConnectionTypeComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            UpdateConnectionMode();
        }

        private void UpdateConnectionMode()
        {
            // Skydda mot att händelsen körs innan alla kontroller skapats.
            if (UsbConnectionPanel == null || WifiConnectionPanel == null)
                return;

            bool useWifi = ConnectionTypeComboBox.SelectedIndex == 1;

            UsbConnectionPanel.Visibility = useWifi
                ? Visibility.Collapsed
                : Visibility.Visible;

            WifiConnectionPanel.Visibility = useWifi
                ? Visibility.Visible
                : Visibility.Collapsed;
        }


        private void RefreshPorts()
        {
            string? selectedPort = PortComboBox.SelectedItem as string;

            PortComboBox.ItemsSource =
                _serialTransport.GetAvailablePorts();

            if (selectedPort != null &&
                PortComboBox.Items.Contains(selectedPort))
            {
                PortComboBox.SelectedItem = selectedPort;
            }
            else if (PortComboBox.Items.Count > 0)
            {
                PortComboBox.SelectedIndex = 0;
            }
        }

        private void SearchPorts_Click(object sender, RoutedEventArgs e)
        {
            RefreshPorts();
            AppendLog("Sökte efter COM-portar.");
        }

        /// <summary>
        /// Läser in TCP värden så de är förifyllda
        /// </summary>
        private void LoadAppConfig()
        {
            string path = Path.Combine(
                AppContext.BaseDirectory,
                "wpf_adm_config.json");

            if (!File.Exists(path))
            {
                MessageBox.Show(
                    $"Konfigurationsfilen hittades inte:\n{path}");
                return;
            }

            try
            {
                string json = File.ReadAllText(path);

                var config = JsonSerializer.Deserialize<AppConfig>(json);

                if (config == null)
                    throw new InvalidDataException("Konfigurationen är tom.");

                WifiIpTextBox.Text = config.IpAddress;
                WifiPortTextBox.Text = config.Port.ToString();
                WifiTokenBox.Password = config.Token;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Kunde inte läsa konfigurationen:\n{ex.Message}");
            }
        }



        private void OverviewRefreshTimer_Tick(object? sender, EventArgs e)
        {
            if (_isReadingSettings || _isSavingSettings)
                return;

            bool useTcp = ConnectionTypeComboBox.SelectedIndex == 1;

            bool connected = useTcp
                ? _tcpTransport.IsConnected
                : _serialTransport.IsConnected;

            if (!connected)
                return;

            _overviewRefreshOnly = true;
            LoadAllSettings();
        }



        private async void Connect_Click(object sender, RoutedEventArgs e)
        {
            // Wi-Fi / TCP
            if (ConnectionTypeComboBox.SelectedIndex == 1)
            {
                string host = WifiIpTextBox.Text.Trim();
                string token = WifiTokenBox.Password;

                if (string.IsNullOrWhiteSpace(host))
                {
                    MessageBox.Show("Ange SmartGates IP-adress.");
                    return;
                }

                if (!int.TryParse(WifiPortTextBox.Text.Trim(), out int port)
                    || port < 1 || port > 65535)
                {
                    MessageBox.Show("Ange en giltig TCP-port.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(token))
                {
                    MessageBox.Show("Ange token.");
                    return;
                }

                try
                {
                    // Vi använder en anslutning åt gången.
                    _serialTransport.Disconnect();

                    StatusText.Text = "Status: Ansluter via Wi-Fi...";
                    AppendLog($"Ansluter via TCP till {host}:{port}.");

                    await _tcpTransport.ConnectAsync(host, port, token);
                    UpdateConnectionButtons();

                    StatusText.Text = $"Status: Ansluten via Wi-Fi ({host})";
                    AppendLog("TCP-anslutning och autentisering lyckades.");
                    LoadAllSettings();
                }
                catch (Exception ex)
                {
                    _tcpTransport.Disconnect();

                    StatusText.Text = "Status: Wi-Fi-anslutning misslyckades";
                    AppendLog($"TCP-ANSLUTNINGSFEL: {ex.Message}");

                    MessageBox.Show(
                        ex.Message,
                        "SmartGate",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }

                return;
            }

            // USB / COM – befintlig funktion
            if (PortComboBox.SelectedItem is not string portName)
            {
                MessageBox.Show("Välj en COM-port först.");
                return;
            }

            try
            {
                _tcpTransport.Disconnect();

                _serialTransport.Connect(portName, 115200);
                UpdateConnectionButtons();
                StatusText.Text = $"Status: Ansluten till {portName}";
                AppendLog($"Ansluten till {portName}, 115200 baud.");
            }
            catch (Exception ex)
            {
                StatusText.Text = "Status: Anslutning misslyckades";
                AppendLog($"ANSLUTNINGSFEL: {ex.Message}");

                MessageBox.Show(
                    ex.Message,
                    "SmartGate",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }


        private void LoadAllSettings()
        {
            if (ConnectionTypeComboBox.SelectedIndex == 1)
            {
                if (!_tcpTransport.IsConnected)
                    return;
            }
            else
            {
                if (!_serialTransport.IsConnected)
                    return;
            }

            _settingsResponseLines.Clear();
            _settingsResponseStarted = false;
            _isReadingSettings = true;

            try
            {
                if (ConnectionTypeComboBox.SelectedIndex == 1)
                    _tcpTransport.Send("getsettings\n");
                else
                    _serialTransport.Send("getsettings\n");

                AppendLog("Inställningar hämtas automatiskt från SmartGate.");
                StatusText.Text = "Status: Läser inställningar...";
            }
            catch (Exception ex)
            {
                _isReadingSettings = false;
                AppendLog($"FEL VID LÄSNING AV INSTÄLLNINGAR: {ex.Message}");
                StatusText.Text = "Status: Kunde inte läsa inställningar";
            }
        }

        private void Disconnect_Click(object sender, RoutedEventArgs e)
        {
            _isReadingSettings = false;

            _serialTransport.Disconnect();
            _tcpTransport.Disconnect();

            UpdateConnectionButtons();

            StatusText.Text = "Status: Ej ansluten";
            AppendLog("Frånkopplad.");
        }

        private void LoadSettings_Click(object sender, RoutedEventArgs e)
        {
            LoadAllSettings();
        }

        private async void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            bool useTcp = ConnectionTypeComboBox.SelectedIndex == 1;

            if (useTcp && !_tcpTransport.IsConnected)
            {
                MessageBox.Show("Anslut till SmartGate via Wi-Fi först.");
                return;
            }

            if (!useTcp && !_serialTransport.IsConnected)
            {
                MessageBox.Show("Anslut till SmartGate via USB först.");
                return;
            }

            if (_loadedSettings == null)
            {
                MessageBox.Show("Läs inställningarna från SmartGate först.");
                return;
            }
            int irExists = chkIrExists.IsChecked == true ? 1 : 0;

            if (_isSavingSettings || _isReadingSettings)
                return;

            if (!TryGetInt(txtGateId.Text, out int gateId) || gateId <= 0 ||
                !TryGetInt(txtSiteId.Text, out int siteId) || siteId <= 0 ||
                !TryGetInt(txtVersion.Text, out int version) || version <= 0 ||
                !TryGetInt(txtLedDuration.Text, out int ledDuration) || ledDuration <= 0 ||
                !TryGetInt(txtRfidAuthTimeout.Text, out int rfidAuthTimeout) || rfidAuthTimeout <= 0 || rfidAuthTimeout > 60000)
            //!TryGetInt(txtIrExists.Text, out int irExists) || (irExists != 0 && irExists != 1)
            {
                MessageBox.Show(
                    "Kontrollera Gate ID, Site ID, programversion, LED-duration och IR-gate.\n" +
                    "ID och version ska vara positiva heltal. LED-duration ska vara positiv. " +
                    "IR-gate ska vara 0 eller 1.",
                    "Ogiltiga värden",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            string dateTime = txtDateTime.Text.Trim();

            if (dateTime.Length > 0 &&
                !DateTime.TryParseExact(
                    dateTime,
                    "yyyy-MM-dd HH:mm",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out _))
            {
                MessageBox.Show(
                    "Datum och tid måste anges som YYYY-MM-DD HH:mm.",
                    "Ogiltigt datum",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            string wifi = txtWifi.Text.Trim();
            string password = txtPassword.Text;
            string server = txtServer.Text.Trim();
            string database = txtDatabase.Text.Trim();

            if (wifi.Contains(','))
            {
                MessageBox.Show(
                    "Wi-Fi-namnet kan inte innehålla kommatecken med nuvarande firmwarekommando.");
                return;
            }

            var commands = new List<(string Command, bool Sensitive, bool Restart)>();

            if (gateId != _loadedSettings.GateId)
                commands.Add(($"setgatenr {gateId}", false, false));

            if (siteId != _loadedSettings.SiteId)
                commands.Add(($"setport {siteId}", false, false));

            if (!string.Equals(server, _loadedSettings.Server, StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(server))
                {
                    MessageBox.Show("Serveradressen får inte vara tom.");
                    return;
                }

                commands.Add(($"setbaseurl {server}", false, false));
            }

            if (!string.Equals(database, _loadedSettings.Database, StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(database))
                {
                    MessageBox.Show("Databasnamnet får inte vara tomt.");
                    return;
                }

                commands.Add(($"setdatabase {database}", false, false));
            }

            if (version != _loadedSettings.Version)
                commands.Add(($"setversion {version}", false, false));

            if (ledDuration != _loadedSettings.LedDuration)
                commands.Add(($"setreldur {ledDuration}", false, false));

            if (rfidAuthTimeout != _loadedSettings.RfidAuthTimeout)
                commands.Add(($"setauthtime {rfidAuthTimeout}", false, false));

            if (irExists != _loadedSettings.IrExists)
                commands.Add(($"irexist {irExists}", false, false));

            if (dateTime.Length > 0)
                commands.Add(($"setdate {dateTime}", false, false));

            bool wifiChanged =
                !string.Equals(wifi, _loadedSettings.Wifi, StringComparison.Ordinal) ||
                !string.Equals(password, _loadedSettings.Password, StringComparison.Ordinal);

            if (wifiChanged)
            {
                if (string.IsNullOrWhiteSpace(wifi))
                {
                    MessageBox.Show("Wi-Fi-namnet får inte vara tomt.");
                    return;
                }

                // Måste ligga sist eftersom SmartGate startar om.
                commands.Add(($"setwifi {wifi},{password}", true, true));
            }

            if (commands.Count == 0)
            {
                AppendLog("Inga ändringar att spara.");
                StatusText.Text = "Status: Inga ändringar";
                return;
            }

            _isSavingSettings = true;

            try
            {
                foreach (var item in commands)
                {
                    string data = item.Command + "\n";

                    if (useTcp)
                        _tcpTransport.Send(data);
                    else
                        _serialTransport.Send(data);

                    // Logga aldrig Wi-Fi-lösenordet.
                    AppendLog(item.Sensitive
                        ? $"TX ({(useTcp ? "TCP" : "USB")}): setwifi [lösenord dolt]"
                        : $"TX ({(useTcp ? "TCP" : "USB")}): {item.Command}");

                    await Task.Delay(350);
                }

                if (wifiChanged)
                {
                    StatusText.Text = "Status: Wi-Fi sparas, SmartGate startar om";
                    AppendLog("Wi-Fi-ändringen skickad. SmartGate startar om.");
                }
                else
                {
                    StatusText.Text = "Status: Ändringar skickade";

                    // Läs tillbaka inställningarna för verifiering.
                    _settingsResponseLines.Clear();
                    _settingsResponseStarted = false;
                    _isReadingSettings = true;

                    if (useTcp)
                        _tcpTransport.Send("getsettings\n");
                    else
                        _serialTransport.Send("getsettings\n");

                    AppendLog($"TX ({(useTcp ? "TCP" : "USB")}): getsettings [verifiering]");
                }
            }
            catch (Exception ex)
            {
                _isReadingSettings = false;
                StatusText.Text = "Status: Fel vid sparning";
                AppendLog($"SPARFEL: {ex.Message}");

                MessageBox.Show(
                    ex.Message,
                    "Fel vid sparning",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                _isSavingSettings = false;
            }
        }


        private async void LoadNewFirmware_Click(
            object sender,
            RoutedEventArgs e)
        {

            var firmwareButton = sender as System.Windows.Controls.Button;
            if (firmwareButton != null) firmwareButton.IsEnabled = false;

            // Firmware skickas via TCP, inte USB.
            if (ConnectionTypeComboBox.SelectedIndex != 1 ||
                !_tcpTransport.IsConnected)
            {
                MessageBox.Show(
                    "Anslut till SmartGate via Wi-Fi först.",
                    "Firmwareuppdatering",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                return;
            }

            if (_isReadingSettings || _isSavingSettings)
            {
                MessageBox.Show(
                    "Vänta tills pågående kommunikation är klar.",
                    "Firmwareuppdatering",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                return;
            }

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Välj firmware till SmartGate",
                Filter = "ESP32 firmware (*.bin)|*.bin",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog(this) != true)
                return;

            var fileInfo = new FileInfo(dialog.FileName);

            MessageBoxResult answer = MessageBox.Show(
                $"Vill du installera denna firmware på SmartGate?\n\n" +
                $"Fil: {fileInfo.Name}\n" +
                $"Storlek: {fileInfo.Length:N0} byte\n\n" +
                "Enheten startar om efter uppdateringen. " +
                "Kontrollera att Smartgate inte används.",
                "Bekräfta firmwareuppdatering",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes)
                return;

            if (sender is System.Windows.Controls.Button button)
                button.IsEnabled = false;

            _overviewRefreshTimer.Stop();

            try
            {
                MainTabControl.SelectedIndex = 2;

                StatusText.Text = "Status: Uppdaterar firmware...";
                AppendLog($"Lokal firmware vald: {fileInfo.Name}");
                AppendLog("Startar firmwareöverföring via TCP.");

                var progress = new Progress<int>(percent =>
                {
                    StatusText.Text =
                        $"Status: Skickar firmware... {percent}%";

                    if (percent == 100 || percent % 10 == 0)
                        AppendLog($"Firmwareöverföring: {percent}%");

                });

                await _tcpTransport.UploadFirmwareAsync(
                    dialog.FileName,
                    progress);

                AppendLog(
                    "ESP32 bekräftade att firmware installerats.");

                // ESP32 startar om. Stäng den gamla anslutningen
                // så att användaren kan ansluta igen efter omstarten.
                _tcpTransport.Disconnect();

                UpdateConnectionButtons();

                StatusText.Text =
                    "Status: Firmware installerad – SmartGate startar om";

                MessageBox.Show(
                    "SmartGate bekräftade installationen.\n\n" +
                    "Enheten startar nu om. Anslut via Wi-Fi igen " +
                    "när den har startat.",
                    "Firmwareuppdatering",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    "Status: Firmwareuppdateringen misslyckades";

                AppendLog(
                    $"FEL VID FIRMWAREUPPDATERING: {ex.Message}");

                UpdateConnectionButtons();

                MessageBox.Show(
                    ex.Message,
                    "Firmwareuppdatering misslyckades",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                if (firmwareButton != null)
                    firmwareButton.IsEnabled = true;
                // Återuppta automatisk statusuppdatering om
                // TCP-anslutningen fortfarande är aktiv.
                if (_tcpTransport.IsConnected)
                    _overviewRefreshTimer.Start();
            }
        }


        private static bool TryGetInt(string text, out int value)
        {
            return int.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value);
        }

        private void OnDataReceived(string data)
        {
            Dispatcher.InvokeAsync(() =>
            {
                AppendLog($"RX: {data}");

                if (!_isReadingSettings)
                    return;

                if (!_settingsResponseStarted)
                {
                    if (data.Contains("Nuvarande inställningar", StringComparison.OrdinalIgnoreCase))
                    {
                        _settingsResponseStarted = true;
                        _settingsResponseLines.Clear();
                        _settingsResponseLines.Add(data);
                    }

                    return;
                }

                if (data.Trim().Length >= 10 &&
                    data.Trim().All(c => c == '='))
                {
                    _isReadingSettings = false;

                    _loadedSettings = GateSettingsParser.Parse(_settingsResponseLines);

                    if (!_overviewRefreshOnly)
                        ShowSettings(_loadedSettings);

                    ShowOverview(_loadedSettings);
                    _overviewRefreshOnly = false;

                    StatusText.Text = "Status: Inställningar inlästa";
                    AppendLog("Inställningarna har lästs in.");
                    StatusText.Text = "Status: Systemet är uppkopplat";
                    return;
                }

                _settingsResponseLines.Add(data);
            });
        }

        private void ShowSettings(GateSettings settings)
        {
            txtGateId.Text = settings.GateId.ToString(CultureInfo.InvariantCulture);
            txtSiteId.Text = settings.SiteId.ToString(CultureInfo.InvariantCulture);
            txtVersion.Text = settings.Version.ToString(CultureInfo.InvariantCulture);
            txtLedDuration.Text = settings.LedDuration.ToString(CultureInfo.InvariantCulture);
            txtRfidAuthTimeout.Text = settings.RfidAuthTimeout.ToString(CultureInfo.InvariantCulture);
            txtWifi.Text = settings.Wifi;
            txtPassword.Text = settings.Password;
            txtServer.Text = settings.Server;
            txtDatabase.Text = settings.Database;
            //txtIrExists.Text = settings.IrExists.ToString(CultureInfo.InvariantCulture);
            chkIrExists.IsChecked = settings.IrExists == 1;
            // Tiden från firmware har ett annat format än setdate-kommandot.
            txtDateTime.Clear();

            // Uppdatera flik 1 med samma avläsning.
            ShowOverview(settings);
        }

        private void ShowOverview(GateSettings settings)
        {
            OverviewGateIdText.Text =
                settings.GateId.ToString(CultureInfo.InvariantCulture);

            OverviewSiteIdText.Text =
                settings.SiteId.ToString(CultureInfo.InvariantCulture);

            OverviewGateStateText.Text = settings.GateState;
            OverviewWifiText.Text = settings.Wifi;
            OverviewWifiSignalText.Text = settings.WifiSignal;
            OverviewIpText.Text = settings.LocalIp;
            OverviewServerStatusText.Text = settings.ExternalStatus;

            OverviewGreenText.Text =
                settings.GreenPassages.ToString(CultureInfo.InvariantCulture);

            OverviewRedText.Text =
                settings.RedPassages.ToString(CultureInfo.InvariantCulture);

            OverviewVersionText.Text =
                settings.Version.ToString(CultureInfo.InvariantCulture);

            OverviewConfigVersionText.Text =
                settings.ConfigVersion.ToString(CultureInfo.InvariantCulture);

            OverviewUpdatedText.Text =
                $"Senast uppdaterad: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        }

        private void ClearOverview()
        {
            OverviewGateIdText.Text = "–";
            OverviewSiteIdText.Text = "–";
            OverviewGateStateText.Text = "Inväntar svar...";
            OverviewWifiText.Text = "–";
            OverviewWifiSignalText.Text = "–";
            OverviewIpText.Text = "–";
            OverviewServerStatusText.Text = "–";
            OverviewGreenText.Text = "–";
            OverviewRedText.Text = "–";
            OverviewVersionText.Text = "–";
            OverviewConfigVersionText.Text = "–";
            OverviewUpdatedText.Text = "Läser status från SmartGate...";
        }

        private void OnSerialError(string message)
        {
            Dispatcher.InvokeAsync(() =>
            {
                _isReadingSettings = false;

                _serialTransport.Disconnect();

                StatusText.Text = "Status: Kommunikationsfel";
                AppendLog($"KOMMUNIKATIONSFEL: {message}");

                UpdateConnectionButtons();
            });
        }
        private void OnTcpError(string message)
        {
            Dispatcher.InvokeAsync(() =>
            {
                _isReadingSettings = false;

                _tcpTransport.Disconnect();

                StatusText.Text = "Status: TCP-kommunikationsfel";
                AppendLog($"TCP-FEL: {message}");

                UpdateConnectionButtons();
            });
        }

        private void Send_Click(object sender, RoutedEventArgs e)
        {
            string command = CommandTextBox.Text.Trim();

            if (ConnectionTypeComboBox.SelectedIndex == 1)
            {

                if (!string.Equals(
                        command,
                        "getsettings",
                        StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(
                        "TCP-testversionen stöder just nu bara getsettings.");
                    return;
                }

                if (!_tcpTransport.IsConnected)
                {
                    MessageBox.Show("Anslut via Wi-Fi först.");
                    return;
                }

                try
                {
                    _tcpTransport.Send("getsettings\n");
                    AppendLog("TX: getsettings [Wi-Fi, LF]");
                    CommandTextBox.Clear();
                }
                catch (Exception ex)
                {
                    AppendLog($"TCP-SÄNDNINGSFEL: {ex.Message}");
                    MessageBox.Show(ex.Message);
                }

                return;
            }


            if (string.IsNullOrWhiteSpace(command))
                return;

            string lineEnding = LineEndingComboBox.SelectedIndex switch
            {
                0 => "",
                1 => "\n",
                2 => "\r",
                3 => "\r\n",
                _ => "\r"
            };

            try
            {
                _serialTransport.Send(command + lineEnding);
                AppendLog($"TX: {command} [radslut valt i loggfliken]");
                CommandTextBox.Clear();
            }
            catch (Exception ex)
            {
                AppendLog($"SÄNDNINGSFEL: {ex.Message}");
                MessageBox.Show(ex.Message);
            }
        }

        private void AppendLog(string message)
        {
            LogTextBox.AppendText(
                $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");

            // Begränsa loggen när den blir för stor
            if (LogTextBox.LineCount > MaxLogRows)
            {
                string[] lines = LogTextBox.Text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

                LogTextBox.Text = string.Join(Environment.NewLine, lines.Skip(lines.Length - MaxLogRows));
            }

            LogTextBox.ScrollToEnd();
        }

        protected override void OnClosed(EventArgs e)
        {
            _serialTransport.DataReceived -= OnDataReceived;
            _serialTransport.ErrorOccurred -= OnSerialError;

            _tcpTransport.DataReceived -= OnDataReceived;
            _tcpTransport.ErrorOccurred -= OnTcpError;

            _serialTransport.Dispose();
            _tcpTransport.Dispose();

            _overviewRefreshTimer.Stop();

            base.OnClosed(e);
        }

        private void UpdateConnectionButtons()
        {
            bool usbConnected = _serialTransport.IsConnected;
            bool tcpConnected = _tcpTransport.IsConnected;

            bool connected = usbConnected || tcpConnected;

            btnUsbConnect.IsEnabled = !connected;
            btnTcpConnect.IsEnabled = !connected;

            btnUsbDisconnect.IsEnabled = usbConnected;
            btnTcpDisconnect.IsEnabled = tcpConnected;
        }
        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            UpdateConnectionButtons();
        }
    }
}
