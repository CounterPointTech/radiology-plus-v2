using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Threading;

namespace FFI_Exam_Monitoring_Service
{
    public class MonitorEngine
    {
        // Loggers
        private static NLog.Logger fileLogger = NLog.LogManager.GetLogger("FileLog");
        private static NLog.Logger consoleLogger = NLog.LogManager.GetLogger("DCMLog");

        // Privates
        private string currentExecDirectory = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
        private CancellationTokenSource _monitorCts;
        private CancellationTokenSource _retentionCts;
        private int _consecutiveFailures;
        DBQuery dbQuery;

        private Task _serviceTask;

        public MonitorEngine()
        {

        }

        public void Start()
        {
            consoleLogger.Info("Starting Service...");
            fileLogger.Info("Starting Service...");
            _serviceTask = StartAsync();
            consoleLogger.Info("Service Started!");
            fileLogger.Info("Starting Started!");
        }

        public void Stop()
        {
            consoleLogger.Info("Stopping Service...");
            fileLogger.Info("Stopping Service...");
            _monitorCts?.Cancel();
            _retentionCts?.Cancel();
            _serviceTask.Wait();
            consoleLogger.Info("Service Stopped!");
            fileLogger.Info("Service Stopped!");
        }

        public async Task StartAsync()
        {
            consoleLogger.Info("Loading config.json...");
            fileLogger.Info("Loading config.json...");

            var configLoaded = AppConfig.LoadConfiguration();

            if (configLoaded)
            {
                dbQuery = new DBQuery();
                int interval = 120;

                consoleLogger.Info("config.json successfully loaded.");
                fileLogger.Info("config.json successfully loaded.");

                consoleLogger.Info("Loading exclusions...");
                fileLogger.Info("Loading exclusions...");
                
                var exclusionPath = Path.Combine(currentExecDirectory, "Exclusions");
                AppConfig.Exclusions = ReadExclusionFiles(exclusionPath);

                if (AppConfig.Exclusions.Keys.Count > 0)
                {
                    consoleLogger.Info("Exclusions loaded!");
                    fileLogger.Info("Exclusions loaded!");
                }

                else
                {
                    consoleLogger.Info("No exclusions found.");
                    fileLogger.Info("No exclusions found.");
                }

                consoleLogger.Info("Starting Monitor Service...");
                fileLogger.Info("Starting Monitor Service...");

                int.TryParse(AppConfig.GeneralSettings.MonitorIntervalInSeconds, out interval);
                var monitorTask = StartMonitorService(interval);
                consoleLogger.Info("Monitor Service Started.");
                fileLogger.Info("Monitor Service Started.");

                int retentionCheckInterval = 24;
                int.TryParse(AppConfig.StatusTrackerSettings.RetentionCheckIntervalInHours, out retentionCheckInterval);

                consoleLogger.Info("Starting study retention monitor...");
                fileLogger.Info("Starting study retention monitor...");
                var retentionTask = StartStudyRetentionCheck(retentionCheckInterval);
                consoleLogger.Info("Retention monitor started!");
                fileLogger.Info("Retention monitor started!");

                await Task.WhenAll(monitorTask, retentionTask);

            }
        }

        private async Task StartMonitorService(double interval)
        {
            _monitorCts?.Cancel();
            _monitorCts = new CancellationTokenSource();

            await TaskMonitor.Run(MonitorService, TimeSpan.FromSeconds(interval), _monitorCts.Token);
        }

        private async Task MonitorService(CancellationToken cancellationToken)
        {
            try
            {
                // Reload exclusions each cycle so file changes take effect without restart
                var exclusionPath = Path.Combine(currentExecDirectory, "Exclusions");
                AppConfig.Exclusions = ReadExclusionFiles(exclusionPath);

                dbQuery = new DBQuery();
                // Build an accession dictionary from FFI
                consoleLogger.Info("Building exam table for updates...");
                fileLogger.Info("Building exam table for updates...");

                var examTable = dbQuery.GetExamData();

                consoleLogger.Info("Exam table built.");
                fileLogger.Info("Exam table built.");

                // Update the status
                if (examTable.Rows.Count > 0)
                {
                    consoleLogger.Info($"{examTable.Rows.Count} exams found!");
                    fileLogger.Info($"{examTable.Rows.Count} exams found!");

                    await dbQuery.UpdateSitesAsync(examTable);

                    consoleLogger.Info($"Updates Complete.");
                    fileLogger.Info($"Updates Compete.");

                    _consecutiveFailures = 0;
                }
                else
                {
                    _consecutiveFailures++;
                    if (_consecutiveFailures >= 3)
                    {
                        consoleLogger.Warn($"Warning: {_consecutiveFailures} consecutive monitor cycles returned zero exams. Verify FFI Clinical Data Store connectivity.");
                        fileLogger.Warn($"Warning: {_consecutiveFailures} consecutive monitor cycles returned zero exams. Verify FFI Clinical Data Store connectivity.");
                    }
                }
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                consoleLogger.Error($"Monitor cycle failed (consecutive failures: {_consecutiveFailures}): {ex.ToString()}");
                fileLogger.Error($"Monitor cycle failed (consecutive failures: {_consecutiveFailures}): {ex.ToString()}");
            }
        }

        private async Task StartStudyRetentionCheck(double interval)
        {
            _retentionCts?.Cancel();
            _retentionCts = new CancellationTokenSource();

            await TaskMonitor.Run(CheckStudyRetention, TimeSpan.FromHours(interval), _retentionCts.Token);
        }

        private async Task CheckStudyRetention(CancellationToken cancellationToken)
        {
            DBQuery dbQuery = new DBQuery();

            consoleLogger.Info($"Checking for studies older than {AppConfig.StatusTrackerSettings.StudyRetentionInDays} days in the Status Tracker...");
            fileLogger.Info($"Checking for studies older than {AppConfig.StatusTrackerSettings.StudyRetentionInDays} days in the Status Tracker...");
            await dbQuery.RemoveStudiesFromStatusTracker();
            consoleLogger.Info($"Status Tracker retention check complete.");
            fileLogger.Info($"Status Tracker retention check complete.");

        }

        private Dictionary<string, string> ReadExclusionFiles(string directoryPath)
        {
            var exclusions = new Dictionary<string, string>();

            try
            {
                // Ensure the directory exists
                if (!Directory.Exists(directoryPath))
                {
                    throw new DirectoryNotFoundException($"The directory {directoryPath} does not exist.");
                }

                // Get all text files in the directory
                var files = Directory.GetFiles(directoryPath, "*.txt");

                foreach (var file in files)
                {
                    try
                    {
                        var fileName = Path.GetFileNameWithoutExtension(file);
                        var lines = File.ReadAllLines(file);
                        var pipeDelimitedContent = string.Join("|", lines);

                        if (!exclusions.Keys.Contains(fileName))
                        {
                            exclusions.Add(fileName, pipeDelimitedContent);
                        }
                    }

                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error reading file {file}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred while processing the directory: {ex.Message}");
            }

            return exclusions;
        }
    }
}
