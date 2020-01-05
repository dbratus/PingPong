using System;

namespace PingPong.HostInterfaces
{
    public sealed class RequestResultException: Exception
    {
        public RequestResult Result { get; private set; }

        public RequestResultException(RequestResult result) :
            base($"Request failed {result}")
        {
            Result = result;
        }
    }
}