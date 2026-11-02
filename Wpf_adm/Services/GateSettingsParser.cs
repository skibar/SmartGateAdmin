
using System;
using System.Collections.Generic;
using System.Globalization;
using smartgate_wpf_adm.models;

namespace smartgate_wpf_adm.Services
{
    public static class GateSettingsParser
    {
        public static GateSettings Parse(IEnumerable<string> lines)
        {
            var result = new GateSettings();

            foreach (string line in lines)
            {
                int separator = line.IndexOf(':');
                if (separator < 0)
                    continue;

                string name = line[..separator].Trim().ToLowerInvariant();
                string value = line[(separator + 1)..].Trim();

                switch (name)
                {
                    case "tid":
                    case "time":
                        result.Time = value;
                        break;
                    case "gate id":
                        result.GateId = Int(value);
                        break;
                    case "site id":
                        result.SiteId = Int(value);
                        break;
                    case "config ver.":
                    case "configversion":
                        result.ConfigVersion = Int(value);
                        break;
                    case "gatestate":
                        result.GateState = value;
                        break;
                    case "wifi":
                        result.Wifi = value;
                        break;
                    case "password":
                        result.Password = value;
                        break;
                    case "wifi db":
                        result.WifiSignal = value;
                        break;
                    case "local ip":
                        result.LocalIp = value;
                        break;
                    case "server":
                        result.Server = value;
                        break;
                    case "database":
                        result.Database = value;
                        break;
                    case "led duration":
                        result.LedDuration = Int(value);
                        break;
                    case "rfid timeout":
                        result.RfidAuthTimeout = Int(value);
                        break;
                    case "ir debounce":
                        result.IrDebounce = Int(value);
                        break;
                    case "invalid red":
                        result.InvalidRed = Int(value);
                        break;
                    case "red post":
                        result.RedPost = Int(value);
                        break;
                    case "ir rearm":
                        result.IrRearm = Int(value);
                        break;
                    case "version":
                        result.Version = Int(value);
                        break;
                    case "ir exists":
                        result.IrExists = Int(value);
                        break;
                    case "green passages":
                        result.GreenPassages = Int(value);
                        break;
                    case "red passages":
                        result.RedPassages = Int(value);
                        break;
                    case "green led pin":
                        result.GreenLedPin = Int(value);
                        break;
                    case "blue led pin":
                        result.BlueLedPin = Int(value);
                        break;
                    case "external stat":
                        result.ExternalStatus = value;
                        break;
                }
            }

            return result;
        }

        private static int Int(string value)
        {
            return int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int result) ? result : 0;
        }
    }
}
