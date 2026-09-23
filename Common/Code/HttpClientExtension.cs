using System.Text;

using Common.Interfaces;

namespace Common.Code;
public static class HttpClientExtension
{
    static readonly string[] protocols = new string[]{ "https", "http" };
    public static async Task<HttpResponseMessage?> GetHttpAsync(
        this HttpClient client,
        string url,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead,
        Version? httpVersion = null,
        HttpVersionPolicy? versionPolicy = null,
        IDictionary<string, string>? headers = null,
        TimeSpan? timeout = null,
        bool ensureSuccessStatusCode = false,
        CancellationToken ct = default)
    {
        foreach (var proto in protocols)
        {
            try
            {
                string fullUrl = url.Contains("://")
                    ? url.Replace(url.Split("://")[0], proto)
                    : $"{proto}://{url}";

                using var request =
                    new HttpRequestMessage(HttpMethod.Get, fullUrl);

                if (httpVersion != null)
                    request.Version = httpVersion;

                if (versionPolicy != null)
                    request.VersionPolicy = versionPolicy.Value;

                if (headers != null)
                {
                    foreach (var header in headers)
                    {
                        request.Headers.TryAddWithoutValidation(
                            header.Key,
                            header.Value);
                    }
                }

                CancellationTokenSource? timeoutSource = null;
                CancellationTokenSource? linkedSource = null;

                try
                {
                    var token = ct;

                    if (timeout.HasValue)
                    {
                        timeoutSource =
                            new CancellationTokenSource(timeout.Value);

                        linkedSource =
                            CancellationTokenSource.CreateLinkedTokenSource(
                                ct,
                                timeoutSource.Token);

                        token = linkedSource.Token;
                    }

                    var response =
                        await client.SendAsync(
                            request,
                            completionOption,
                            token);

                    if (ensureSuccessStatusCode)
                        response.EnsureSuccessStatusCode();

                    if (response.IsSuccessStatusCode)
                        return response;

                    response.Dispose();
                }
                finally
                {
                    linkedSource?.Dispose();
                    timeoutSource?.Dispose();
                }
            }
            catch
            {
                continue;
            }
        }

        return null;
    }
}