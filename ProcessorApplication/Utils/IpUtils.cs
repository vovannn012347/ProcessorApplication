using System;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace ProcessorApplication.Utils
{
    public static class IpUtils
    {
        public static bool IsLocalIp(IPAddress? LocalIp, IPAddress? RemoteIp)
        {
            if(RemoteIp != null)
            {

                bool isLocalIp = IPAddress.IsLoopback(RemoteIp) || (LocalIp != null && RemoteIp.Equals(LocalIp));

                // 2. Optional: Allow IPv4 localhost manually
                if (RemoteIp?.ToString() == "::1" || RemoteIp?.ToString() == "127.0.0.1")
                {
                    isLocalIp = true;
                }

                return isLocalIp;
            }

            return true;
        }
        
    }
}
