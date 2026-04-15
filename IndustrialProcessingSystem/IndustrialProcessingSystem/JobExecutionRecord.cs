using System;

namespace IndustrialProcessingSystem
{
    public class JobExecutionRecord
    {
        public Guid JobId { get; set; }
        public JobType Type { get; set; }
        public bool Success { get; set; }
        public int Result { get; set; }
        public TimeSpan Duration { get; set; }
        public DateTime CompletedAt { get; set; }
    }
}
