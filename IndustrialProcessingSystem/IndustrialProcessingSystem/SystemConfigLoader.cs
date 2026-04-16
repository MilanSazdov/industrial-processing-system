using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace IndustrialProcessingSystem
{
    public class SystemConfig
    {
        public int WorkerCount { get; set; }
        public int MaxQueueSize { get; set; }
        public List<Job> InitialJobs { get; set; }

        public SystemConfig()
        {
            InitialJobs = new List<Job>();
        }
    }

    public static class SystemConfigLoader
    {
        public static SystemConfig Load(string path)
        {
            XElement root = XElement.Load(path);

            int workerCount = int.Parse(root.Element("WorkerCount").Value);
            int maxQueueSize = int.Parse(root.Element("MaxQueueSize").Value);

            List<Job> jobs = (from jobElement in root.Element("Jobs").Descendants("Job")
                              let type = (JobType)Enum.Parse(typeof(JobType), jobElement.Attribute("Type").Value)
                              let payload = jobElement.Attribute("Payload").Value
                              let priority = int.Parse(jobElement.Attribute("Priority").Value)
                              select new Job(type, payload, priority)).ToList();

            return new SystemConfig
            {
                WorkerCount = workerCount,
                MaxQueueSize = maxQueueSize,
                InitialJobs = jobs
            };
        }
    }
}
