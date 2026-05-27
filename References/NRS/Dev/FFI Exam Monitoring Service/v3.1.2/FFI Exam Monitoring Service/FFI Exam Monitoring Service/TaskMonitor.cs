using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FFI_Exam_Monitoring_Service
{
    public static class TaskMonitor
    {
        // Loggers
        private static NLog.Logger fileLogger = NLog.LogManager.GetLogger("FileLog");
        private static NLog.Logger consoleLogger = NLog.LogManager.GetLogger("DCMLog");

        public static async Task Run(Func<CancellationToken, Task> action, TimeSpan interval, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await action(cancellationToken);
                }
                catch (Exception ex)
                {
                    consoleLogger.Error($"An exception occurred while executing the Task action: {ex.Message}");
                    consoleLogger.Error(ex.StackTrace);

                    fileLogger.Error($"An exception occurred while executing the Task action: {ex.Message}");
                    fileLogger.Error(ex.StackTrace);
                }

                try
                {
                    await Task.Delay(interval, cancellationToken);
                }

                catch (TaskCanceledException)
                {
                    // Task.Delay can throw a TaskCanceledException if the cancellationToken is cancelled. 
                    // This is expected behavior when the task is cancelled, so we just catch and ignore this exception.
                }

                catch (Exception ex)
                {
                    consoleLogger.Error($"An exception occurred while executing the Task timer: {ex.Message}");
                    consoleLogger.Error(ex.StackTrace);

                    fileLogger.Error($"An exception occurred while executing the Task timer: {ex.Message}");
                    fileLogger.Error(ex.StackTrace);
                    consoleLogger.Error(ex.StackTrace);
                }
            }
        }
    }
}
