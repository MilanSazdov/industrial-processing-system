using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace IndustrialProcessingSystem
{
    public class ProcessingSystem
    {
        private readonly SortedDictionary<int, Queue<Job>> _priorityQueue = new SortedDictionary<int, Queue<Job>>();
        private readonly object _queueLock = new object();
        private int _currentQueueSize;
        private readonly int _maxQueueSize;

        private readonly SemaphoreSlim _jobAvailable = new SemaphoreSlim(0);

        private readonly ConcurrentDictionary<Guid, JobHandle> _jobHandles = new ConcurrentDictionary<Guid, JobHandle>();
        private readonly ConcurrentDictionary<Guid, Job> _jobRegistry = new ConcurrentDictionary<Guid, Job>();
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<int>> _tcsMap = new ConcurrentDictionary<Guid, TaskCompletionSource<int>>();

        private readonly List<JobExecutionRecord> _executionLog = new List<JobExecutionRecord>();
        private readonly object _logLock = new object();

        private readonly List<Thread> _workers = new List<Thread>();
        private readonly Timer _reportTimer;
        private int _reportIndex;

        private readonly SemaphoreSlim _fileLock = new SemaphoreSlim(1, 1);
        private readonly string _logFilePath;

        public event Action<Guid, int> JobCompleted;
        public event Action<Guid, string> JobFailed;

        public ProcessingSystem(int workerCount, int maxQueueSize)
        {
            _maxQueueSize = maxQueueSize;
            _logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "jobs.log");

            for (int i = 0; i < workerCount; i++)
            {
                Thread worker = new Thread(WorkerLoop)
                {
                    IsBackground = true,
                    Name = $"Worker-{i}"
                };
                _workers.Add(worker);
                worker.Start();
            }

            _reportTimer = new Timer(GenerateReport, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }

        public JobHandle Submit(Job job)
        {
            JobHandle existingHandle;
            if (_jobHandles.TryGetValue(job.Id, out existingHandle))
            {
                Console.WriteLine($"[IDEMPOTENT] Job {job.Id} already submitted, returning existing handle.");
                return existingHandle;
            }

            lock (_queueLock)
            {
                if (_currentQueueSize >= _maxQueueSize)
                {
                    Console.WriteLine($"[REJECTED] Queue full ({_currentQueueSize}/{_maxQueueSize}), job {job.Id} rejected.");
                    return null;
                }

                var tcs = new TaskCompletionSource<int>();
                var handle = new JobHandle(job.Id, tcs.Task);

                if (!_jobHandles.TryAdd(job.Id, handle))
                {
                    return _jobHandles[job.Id];
                }

                _tcsMap[job.Id] = tcs;
                _jobRegistry[job.Id] = job;

                Queue<Job> queue;
                if (!_priorityQueue.TryGetValue(job.Priority, out queue))
                {
                    queue = new Queue<Job>();
                    _priorityQueue[job.Priority] = queue;
                }
                queue.Enqueue(job);
                _currentQueueSize++;
            }

            _jobAvailable.Release();
            return _jobHandles[job.Id];
        }

        private Job Dequeue()
        {
            lock (_queueLock)
            {
                foreach (var kvp in _priorityQueue)
                {
                    if (kvp.Value.Count > 0)
                    {
                        Job job = kvp.Value.Dequeue();
                        _currentQueueSize--;

                        if (kvp.Value.Count == 0)
                        {
                            _priorityQueue.Remove(kvp.Key);
                        }

                        return job;
                    }
                }
            }
            return null;
        }

        private void WorkerLoop()
        {
            while (true)
            {
                _jobAvailable.Wait();

                Job job = Dequeue();
                if (job == null) continue;

                ProcessJobWithRetry(job);
            }
        }

        private void ProcessJobWithRetry(Job job)
        {
            const int maxAttempts = 3;
            const int timeoutMs = 2000;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var stopwatch = Stopwatch.StartNew();

                Task<int> processingTask = Task.Run(() => ExecuteJob(job));
                Task timeoutTask = Task.Delay(timeoutMs);
                Task winner = Task.WhenAny(processingTask, timeoutTask).Result;

                stopwatch.Stop();

                if (winner == processingTask && processingTask.Status == TaskStatus.RanToCompletion)
                {
                    int result = processingTask.Result;

                    lock (_logLock)
                    {
                        _executionLog.Add(new JobExecutionRecord
                        {
                            JobId = job.Id,
                            Type = job.Type,
                            Success = true,
                            Result = result,
                            Duration = stopwatch.Elapsed,
                            CompletedAt = DateTime.Now
                        });
                    }

                    TaskCompletionSource<int> tcs;
                    if (_tcsMap.TryGetValue(job.Id, out tcs))
                    {
                        tcs.TrySetResult(result);
                    }

                    JobCompleted?.Invoke(job.Id, result);

                    Console.WriteLine($"[{Thread.CurrentThread.Name}] COMPLETED {job.Type} job {job.Id} = {result} (attempt {attempt})");
                    return;
                }
                else
                {
                    string reason = processingTask.IsFaulted
                        ? processingTask.Exception?.InnerException?.Message ?? "Unknown error"
                        : "Timeout";

                    lock (_logLock)
                    {
                        _executionLog.Add(new JobExecutionRecord
                        {
                            JobId = job.Id,
                            Type = job.Type,
                            Success = false,
                            Result = -1,
                            Duration = stopwatch.Elapsed,
                            CompletedAt = DateTime.Now
                        });
                    }

                    JobFailed?.Invoke(job.Id, reason);

                    Console.WriteLine($"[{Thread.CurrentThread.Name}] FAILED {job.Type} job {job.Id} - {reason} (attempt {attempt}/{maxAttempts})");

                    if (attempt == maxAttempts)
                    {
                        LogToFileAsync($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [ABORT] {job.Id}, {reason}");

                        TaskCompletionSource<int> tcs;
                        if (_tcsMap.TryGetValue(job.Id, out tcs))
                        {
                            tcs.TrySetException(new TimeoutException($"Job {job.Id} aborted after {maxAttempts} attempts."));
                        }

                        Console.WriteLine($"[{Thread.CurrentThread.Name}] ABORT {job.Type} job {job.Id}");
                    }
                }
            }
        }

        private int ExecuteJob(Job job)
        {
            switch (job.Type)
            {
                case JobType.Prime:
                    return ExecutePrimeJob(job.Payload);
                case JobType.IO:
                    return ExecuteIOJob(job.Payload);
                default:
                    throw new InvalidOperationException($"Unknown job type: {job.Type}");
            }
        }

        private int ExecutePrimeJob(string payload)
        {
            string cleaned = payload.Replace("_", "");
            string[] parts = cleaned.Split(',');

            int numbers = 0;
            int threadCount = 1;

            foreach (string part in parts)
            {
                string[] kv = part.Split(':');
                string key = kv[0].Trim();
                int value = int.Parse(kv[1].Trim());

                if (key == "numbers") numbers = value;
                else if (key == "threads") threadCount = value;
            }

            threadCount = Math.Max(1, Math.Min(8, threadCount));

            if (numbers < 2) return 0;

            int totalCount = 0;
            int rangeSize = (numbers - 1) / threadCount;
            var threads = new Thread[threadCount];
            var counts = new int[threadCount];

            for (int t = 0; t < threadCount; t++)
            {
                int threadIndex = t;
                int start = 2 + threadIndex * rangeSize;
                int end = (threadIndex == threadCount - 1) ? numbers : start + rangeSize - 1;

                threads[t] = new Thread(() =>
                {
                    int localCount = 0;
                    for (int n = start; n <= end; n++)
                    {
                        if (IsPrime(n)) localCount++;
                    }
                    counts[threadIndex] = localCount;
                });
                threads[t].Start();
            }

            for (int t = 0; t < threadCount; t++)
            {
                threads[t].Join();
                totalCount += counts[t];
            }

            return totalCount;
        }

        private static bool IsPrime(int n)
        {
            if (n < 2) return false;
            if (n == 2) return true;
            if (n % 2 == 0) return false;
            int limit = (int)Math.Sqrt(n);
            for (int i = 3; i <= limit; i += 2)
            {
                if (n % i == 0) return false;
            }
            return true;
        }

        private int ExecuteIOJob(string payload)
        {
            string cleaned = payload.Replace("_", "");
            string[] kv = cleaned.Split(':');
            int delay = int.Parse(kv[1].Trim());

            Thread.Sleep(delay);

            Random rng = new Random();
            return rng.Next(0, 101);
        }

        public IEnumerable<Job> GetTopJobs(int n)
        {
            lock (_queueLock)
            {
                var result = new List<Job>();
                foreach (var kvp in _priorityQueue)
                {
                    foreach (var job in kvp.Value)
                    {
                        result.Add(job);
                        if (result.Count >= n) return result;
                    }
                }
                return result;
            }
        }

        public Job GetJob(Guid id)
        {
            Job job;
            _jobRegistry.TryGetValue(id, out job);
            return job;
        }

        private void LogToFileAsync(string message)
        {
            Task.Run(async () =>
            {
                await _fileLock.WaitAsync();
                try
                {
                    using (var writer = new StreamWriter(_logFilePath, true))
                    {
                        await writer.WriteLineAsync(message);
                    }
                }
                finally
                {
                    _fileLock.Release();
                }
            });
        }

        private void GenerateReport(object state)
        {
            List<JobExecutionRecord> snapshot;
            lock (_logLock)
            {
                snapshot = _executionLog.ToList();
            }

            if (snapshot.Count == 0) return;

            var completedByType = from record in snapshot
                                  where record.Success
                                  group record by record.Type into g
                                  select new { Type = g.Key, Count = g.Count() };

            var averageTimeByType = from record in snapshot
                                    group record by record.Type into g
                                    select new { Type = g.Key, AverageMs = g.Average(r => r.Duration.TotalMilliseconds) };

            var failedByType = from record in snapshot
                               where !record.Success
                               group record by record.Type into g
                               orderby g.Key
                               select new { Type = g.Key, Count = g.Count() };

            var report = new XDocument(
                new XElement("Report",
                    new XAttribute("Timestamp", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss")),
                    new XElement("CompletedByType",
                        completedByType.Select(x => new XElement("Type",
                            new XAttribute("Name", x.Type),
                            new XAttribute("Count", x.Count)))),
                    new XElement("AverageTimeByType",
                        averageTimeByType.Select(x => new XElement("Type",
                            new XAttribute("Name", x.Type),
                            new XAttribute("AverageMs", x.AverageMs.ToString("F1"))))),
                    new XElement("FailedByType",
                        failedByType.Select(x => new XElement("Type",
                            new XAttribute("Name", x.Type),
                            new XAttribute("Count", x.Count))))
                )
            );

            int index = Interlocked.Increment(ref _reportIndex) - 1;
            string fileName = $"report_{index % 10}.xml";
            string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);

            report.Save(filePath);
            Console.WriteLine($"[REPORT] Generated {fileName}");
        }
    }
}
