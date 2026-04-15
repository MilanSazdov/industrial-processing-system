using System;

namespace IndustrialProcessingSystem
{
    public class Job
    {
        public Guid Id { get; set; }
        public JobType Type { get; set; }
        public string Payload { get; set; }
        public int Priority { get; set; }

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
