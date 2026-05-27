using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FFI_Exam_Monitoring_Service
{
    public class RemoteConnection
    {
        [JsonProperty("Name")]
        public string Name { get; set; }

        [JsonProperty("Active")]
        public string Active { get; set; }

        [JsonProperty("MarkIfVerified")]
        public string MarkIfVerified { get; set; }

        [JsonProperty("Server")]
        public string Server { get; set; }

        [JsonProperty("Port")]
        public string Port { get; set; }

        [JsonProperty("UserID")]
        public string UserID { get; set; }

        [JsonProperty("Password")]
        public string Password { get; set; }

        [JsonProperty("Database")]
        public string Database { get; set; }

        [JsonProperty("Timeout")]
        public string Timeout { get; set; }
    }
}
