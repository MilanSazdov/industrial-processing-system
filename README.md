# Industrial Processing System

A thread-safe C# console application that simulates industrial job processing using an **async producer-consumer architecture** with a priority queue, event-driven logging, retry logic, and idempotency guarantees.

Built as a university assignment for the **SNUS (Sistemski i Namenski Upravljacki Softver)** course — Colloquium 1, 2026.

---

## Table of Contents

- [Overview](#overview)
- [Architecture](#architecture)
- [Features](#features)
- [Project Structure](#project-structure)
- [Data Model](#data-model)
- [How It Works](#how-it-works)
  - [System Initialization](#system-initialization)
  - [Job Submission](#job-submission)
  - [Worker Loop](#worker-loop)
  - [Job Execution](#job-execution)
  - [Retry and Timeout](#retry-and-timeout)
  - [Event System and Logging](#event-system-and-logging)
  - [Query Methods](#query-methods)
  - [LINQ Reports](#linq-reports)
- [XML Configuration](#xml-configuration)
- [Thread Safety](#thread-safety)
- [Build and Run](#build-and-run)
- [Sample Output](#sample-output)
- [Generated Files](#generated-files)
- [Tech Stack](#tech-stack)

---

## Overview

The system implements a **ProcessingSystem** service that accepts industrial jobs through a `Submit(Job)` API, processes them asynchronously via configurable worker threads, and returns a `JobHandle` that callers can await for results. The system enforces:

- **Priority-based scheduling** — jobs with lower priority numbers are processed first
- **Idempotency** — the same job (by GUID) is never processed twice
- **Bounded queue** — configurable maximum queue size; excess jobs are rejected
- **Timeout and retry** — 2-second timeout per attempt, up to 3 attempts before abort
- **Event-driven logging** — asynchronous file logging via lambda-subscribed events
- **Periodic LINQ reports** — XML reports generated every 60 seconds with execution statistics

---

## Architecture

```
                    +---------------------------------+
                    |   Main Thread (N Producers)      |
                    |   Each thread randomly generates  |
                    |   and submits new jobs            |
                    +----------------+----------------+
                                     |
                                Submit(Job)
                                     |
                                     v
                    +---------------------------------+
                    |       ProcessingSystem           |
                    |  +---------------------------+  |
                    |  | Thread-Safe Priority Queue |  |
                    |  | SortedDictionary<int,      |  |
                    |  |     Queue<Job>>             |  |
                    |  +---------------------------+  |
                    |  | MaxQueueSize Limit         |  |
                    |  +---------------------------+  |
                    |  | Idempotency Check          |  |
                    |  | ConcurrentDictionary       |  |
                    |  +---------------------------+  |
                    +--------+---------------+--------+
                             |               |
                        Dequeue()       Dequeue()
                             |               |
                             v               v
                    +--------+--+   +--------+--+
                    | Worker-0  |   | Worker-N  |     N background
                    | Thread    |   | Thread    |     threads
                    +-----------+   +-----------+
                             |               |
                +------------+---+---+-------+--------+
                |                |                     |
                v                v                     v
        +-------+------+  +-----+--------+   +--------+-------+
        |  Prime Job   |  |   IO Job     |   | Job Completed  |
        |  CPU-bound   |  |  Simulated   |   |    Event       |
        | prime count  |  |  I/O delay   |   +--------+-------+
        |  (parallel)  |  | Thread.Sleep |            |
        +-------+------+  +-----+--------+   +--------+-------+
                |                |            | Job Failed     |
                v                v            |    Event       |
        +-------+----------------+--+         +--------+-------+
        | TaskCompletionSource<int> |                  |
        |      (JobHandle)          |                  v
        +-------+------------------+         +--------+-------+
                |                            | Async File Log |
                v                            | (jobs.log)     |
        +-------+------+                    +----------------+
        |    Client    |
        | (Await Result)|
        +--------------+
```

---

## Features

| Feature | Description |
|---|---|
| **Async Producer-Consumer** | Multiple producer threads submit jobs; multiple worker threads consume and process them concurrently |
| **Priority Queue** | `SortedDictionary<int, Queue<Job>>` ensures lower-numbered (higher-priority) jobs are always dequeued first |
| **Idempotency** | `ConcurrentDictionary<Guid, JobHandle>` prevents duplicate job execution; submitting a job with the same ID returns the existing handle |
| **Bounded Queue** | Configurable `MaxQueueSize`; submissions beyond the limit throw `InvalidOperationException` |
| **2s Timeout + 3 Retries** | Each job attempt races against a 2-second `Task.Delay`; after 3 consecutive timeouts the job is aborted |
| **Event-Driven Logging** | `JobCompleted`, `JobFailed`, and `JobAborted` events fire lambda handlers that asynchronously write to `jobs.log` |
| **LINQ Reports** | Every 60 seconds, LINQ queries aggregate execution records into an XML report (completed/failed counts, average duration per type) |
| **Circular Report Files** | Reports rotate through 10 files (`report_0.xml` ... `report_9.xml`); the 11th report overwrites the 1st |
| **XML Configuration** | Worker count, queue size, and initial jobs are loaded from `SystemConfig.xml` using LINQ to XML |
| **Graceful Shutdown** | `CancellationTokenSource` propagates shutdown to workers and producers; `IDisposable` cleans up all resources |

---

## Project Structure

```
industrial-processing-system/
├── .gitignore
├── CLAUDE.md                          # Development notes and architecture spec
├── README.md                          # This file
├── SystemConfig/
│   └── SystemConfig.xml               # System configuration (workers, queue size, initial jobs)
└── IndustrialProcessingSystem/
    ├── IndustrialProcessingSystem.sln  # Visual Studio solution
    └── IndustrialProcessingSystem/
        ├── IndustrialProcessingSystem.csproj   # .NET Framework 4.7.2 project
        ├── App.config                          # Runtime configuration
        ├── Program.cs                          # Entry point, producers, event wiring, async logging
        ├── ProcessingSystem.cs                 # Core engine: queue, workers, submit, retry, reports
        ├── SystemConfigLoader.cs               # XML config parser (LINQ to XML)
        ├── Job.cs                              # Job data model
        ├── JobType.cs                          # Enum: Prime, IO
        ├── JobHandle.cs                        # Async result handle (wraps TaskCompletionSource)
        ├── JobExecutionRecord.cs               # Internal execution tracking for LINQ reports
        └── Properties/
            └── AssemblyInfo.cs                 # Assembly metadata
```

---

## Data Model

### `JobType` (enum)

```csharp
public enum JobType { Prime, IO }
```

### `Job`

| Field | Type | Description |
|---|---|---|
| `Id` | `Guid` | Auto-generated unique identifier (`Guid.NewGuid()`) |
| `Type` | `JobType` | Either `Prime` (CPU-bound) or `IO` (simulated I/O) |
| `Payload` | `string` | Parseable parameters — e.g. `"numbers:10_000,threads:3"` or `"delay:1_000"` |
| `Priority` | `int` | Lower number = higher priority (1 is highest) |

All properties are **read-only** (immutable) — set via constructor only, preventing data races across threads.

### `JobHandle`

| Field | Type | Description |
|---|---|---|
| `Id` | `Guid` | Matches the submitted `Job.Id` |
| `Result` | `Task<int>` | Awaitable result backed by a `TaskCompletionSource<int>` |

The caller receives a `JobHandle` immediately on `Submit()` and can `await handle.Result` to get the computed value asynchronously — no `Thread.Sleep` polling needed.

### `JobExecutionRecord`

| Field | Type | Description |
|---|---|---|
| `JobId` | `Guid` | Which job was executed |
| `Type` | `JobType` | Prime or IO |
| `Success` | `bool` | Whether this attempt succeeded |
| `Result` | `int` | Computed result (or `-1` on failure) |
| `Duration` | `TimeSpan` | Wall-clock time of this attempt |
| `CompletedAt` | `DateTime` | Timestamp of completion |

Used internally by the LINQ report generator to compute aggregate statistics.

---

## How It Works

### System Initialization

1. `Program.Main()` loads `SystemConfig.xml` via `SystemConfigLoader.Load()` using **LINQ to XML**
2. A `ProcessingSystem` is created with the configured `WorkerCount` and `MaxQueueSize`
3. Lambda expressions subscribe to `JobCompleted`, `JobFailed`, and `JobAborted` events
4. Initial jobs from the XML config are submitted to the system
5. `N` producer threads are started, each randomly generating and submitting new jobs in a loop
6. The system runs until the user presses Enter, then shuts down gracefully

### Job Submission

```
Submit(Job job) → JobHandle
```

1. **Idempotency check** — if `job.Id` already exists in the handles dictionary, return the existing handle immediately
2. **Capacity check** — if `_currentQueueSize >= _maxQueueSize`, throw `InvalidOperationException`
3. **Create TCS** — create a new `TaskCompletionSource<int>` and wrap it in a `JobHandle`
4. **Register** — store the handle, TCS, and job in their respective `ConcurrentDictionary` instances
5. **Enqueue** — add to `_priorityQueue[job.Priority]`; create the inner `Queue<Job>` if this priority level is new
6. **Signal** — `_jobAvailable.Release()` wakes one blocked worker
7. **Return** — caller receives the `JobHandle` immediately

The entire method runs inside `lock(_queueLock)` to ensure atomicity of the capacity check + enqueue operation.

### Worker Loop

Each of the `N` worker threads runs the same loop:

```
while (!cancellationRequested):
    _jobAvailable.Wait(token)        // block until a job is available
    job = Dequeue()                  // lock → take from lowest priority key → clean up empty queues
    ProcessJobWithRetry(job)         // execute with timeout + retry
```

- Workers use `SemaphoreSlim` for signaling (not busy-waiting)
- `Dequeue()` always picks from the **lowest key** in the `SortedDictionary` (= highest priority)
- Cancellation is cooperative via `CancellationToken`

### Job Execution

**Prime Jobs** — CPU-bound parallel prime counting:
1. Parse `numbers` (upper bound) and `threads` (parallelism) from the payload string
2. Clamp thread count to `[1, 8]`
3. Divide the range `[2, numbers]` into equal chunks
4. Spawn `N` background threads; each counts primes in its chunk using **trial division** (check divisors up to `sqrt(n)`)
5. Join all threads and sum the counts
6. Cancellation is checked between iterations to allow early exit on timeout

**IO Jobs** — simulated I/O delay:
1. Parse `delay` (milliseconds) from the payload string
2. Wait for the specified duration using `token.WaitHandle.WaitOne(delay)` (cancellation-aware)
3. Return a random integer in `[0, 100]` using a `ThreadLocal<Random>`

Note: IO jobs with `delay > 2000ms` will **always timeout** due to the 2-second execution limit, triggering the retry mechanism.

### Retry and Timeout

Each job attempt follows the **race pattern**:

```
for attempt = 1 to 3:
    processingTask = Task.Run(() => ExecuteJob(job))
    timeoutTask    = Task.Delay(2000ms)
    winner         = Task.WhenAny(processingTask, timeoutTask)

    if winner == processingTask AND succeeded:
        → record success, set TCS result, fire JobCompleted, return

    else (timeout or fault):
        → cancel the attempt, wait for task to finish
        → record failure, fire JobFailed
        → if attempt == 3: fire JobAborted, set TCS exception, return
```

Key details:
- After a timeout, the worker **waits for the timed-out task to finish** before retrying — this prevents multiple concurrent attempts of the same job
- After 3 failures, `tcs.TrySetException(TimeoutException)` is called — any code awaiting the `JobHandle.Result` will receive an exception
- The linked `CancellationTokenSource` allows cancelling the `Task.Delay` timer immediately when the job succeeds (avoids a 2-second leak)

### Event System and Logging

Three events are defined on `ProcessingSystem`:

```csharp
public event Action<Guid, int>    JobCompleted;   // (jobId, result)
public event Action<Guid, string> JobFailed;      // (jobId, reason)
public event Action<Guid, string> JobAborted;     // (jobId, reason)
```

In `Program.cs`, lambda expressions subscribe to these events and write to `jobs.log` asynchronously:

```
[2026-04-21 14:30:05] [COMPLETED] 3f2a..., 1229
[2026-04-21 14:30:07] [FAILED] 8b1c..., Timeout
[2026-04-21 14:30:13] [ABORT] 8b1c..., Timeout
```

The async logging pipeline:
1. Event fires → lambda calls `EnqueueLog(message)`
2. `AppendLogAsync()` acquires a `SemaphoreSlim(1,1)` (async mutex) → writes to file → releases
3. Pending log tasks are flushed on shutdown to ensure no entries are lost

### Query Methods

**`GetTopJobs(int n)`** — returns the top `N` jobs by priority from the current queue:
- Acquires `_queueLock`
- Iterates through the `SortedDictionary` (lowest key first = highest priority)
- Collects jobs across priority levels until `N` is reached or the queue is exhausted

**`GetJob(Guid id)`** — returns a specific job by ID:
- Direct `ConcurrentDictionary` lookup — O(1), no lock needed

### LINQ Reports

A `System.Threading.Timer` fires every 60 seconds and runs three LINQ queries on the execution log:

1. **Completed count per type** — groups successful records by `JobType`, counts each group
2. **Average duration per type** — groups all records by `JobType`, averages `Duration.TotalMilliseconds`
3. **Failed count per type** — groups failed records by `JobType`, sorts by type, counts each group

The results are written to XML using `XDocument` (LINQ to XML):

```xml
<Report Timestamp="2026-04-21T14:31:00">
  <CompletedByType>
    <Type Name="Prime" Count="12" />
    <Type Name="IO" Count="8" />
  </CompletedByType>
  <AverageTimeByType>
    <Type Name="Prime" AverageMs="845.3" />
    <Type Name="IO" AverageMs="1523.7" />
  </AverageTimeByType>
  <FailedByType>
    <Type Name="IO" Count="5" />
  </FailedByType>
</Report>
```

**Circular file rotation**: Reports cycle through `report_0.xml` to `report_9.xml`. The index is atomically incremented via `Interlocked.Increment` and wrapped with `% 10`. After 10 reports, the 11th overwrites `report_0.xml`, and so on.

---

## XML Configuration

The system is initialized from `SystemConfig/SystemConfig.xml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<SystemConfig>
    <WorkerCount>5</WorkerCount>
    <MaxQueueSize>100</MaxQueueSize>
    <Jobs>
        <Job Type="Prime" Payload="numbers:10_000,threads:3" Priority="1"/>
        <Job Type="Prime" Payload="numbers:20_000,threads:2" Priority="2"/>
        <Job Type="IO"    Payload="delay:1_000"              Priority="3"/>
        <Job Type="IO"    Payload="delay:3_000"              Priority="3"/>
        <Job Type="IO"    Payload="delay:15_000"             Priority="3"/>
    </Jobs>
</SystemConfig>
```

| Parameter | Description |
|---|---|
| `WorkerCount` | Number of background worker threads that process jobs |
| `MaxQueueSize` | Maximum number of jobs allowed in the priority queue simultaneously |
| `Jobs` | Initial jobs loaded at startup; each has `Type`, `Payload`, and `Priority` attributes |

**Payload format:**
- **Prime**: `"numbers:<N>,threads:<T>"` — count primes up to `N` using `T` threads (underscores allowed as separators, e.g. `10_000`)
- **IO**: `"delay:<ms>"` — simulate I/O by sleeping for `ms` milliseconds

Parsed by `SystemConfigLoader` using LINQ to XML query expressions.

---

## Thread Safety

Every shared resource is protected by an appropriate synchronization primitive:

| Resource | Protection Mechanism | Rationale |
|---|---|---|
| Priority queue (`SortedDictionary`) | `lock(_queueLock)` | Not inherently thread-safe; exclusive access needed for enqueue/dequeue |
| Queue size counter | Protected inside `_queueLock` | Atomic with enqueue/dequeue to prevent over-admission |
| Job handles, registry, TCS maps | `ConcurrentDictionary` | Lock-free concurrent reads/writes |
| Worker signaling | `SemaphoreSlim(0)` | Efficient blocking without busy-wait; supports cancellation tokens |
| Execution log | `lock(_logLock)` | `List<T>` is not thread-safe; short critical section |
| File logging | `SemaphoreSlim(1,1)` | Async-compatible mutex for `StreamWriter` access |
| Random number generation | `ThreadLocal<Random>` | Each thread gets its own `Random` instance — no lock contention |
| Report file index | `Interlocked.Increment` | Lock-free atomic increment for circular counter |
| TCS resolution | `TrySetResult` / `TrySetException` | Idempotent — safe to call from any thread; only the first call succeeds |
| Disposal guard | `Interlocked.CompareExchange` | Ensures `Dispose()` runs exactly once |

**Design constraint**: No nested locks are used anywhere in the codebase, eliminating deadlock risk.

---

## Build and Run

### Prerequisites

- **.NET Framework 4.7.2** (not .NET Core / .NET 5+)
- **Visual Studio 2017+** or **MSBuild** (included with Visual Studio Build Tools)
- Windows OS (required for .NET Framework)

### Build

**Via Visual Studio:**
1. Open `IndustrialProcessingSystem/IndustrialProcessingSystem.sln`
2. Build with `Ctrl+Shift+B`
3. Run with `Ctrl+F5` (Start Without Debugging)

**Via command line:**
```bash
cd IndustrialProcessingSystem
msbuild IndustrialProcessingSystem.sln /p:Configuration=Debug
```

### Run

```bash
cd IndustrialProcessingSystem/IndustrialProcessingSystem/bin/Debug
IndustrialProcessingSystem.exe
```

The application runs continuously, processing jobs and generating reports. Press **Enter** to initiate graceful shutdown.

---

## Sample Output

```
WorkerCount: 5
MaxQueueSize: 100
Initial jobs: 5
===================================
Submitting initial jobs from config...
  Submitted [1] Prime - numbers:10_000,threads:3 (Id: a1b2c3d4-...)
  Submitted [2] Prime - numbers:20_000,threads:2 (Id: e5f6a7b8-...)
  Submitted [3] IO - delay:1_000 (Id: c9d0e1f2-...)
  Submitted [3] IO - delay:3_000 (Id: 1a2b3c4d-...)
  Submitted [3] IO - delay:15_000 (Id: 5e6f7a8b-...)
===================================
Starting 5 producer threads...
System running. Press Enter to exit.
[Worker-0] COMPLETED Prime job a1b2c3d4-... = 1229 (attempt 1)
[Worker-1] COMPLETED IO job c9d0e1f2-... = 42 (attempt 1)
[Worker-2] FAILED IO job 1a2b3c4d-... - Timeout (attempt 1/3)
[Worker-2] FAILED IO job 1a2b3c4d-... - Timeout (attempt 2/3)
[Worker-2] FAILED IO job 1a2b3c4d-... - Timeout (attempt 3/3)
[Worker-2] ABORT IO job 1a2b3c4d-...
[Producer-0] Submitted Prime [3] - numbers:4521,threads:4
[Producer-2] Submitted IO [1] - delay:750
[REPORT] Generated report_0.xml
```

---

## Generated Files

These files are created at runtime in the executable's directory and are excluded from version control via `.gitignore`:

| File | Description |
|---|---|
| `jobs.log` | Append-only log of all `COMPLETED`, `FAILED`, and `ABORT` events with timestamps |
| `report_0.xml` ... `report_9.xml` | Circular XML reports with LINQ-aggregated execution statistics, generated every 60 seconds |

---

## Tech Stack

| Component | Technology |
|---|---|
| Language | C# 7.0 |
| Framework | .NET Framework 4.7.2 |
| IDE | Visual Studio 2017+ |
| XML Parsing | LINQ to XML (`System.Xml.Linq`) |
| Concurrency | `Thread`, `Task`, `SemaphoreSlim`, `ConcurrentDictionary`, `Interlocked`, `ThreadLocal<T>` |
| Async Patterns | `TaskCompletionSource<int>`, `Task.WhenAny`, `async/await` |
| Configuration | Custom XML (`SystemConfig.xml`) |
| Reporting | LINQ queries + `XDocument` XML generation |
