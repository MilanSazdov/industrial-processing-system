using System;

namespace IndustrialProcessingSystem
{
    public class Job
    {
        public Guid Id { get; }
        public JobType Type { get; }
        public string Payload { get; }
        public int Priority { get; }

        public Job(JobType type, string payload, int priority)
        {
            Id = Guid.NewGuid();
            Type = type;
            Payload = payload;
            Priority = priority;
        }

        public Job(Guid id, JobType type, string payload, int priority)
        {
            Id = id;
            Type = type;
            Payload = payload;
            Priority = priority;
        }
    }
}
