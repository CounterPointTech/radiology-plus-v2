using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FFI_Exam_Monitoring_Service
{
    public class SqlDatabaseSettings
    {
        [JsonProperty("UserID")]
        public string UserID { get; set; }
        [JsonProperty("Password")]
        public string Password { get; set; }
        [JsonProperty("Server")]
        public string Server { get; set; }
        [JsonProperty("Database")]
        public string Database { get; set; }
        [JsonProperty("Encrypt")]
        public string Encrypt { get; set; }
    }
}
