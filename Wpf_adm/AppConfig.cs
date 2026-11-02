using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace smartgate_wpf_adm
{
    public class AppConfig
    {
        public string IpAddress { get; set; } = "192.168.86.191";
        public int Port { get; set; } = 4242;
        public string Token { get; set; } = "";
    }
}