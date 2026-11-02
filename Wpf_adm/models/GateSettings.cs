namespace smartgate_wpf_adm.models
{
    public class GateSettings
    {
        public string Time { get; set; } = "";
        public int GateId { get; set; }
        public int SiteId { get; set; }
        public int ConfigVersion { get; set; }

        public string GateState { get; set; } = "";
        public string Wifi { get; set; } = "";
        public string Password { get; set; } = "";
        public string WifiSignal { get; set; } = "";
        public string LocalIp { get; set; } = "";

        public string Server { get; set; } = "";
        public string ConfigUrl { get; set; } = "";
        public string Database { get; set; } = "";

        public int LedDuration { get; set; }
        public int RfidAuthTimeout { get; set; }
        public int IrDebounce { get; set; }
        public int InvalidRed { get; set; }
        public int RedPost { get; set; }
        public int IrRearm { get; set; }

        public int Version { get; set; }
        public int IrExists { get; set; }
        public int GreenPassages { get; set; }
        public int RedPassages { get; set; }

        public int GreenLedPin { get; set; }
        public int BlueLedPin { get; set; }

        public string ExternalStatus { get; set; } = "";
    }
}
