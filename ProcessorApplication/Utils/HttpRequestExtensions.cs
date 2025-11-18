using System;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace ProcessorApplication.Utils
{
    public static class HttpRequestExtensions
    {
        public static bool IsAjaxRequest(this HttpRequest request)
        {
            return request.Headers["X-Requested-With"] == "XMLHttpRequest";
        }
    }
}
