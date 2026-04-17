using System;
using System.IO;
using System.Threading;

namespace IndustrialProcessingSystem
{
    internal class Program
    {
        private static readonly ThreadLocal<Random> _random = new ThreadLocal<Random>(
            () => new Random(Interlocked.Increment(ref _seed)));
        private static int _seed = Environment.TickCount;

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
                Console.WriteLine("===================================");

                ProcessingSystem system = new ProcessingSystem(config.WorkerCount, config.MaxQueueSize);

                system.JobCompleted += (id, result) =>
                {
                    string logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [COMPLETED] {id}, {result}";
                    AppendLogAsync(logLine);
                };

                system.JobFailed += (id, reason) =>
                {
                    string logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [FAILED] {id}, {reason}";
                    AppendLogAsync(logLine);
                };

                system.JobAborted += (id, reason) =>
                {
                    string logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [ABORT] {id}, {reason}";
                    AppendLogAsync(logLine);
                };

                Console.WriteLine("Submitting initial jobs from config...");
                foreach (var job in config.InitialJobs)
                {
                    try
                    {
                        var handle = system.Submit(job);
                        Console.WriteLine($"  Submitted [{job.Priority}] {job.Type} - {job.Payload} (Id: {job.Id})");
                    }
                    catch (InvalidOperationException ex)
                    {
                        Console.WriteLine($"  [REJECTED] {job.Id}: {ex.Message}");
                    }
                }

                Console.WriteLine("===================================");
                Console.WriteLine($"Starting {config.WorkerCount} producer threads...");

                for (int i = 0; i < config.WorkerCount; i++)
                {
                    int threadIndex = i;
                    Thread producer = new Thread(() => ProducerLoop(system, threadIndex))
                    {
                        IsBackground = true,
                        Name = $"Producer-{threadIndex}"
                    };
                    producer.Start();
                }

                Console.WriteLine("System running. Press Enter to exit.");
                Console.ReadLine();

                system.Dispose();
                Console.WriteLine("System shut down.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fatal error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }

        private static void ProducerLoop(ProcessingSystem system, int threadIndex)
        {
            while (true)
            {
                try
                {
                    Job job = GenerateRandomJob();

                    try
                    {
                        var handle = system.Submit(job);
                        Console.WriteLine($"[Producer-{threadIndex}] Submitted {job.Type} [{job.Priority}] - {job.Payload}");
                    }
                    catch (InvalidOperationException)
                    {
                        // Queue full, skip this job
                    }

                    int delay = _random.Value.Next(500, 3000);
                    Thread.Sleep(delay);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Producer-{threadIndex}] Error: {ex.Message}");
                }
            }
        }

        private static Job GenerateRandomJob()
        {
            var rng = _random.Value;
            JobType type = rng.Next(2) == 0 ? JobType.Prime : JobType.IO;
            int priority = rng.Next(1, 6);
            string payload;

            if (type == JobType.Prime)
            {
                int numbers = rng.Next(100, 50001);
                int threads = rng.Next(1, 9);
                payload = $"numbers:{numbers},threads:{threads}";
            }
            else
            {
                int delay = rng.Next(100, 5001);
                payload = $"delay:{delay}";
            }

            return new Job(type, payload, priority);
        }

        private static readonly SemaphoreSlim _mainLogLock = new SemaphoreSlim(1, 1);

        private static async void AppendLogAsync(string message)
        {
            await _mainLogLock.WaitAsync();
            try
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "jobs.log");
                using (var writer = new StreamWriter(logPath, true))
                {
                    await writer.WriteLineAsync(message);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LOG ERROR] {ex.Message}");
            }
            finally
            {
                _mainLogLock.Release();
            }
        }
    }
}
