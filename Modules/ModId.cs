using System;
using System.Security.Cryptography;
using System.Text;

namespace NetworkingLibrary.Modules
{
    public static class ModId
    {
        public static uint FromGuid(string guid)
        {
            var bytes = MD5.Create().ComputeHash(Encoding.UTF8.GetBytes(guid.ToLowerInvariant()));
            return BitConverter.ToUInt32(bytes, 0);
        }
    }
}