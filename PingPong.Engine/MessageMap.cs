using System;
using System.Collections.Generic;
using System.Linq;

namespace PingPong.Engine
{
    sealed class MessageMap
    {
        private readonly Dictionary<int, Type> _messageTypesById = new Dictionary<int, Type>();
        private readonly Dictionary<Type, int> _messageIdsByType = new Dictionary<Type, int>();
        
        public Type GetMessageTypeById(int messageId) =>
            _messageTypesById[messageId];

        public int GetMessageIdByType(Type messageType) =>
            _messageIdsByType[messageType];

        public IEnumerable<(Type Type, int Id)> Enumerate() =>
            _messageIdsByType.Select(kv => (kv.Key, kv.Value));

        public bool ContainsType(Type messageType) =>
            _messageIdsByType.ContainsKey(messageType);

        public void Add(Type messageType, int messageId)
        {
            _messageIdsByType.Add(messageType, messageId);
            _messageTypesById.Add(messageId, messageType);
        }
    }
}