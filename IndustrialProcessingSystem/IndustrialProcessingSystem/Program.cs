using System;
using System.IO;

namespace IndustrialProcessingSystem
{
    internal class Program
    {
        static void Main(string[] args)
        {
            try
            {
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\SystemConfig\SystemConfig.xml");
                configPath = Path.GetFullPath(configPath);

                if (!File.Exists(configPath))
                {
                    Console.WriteLine($"Configuration file not found: {configPath}");
                    return;
                }

                SystemConfig config = SystemConfigLoader.Load(configPath);

                Console.WriteLine($"WorkerCount: {config.WorkerCount}");
                Console.WriteLine($"MaxQueueSize: {config.MaxQueueSize}");
                Console.WriteLine($"Initial jobs: {config.InitialJobs.Count}");
                Console.WriteLine("---");

                foreach (var job in config.InitialJobs)
                {
                    Console.WriteLine($"  [{job.Priority}] {job.Type} - {job.Payload}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }
    }
}
