using System;
using System.Text;

namespace NetworkingLibrary.Modules
{
    public static class StringId
    {
        public static uint Map(string s)
        {
            const uint FNV_OFFSET = 2166136261u;
            const uint FNV_PRIME = 16777619u;
            uint hash = FNV_OFFSET;
            var data = Encoding.UTF8.GetBytes(s);
            foreach (var b in data)
            {
                hash ^= b;
                hash *= FNV_PRIME;
            }
            return hash;
        }
    }
}