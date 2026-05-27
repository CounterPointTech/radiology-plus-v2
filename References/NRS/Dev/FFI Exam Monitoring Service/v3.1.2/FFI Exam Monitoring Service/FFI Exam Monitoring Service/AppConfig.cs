using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using Newtonsoft.Json;

namespace FFI_Exam_Monitoring_Service
{
    public static class AppConfig
    {
        // Declare static properties
        public static GeneralSettings GeneralSettings { get; set; }
        public static SqlDatabaseSettings SQLDatabaseSettings { get; private set; }
        public static StatusTrackerSettings StatusTrackerSettings { get; set; }
        public static List<RemoteConnection> RemoteConnections { get; set; } = new List<RemoteConnection>();
        public static bool IsSCPStarted { get; set; }
        public static Dictionary<string, string> Exclusions { get; set; } = new Dictionary<string, string>();

        public static bool LoadConfiguration()
        {
            try
            {
                string configFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

                if (!File.Exists(configFile))
                {
                    Console.WriteLine("Configuration file not found.");
                    return false;
                }

                string json = File.ReadAllText(configFile);

                // Deserialize into a container object
                var dsConfig = JsonConvert.DeserializeObject<DeserializeAppConfig>(json);

                if (dsConfig != null)
                {
                    // Assign values to static properties
                    GeneralSettings = dsConfig.GeneralSettings;
                    StatusTrackerSettings = dsConfig.StatusTrackerSettings;
                    SQLDatabaseSettings = dsConfig.SQLDatabaseSettings;
                    RemoteConnections = dsConfig.RemoteConnections;
                }

                return true;
            }
            catch (Exception ex)
            {
                // Log the exception or print to console
                Console.WriteLine($"Error loading configuration: {ex.Message}");
                return false;
            }
        }

        // Helper class for deserialization
        private class DeserializeAppConfig
        {
            public SqlDatabaseSettings SQLDatabaseSettings { get; set; }
            public GeneralSettings GeneralSettings { get; set; }
            public StatusTrackerSettings StatusTrackerSettings { get; set; }
            public List<RemoteConnection> RemoteConnections { get; set; }
        }
    }
}
