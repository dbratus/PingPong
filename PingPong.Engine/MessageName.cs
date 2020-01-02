using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NLog;

namespace PingPong.Engine
{
    public static class MessageName
    {
        private static readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        public static (long, long) GetHash(string messageTypeName)
        {
            using var md5 = MD5.Create();

            byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(messageTypeName));
            var hashMem = new Memory<byte>(hash);

            long lo = BinaryPrimitives.ReadInt64LittleEndian(hashMem.Slice(0, 8).Span);
            long hi = BinaryPrimitives.ReadInt64LittleEndian(hashMem.Slice(8, 8).Span);

            return (lo, hi);
        }

        public static string HashToString(long lo, long hi) =>
            hi.ToString("X") + lo.ToString("X");

        public static Dictionary<(long, long), Type> FindMessageTypes()
        {
            var result = new Dictionary<(long, long), Type>();
            var startedAt = DateTime.Now;

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name.StartsWith("System."))
                    continue;
                
                if (assembly.IsDynamic)
                    continue;

                foreach (Type type in assembly.GetExportedTypes())
                {
                    if (!type.IsClass || type.IsAbstract)
                        continue;

                    var hash = MessageName.GetHash(type.AssemblyQualifiedName);

                    if (!result.ContainsKey(hash))
                        result.Add(hash, type);
                }
            }

            _logger.Trace(
                "Found {0} potential message types in the current application domain. Time spent {1}.", 
                result.Count,
                DateTime.Now - startedAt
            );

            return result;
        }
    }
}