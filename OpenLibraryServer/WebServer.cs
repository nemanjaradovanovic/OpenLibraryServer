using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Web;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OpenLibraryServer
{
    public sealed class WebServer : IDisposable
    {
        private readonly HttpListener _listener = new HttpListener();
        private readonly OpenLibraryClient _client = new OpenLibraryClient();
        private readonly ResponseCache _cache = new ResponseCache(TimeSpan.FromMinutes(5));
        private readonly object _consoleLock = new object();

        
        private Thread _acceptThread;
        private volatile bool _running;

        
        private sealed class BookItem
        {
            public string Title { get; set; }
            public string Author { get; set; } // prvi autor 
            public System.Collections.Generic.List<string> Authors { get; set; } // svi autori
            public int? FirstYear { get; set; }
            public string WorkKey { get; set; }
        }

        private sealed class SearchProjection
        {
            public System.Collections.Generic.Dictionary<string, string> Query { get; set; }
            public int Total { get; set; }
            public System.Collections.Generic.List<BookItem> Items { get; set; }
        }

        private sealed class ClientVisibleException : Exception
        {
            public int StatusCode { get; }
            public ClientVisibleException(int code, string msg) : base(msg) => StatusCode = code;
        }

        public WebServer(string prefix)
        {
            _listener.Prefixes.Add(prefix);
        }

        public void Start()
        {
            _listener.Start();
            _running = true;

            _acceptThread = new Thread(AcceptLoopThread)
            {
                IsBackground = true,
                Name = "Http-Acceptor"
            };
            _acceptThread.Start();
        }

        private void AcceptLoopThread()
        {
            while (_running)
            {
                HttpListenerContext ctx = null;
                try
                {
                    
                    ctx = _listener.GetContext();
                }
                catch (ObjectDisposedException) { break; }
                catch (HttpListenerException) { break; }
                catch (Exception ex) { SafeLog($"ACCEPT ERROR: {ex}"); }

                if (ctx == null) continue;

                // za svaku konekciju koristimo ThreadPool (klasični WaitCallback)
                ThreadPool.UnsafeQueueUserWorkItem(HandleRequest, ctx);
            }
        }

        private void HandleRequest(object state)
        {
            var ctx = (HttpListenerContext)state;
            var req = ctx.Request;
            var res = ctx.Response;
            var sw = Stopwatch.StartNew();
            var threadId = Thread.CurrentThread.ManagedThreadId;

            int status = 200;

            try
            {
                if (req.HttpMethod != "GET")
                {
                    status = 405;
                    WriteText(ctx, status, "Only GET is allowed.", "text/plain");
                    return;
                }

                var path = req.Url?.AbsolutePath?.TrimEnd('/').ToLowerInvariant() ?? "/";

                if (path == "" || path == "/")
                {
                    WriteHtml(ctx, BuildLandingHtml());
                    return;
                }

                if (path == "/health")
                {
                    WriteJson(ctx, new { status = "ok", cache = _cache.Stats, time = DateTime.UtcNow });
                    return;
                }

                if (path == "/search")
                {
                    var qs = HttpUtility.ParseQueryString(req.Url.Query);
                    var forward = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    string[] allowed = { "q", "author", "title", "subject", "isbn", "publisher", "sort", "page", "limit", "fields", "lang", "offset" };

                    foreach (var key in allowed)
                    {
                        var v = qs[key];
                        if (!string.IsNullOrWhiteSpace(v))
                            forward[key] = v;
                    }

                    bool wantHtml = string.Equals(qs["format"], "html", StringComparison.OrdinalIgnoreCase);

                    if (forward.Count == 0)
                    {
                        status = 400;
                        if (wantHtml) WriteHtml(ctx, BuildErrorHtml("Provide at least one filter (q, author, or title)."));
                        else WriteJson(ctx, new { error = "Provide at least one filter (q, author, or title).", status });
                        return;
                    }

                    var cacheKey = BuildCacheKey(forward, wantHtml);
                    if (_cache.TryGet(cacheKey, out var cached))
                    {
                        WriteBytes(ctx, 200, cached, wantHtml ? "text/html; charset=utf-8" : "application/json; charset=utf-8");
                        return;
                    }

                    var proj = FetchProjectionSync(forward);

                    byte[] payload;
                    string contentType;

                    if (wantHtml)
                    {
                        var html = BuildResultsHtml(proj);
                        payload = Encoding.UTF8.GetBytes(html);
                        contentType = "text/html; charset=utf-8";
                    }
                    else
                    {
                        var responseObj = new
                        {
                            ok = true,
                            query = proj.Query,
                            total = proj.Total,
                            count = proj.Items.Count,
                            items = proj.Items.Select(x => new
                            {
                                title = x.Title,
                                author = x.Author,      // prvi autor
                                authors = x.Authors,    // svi autori
                                first_publish_year = x.FirstYear,
                                work_key = x.WorkKey
                            }).ToList()
                        };
                        payload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(responseObj, Formatting.Indented));
                        contentType = "application/json; charset=utf-8";
                    }

                    _cache.Set(cacheKey, payload);
                    WriteBytes(ctx, 200, payload, contentType);
                    return;
                }

                status = 404;
                WriteText(ctx, status, "Not Found", "text/plain");
            }
            catch (ClientVisibleException ex)
            {
                status = ex.StatusCode;
                WriteJson(ctx, new { error = ex.Message, status });
            }
            catch (WebException wex)
            {
                var http = wex.Response as HttpWebResponse;
                if (http != null)
                {
                    int upstream = (int)http.StatusCode;

                    if (upstream >= 500 || upstream == 429)
                    {
                        status = 502;
                        WriteJson(ctx, new
                        {
                            error = "Upstream service unavailable. Please try again.",
                            upstream_status = upstream,
                            status
                        });
                    }
                    else
                    {
                        status = upstream;
                        WriteJson(ctx, new
                        {
                            error = "Upstream error.",
                            upstream_status = upstream,
                            status
                        });
                    }
                }
                else
                {
                    // Nema HTTP odgovora 
                    status = 502;
                    WriteJson(ctx, new { error = "Network error contacting Open Library.", status });
                }
            }
            catch (Exception ex)
            {
                status = 500;
                WriteJson(ctx, new { error = "Internal server error.", detail = ex.Message, status });
            }
            finally
            {
                sw.Stop();
                var ms = (int)sw.ElapsedMilliseconds;
                var ip = req.RemoteEndPoint != null ? req.RemoteEndPoint.Address.ToString() : "-";
                SafeLog($"{DateTime.Now:HH:mm:ss} | {req.HttpMethod} {req.RawUrl} | {status} | thr={threadId} | {ms} ms | ip={ip}");
                try { res.Close(); } catch { }
            }
        }

        //Sinhroni helperi 

        private SearchProjection FetchProjectionSync(System.Collections.Generic.Dictionary<string, string> forward)
        {
            var apiJsonBytes = _client.Search(forward, timeoutMilliseconds: 15000);
            var apiJson = Encoding.UTF8.GetString(apiJsonBytes);

            var root = JObject.Parse(apiJson);
            int numFound = (root.Value<int?>("numFound") ?? root.Value<int?>("num_found")) ?? 0;
            if (numFound <= 0)
                throw new ClientVisibleException(404, "No books found for given filters.");

            var items = new System.Collections.Generic.List<BookItem>();
            var docs = root["docs"] as JArray;
            if (docs != null)
            {
                foreach (var d in docs.Take(50))
                {
                    string title = d.Value<string>("title");
                    int? firstYear = d.Value<int?>("first_publish_year");
                    string workKey = d.Value<string>("key");

                    var authors = new System.Collections.Generic.List<string>();
                    var an = d["author_name"] as JArray;
                    if (an != null && an.Count > 0)
                    {
                        foreach (var a in an)
                        {
                            var s = a.ToString();
                            if (!string.IsNullOrWhiteSpace(s)) authors.Add(s);
                        }
                    }

                    items.Add(new BookItem
                    {
                        Title = title,
                        Author = authors.FirstOrDefault(),
                        Authors = authors,
                        FirstYear = firstYear,
                        WorkKey = workKey
                    });
                }
            }

            return new SearchProjection
            {
                Query = new System.Collections.Generic.Dictionary<string, string>(forward, StringComparer.OrdinalIgnoreCase),
                Total = numFound,
                Items = items
            };
        }

        private static string BuildCacheKey(System.Collections.Generic.IDictionary<string, string> forward, bool wantHtml)
        {
            var sb = new StringBuilder("search:");
            foreach (var kv in forward.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                sb.Append(kv.Key).Append('=').Append(kv.Value).Append('&');
            sb.Append("format=").Append(wantHtml ? "html" : "json");
            return sb.ToString();
        }

        private static string BuildResultsHtml(SearchProjection proj)
        {
            string H(string s) => HttpUtility.HtmlEncode(s ?? "");

            var sb = new StringBuilder();
            sb.AppendLine("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"/>");
            sb.AppendLine("<title>OpenLibrary Results</title>");
            sb.AppendLine("<style>");
            sb.AppendLine("body{font-family:Segoe UI,Arial,sans-serif;margin:2rem;line-height:1.5}");
            sb.AppendLine("table{border-collapse:collapse;width:100%;margin-top:1rem}");
            sb.AppendLine("th,td{border:1px solid #ddd;padding:.5rem;text-align:left}");
            sb.AppendLine("th{background:#f4f4f4}");
            sb.AppendLine(".muted{color:#666}");
            sb.AppendLine("</style>");
            sb.AppendLine("<h1>Search results</h1>");

           
            sb.Append("<p class=\"muted\">Filters: ");
            sb.Append(string.Join(", ", proj.Query.Select(kv => $"{H(kv.Key)}=<b>{H(kv.Value)}</b>")));
            sb.Append("</p>");

            sb.AppendFormat("<p>Total found: <b>{0}</b> • Showing: <b>{1}</b></p>", proj.Total, proj.Items.Count);

            sb.AppendLine("<table>");
            sb.AppendLine("<tr><th>Title</th><th>Author</th><th>First publish year</th></tr>");

            
            proj.Query.TryGetValue("author", out var authorFilter);
            var tokens = SplitAuthorFilter(authorFilter);

            foreach (var it in proj.Items)
            {
                var url = !string.IsNullOrWhiteSpace(it.WorkKey) ? ("https://openlibrary.org" + it.WorkKey) : "#";
                sb.Append("<tr>");
                sb.AppendFormat("<td><a href=\"{0}\" target=\"_blank\" rel=\"noopener\">{1}</a></td>", H(url), H(it.Title));

                var authorList = (it.Authors != null && it.Authors.Count > 0)
                    ? string.Join(", ", it.Authors.Select(a => RenderAuthor(a, tokens)))
                    : "";

                sb.AppendFormat("<td>{0}</td>", authorList);
                sb.AppendFormat("<td>{0}</td>", it.FirstYear.HasValue ? it.FirstYear.Value.ToString() : "");
                sb.Append("</tr>");
            }

            sb.AppendLine("</table>");
            sb.AppendLine("<p class=\"muted\">Add <code>&format=html</code> to get this HTML view. Remove it to get JSON.</p>");
            sb.AppendLine("</html>");
            return sb.ToString();
        }

        private static string[] SplitAuthorFilter(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
            return raw.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static string RenderAuthor(string name, string[] tokens)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            if (tokens == null || tokens.Length == 0)
                return HttpUtility.HtmlEncode(name);

            bool match = tokens.Any(t => name.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
            var encoded = HttpUtility.HtmlEncode(name);
            return match ? $"<b>{encoded}</b>" : encoded;
        }

        private static void WriteText(HttpListenerContext ctx, int status, string text, string contentType)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            WriteBytes(ctx, status, bytes, contentType + "; charset=utf-8");
        }

        private static void WriteHtml(HttpListenerContext ctx, string html)
            => WriteText(ctx, 200, html, "text/html");

        private static void WriteJson(HttpListenerContext ctx, object obj)
        {
            var payload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(obj, Formatting.Indented));
            WriteBytes(ctx, 200, payload, "application/json; charset=utf-8");
        }

        private static void WriteBytes(HttpListenerContext ctx, int status, byte[] payload, string contentType)
        {
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = contentType;
            ctx.Response.ContentEncoding = Encoding.UTF8;
            ctx.Response.ContentLength64 = payload.LongLength;
            ctx.Response.OutputStream.Write(payload, 0, payload.Length);
        }

        private static string BuildLandingHtml()
        {
            return @"<!doctype html>
<html lang=""en"">
<meta charset=""utf-8""/>
<title>OpenLibrary Server (.NET Framework - Classic Threads)</title>
<style>
 body { font-family: Segoe UI, Arial, sans-serif; margin: 2rem; line-height:1.5 }
 input,select { padding:.35rem; font-size:1rem; }
 .row { margin:.5rem 0 }
 code { background:#f5f5f5; padding:.15rem .35rem; border-radius:.25rem }
 small { color:#666 }
</style>

<h1>OpenLibrary Multithreaded Server — Classic Threads</h1>
<p>
  Endpoints: <code>/search</code>, <code>/health</code> • In-memory cache •
  HTML view (<code>&format=html</code>).
</p>

<h2>Primeri</h2>
<ul>
  <li><a href=""/search?author=tolkien&sort=new"">/search?author=tolkien&sort=new</a></li>
  <li><a href=""/search?author=tolkien&sort=new&format=html"">/search?author=tolkien&sort=new&format=html</a></li>
  <li><a href=""/search?q=harry%20potter&limit=5"">/search?q=harry%20potter&limit=5</a></li>
</ul>

<h2>Probaj ovde</h2>
<form method=""get"" action=""/search"">
  <div class=""row"">q: <input name=""q"" placeholder=""free text""/></div>
  <div class=""row"">author: <input name=""author"" placeholder=""npr. tolkien""/></div>
  <div class=""row"">title: <input name=""title"" placeholder=""npr. dune""/></div>
  <div class=""row"">subject: <input name=""subject"" placeholder=""optional""/></div>
  <div class=""row"">
    sort:
    <select name=""sort"">
      <option value="""">(default)</option>
      <option>new</option><option>old</option><option>random</option><option>key</option>
    </select>
    page: <input name=""page"" type=""number"" min=""1"" value=""1"" style=""width:5rem""/>
    limit: <input name=""limit"" type=""number"" min=""1"" max=""100"" value=""20"" style=""width:5rem""/>
  </div>
  <div class=""row"">
    <label><input type=""checkbox"" name=""format"" value=""html""> HTML view</label>
  </div>
  <button type=""submit"">Search</button>
</form>

<p>Health: <a href=""/health"">/health</a></p>
</html>";
        }

        private static string BuildErrorHtml(string message)
        {
            string H(string s) => HttpUtility.HtmlEncode(s ?? "");
            return $@"<!doctype html>
<html lang=""en"">
<meta charset=""utf-8""/>
<title>OpenLibrary - Error</title>
<style>
body{{font-family:Segoe UI,Arial,sans-serif;margin:2rem;line-height:1.5}}
.alert{{background:#ffecec;border:1px solid #f5c2c7;padding:1rem;border-radius:.5rem;color:#842029}}
a{{color:#0b5ed7;text-decoration:none}}
a:hover{{text-decoration:underline}}
</style>
<h1>Search error</h1>
<p class=""alert"">{H(message)}</p>
<p><a href=""/"">&larr; Back</a></p>
</html>";
        }

        private void SafeLog(string line)
        {
            lock (_consoleLock)
            {
                Console.WriteLine(line);
            }
        }

        public void Dispose()
        {
            _running = false;
            try { _listener.Stop(); } catch { }
            try { _acceptThread?.Join(1000); } catch { }
            _listener.Close();
            _client.Dispose();
            _cache.Dispose();
        }
    }
}
