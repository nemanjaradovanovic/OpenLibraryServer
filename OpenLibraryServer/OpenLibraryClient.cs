using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web;

namespace OpenLibraryServer
{
    public sealed class OpenLibraryClient : IDisposable
    {
        private static readonly Uri BaseUri = new Uri("https://openlibrary.org/search.json", UriKind.Absolute);

        public OpenLibraryClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        public byte[] Search(IDictionary<string, string> query, int timeoutMilliseconds = 15000)
        {
            var b = new StringBuilder();
            bool first = true;
            foreach (var kv in query)
            {
                if (!first) b.Append('&'); else first = false;
                b.Append(HttpUtility.UrlEncode(kv.Key));
                b.Append('=');
                b.Append(HttpUtility.UrlEncode(kv.Value));
            }

            var uri = new Uri(BaseUri + "?" + b.ToString());

            int attempts = 0;
            int maxAttempts = 3;
            int delayMs = 300; //backoff

            while (true)
            {
                attempts++;
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(uri);
                    req.Method = "GET";
                    req.Timeout = timeoutMilliseconds;
                    req.ReadWriteTimeout = timeoutMilliseconds;
                    req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                    req.UserAgent = "OpenLibraryServer/1.0 (+classic)";
                    req.Accept = "application/json";
                    req.KeepAlive = true;

                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var stream = resp.GetResponseStream())
                    {
                        if (resp.StatusCode != HttpStatusCode.OK)
                            throw new WebException($"Open Library returned {(int)resp.StatusCode}.", null, WebExceptionStatus.ProtocolError, resp);

                        using (var ms = new MemoryStream())
                        {
                            stream.CopyTo(ms);
                            return ms.ToArray();
                        }
                    }
                }
                catch (WebException wex)
                {
                    var http = wex.Response as HttpWebResponse;

                    if (http != null)
                    {
                        int code = (int)http.StatusCode;
                        bool transient = code >= 500 || code == 429;
                        if (transient && attempts < maxAttempts)
                        {
                            Thread.Sleep(delayMs);
                            delayMs *= 2;
                            continue;
                        }
                    }
                    else
                    {
                        if (attempts < maxAttempts)
                        {
                            Thread.Sleep(delayMs);
                            delayMs *= 2;
                            continue;
                        }
                    }

                    throw;
                }
            }
        }


        public void Dispose()
        {
            //ništa za osloboditi 
        }
    }
}
