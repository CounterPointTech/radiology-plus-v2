using System;
using Topshelf;

namespace FFI_Exam_Monitoring_Service
{
    class Program
    {
        static void Main(string[] args)
        {
            var exitCode = HostFactory.Run(x =>
            {
                x.Service<MonitorEngine>(s =>
                {
                    s.ConstructUsing(monitorEngine => new MonitorEngine());
                    s.WhenStarted(monitorEngine => monitorEngine.Start());
                    s.WhenStopped(monitorEngine => monitorEngine.Stop());
                });

                x.RunAsLocalSystem();

                x.SetServiceName("FFIStudyMonitorService");
                x.SetDisplayName("FFI Study Monitor Service");
                x.SetDescription("");

                x.StartAutomaticallyDelayed();
            });
        }
    }
}
