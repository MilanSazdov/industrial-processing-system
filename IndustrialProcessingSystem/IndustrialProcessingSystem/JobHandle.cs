using System;
using System.Threading.Tasks;

namespace IndustrialProcessingSystem
{
    public class JobHandle
    {
        public Guid Id { get; }
        public Task<int> Result { get; }

        internal JobHandle(Guid id, Task<int> result)
        {
            Id = id;
            Result = result;
        }
    }
}
