using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FetchTree
{
    public sealed class Entry
    {
        public string Name { get; set; }
        public string Url { get; set; }
        public string RelativePath { get; set; }
        public bool IsDirectory { get; set; }
        public string Status { get; set; }
        public string Error { get; set; }
        public List<Entry> Children { get; set; }
        public Entry() { Children = new List<Entry>(); }
    }

    public sealed class ScanResult
    {
        public int SchemaVersion { get; set; }
        public string StartedUtc { get; set; }
        public int Depth { get; set; }
        public int PagesRead { get; set; }
        public int FilesFound { get; set; }
        public int FoldersFound { get; set; }
        public bool Cancelled { get; set; }
        public bool Limited { get; set; }
        public int Errors { get; set; }
        public Entry Root { get; set; }
    }

    public sealed class Scanner
    {
        private readonly HttpClient client;
        private const int MaxPageBytes = 4 * 1024 * 1024;
        private static readonly Regex Noise = new Regex(@"<!--.*?-->|<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(2));
        private static readonly Regex Anchors = new Regex(@"<a\b(?:[^>""']|""[^""]*""|'[^']*')*>", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        private static readonly Regex Href = new Regex(@"\s+href\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        private static readonly Regex Rows = new Regex(@"<tr\b[^>]*>.*?</tr\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(2));
        private static readonly Regex FolderIcon = new Regex(@"<img\b[^>]*(?:/(?:folder|dir)\.(?:gif|png|svg)|\[DIR\])[^>]*>", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));

        public Scanner(HttpClient client) { this.client = client; }

        public static Uri NormalizeRoot(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https") || uri.UserInfo.Length != 0 ||
                uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new ArgumentException("Enter an HTTP or HTTPS directory URL without credentials, a query, or a fragment.");
            return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
        }

        public static bool InScope(Uri root, Uri target)
        {
            return target.Scheme == root.Scheme && target.Authority == root.Authority &&
                target.UserInfo.Length == 0 && target.Query.Length == 0 && target.Fragment.Length == 0 &&
                target.AbsolutePath.StartsWith(root.AbsolutePath, StringComparison.Ordinal);
        }

        // Apache/nginx use trailing slashes; ArduPilot uses folder icons in table rows.
        // Only immediate children are accepted: navigation, sort links and aliases cannot escape the tree.
        public static List<Entry> Parse(Uri root, Uri page, string html)
        {
            var result = new List<Entry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string cleaned = Noise.Replace(html, "");
            var folderLinks = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match row in Rows.Matches(cleaned))
            {
                if (!FolderIcon.IsMatch(row.Value)) continue;
                foreach (Match anchor in Anchors.Matches(row.Value))
                {
                    var attr = Href.Match(anchor.Value);
                    if (attr.Success) folderLinks.Add(WebUtility.HtmlDecode(attr.Groups["v"].Value).Trim());
                }
            }
            foreach (Match anchor in Anchors.Matches(cleaned))
            {
                var attr = Href.Match(anchor.Value);
                if (!attr.Success) continue;
                var href = WebUtility.HtmlDecode(attr.Groups["v"].Value).Trim();
                Uri target;
                if (href.Length == 0 || href.Contains("\\") || !Uri.TryCreate(page, href, out target) || !InScope(root, target)) continue;
                if (folderLinks.Contains(href) && !target.AbsolutePath.EndsWith("/", StringComparison.Ordinal)) target = new Uri(target.AbsoluteUri + "/");
                if (!target.AbsolutePath.StartsWith(page.AbsolutePath, StringComparison.Ordinal)) continue;
                string tail = target.AbsolutePath.Substring(page.AbsolutePath.Length);
                bool directory = tail.EndsWith("/", StringComparison.Ordinal);
                string segment = directory ? tail.Substring(0, tail.Length - 1) : tail;
                string name = Uri.UnescapeDataString(segment);
                if (segment.Length == 0 || segment.Contains("/") || name.Contains("/") || name.Contains("\\") ||
                    name == "." || name == ".." || name.Any(char.IsControl) || !seen.Add(target.AbsoluteUri)) continue;
                result.Add(new Entry { Name = name, Url = target.AbsoluteUri,
                    RelativePath = Uri.UnescapeDataString(target.AbsolutePath.Substring(root.AbsolutePath.Length)),
                    IsDirectory = directory, Status = directory ? "NotScanned" : "Discovered" });
            }
            return result.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private async Task<string> ReadPage(Uri root, Uri page, CancellationToken token)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                Uri current = page;
                for (int redirect = 0; redirect <= 5; redirect++)
                {
                    using (var response = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, timeout.Token))
                    {
                        int status = (int)response.StatusCode;
                        if (status >= 300 && status <= 399)
                        {
                            var location = response.Headers.Location;
                            if (location == null) throw new IOException("Redirect without a destination.");
                            Uri next = location.IsAbsoluteUri ? location : new Uri(current, location);
                            // A redirected alias could change hierarchy or create loops. Report it explicitly.
                            if (next.Scheme != root.Scheme || next.Authority != root.Authority || next.UserInfo.Length != 0 || next.Query.Length != 0 || next.Fragment.Length != 0 ||
                                next.AbsolutePath.TrimEnd('/') != page.AbsolutePath.TrimEnd('/'))
                                throw new IOException("Redirect changes directory or leaves the selected root: " + next);
                            current = next;
                            continue;
                        }
                        response.EnsureSuccessStatusCode();
                        string media = response.Content.Headers.ContentType == null ? "" : response.Content.Headers.ContentType.MediaType;
                        if (media != "text/html" && media != "application/xhtml+xml")
                            throw new IOException("Expected an HTML directory listing; received " + media + ".");
                        if (response.Content.Headers.ContentLength > MaxPageBytes) throw new IOException("Listing exceeds the 4 MB page limit.");
                        using (var stream = await response.Content.ReadAsStreamAsync())
                        using (var memory = new MemoryStream())
                        {
                            byte[] buffer = new byte[16384];
                            int count;
                            while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, timeout.Token)) != 0)
                            {
                                if (memory.Length + count > MaxPageBytes) throw new IOException("Listing exceeds the 4 MB page limit.");
                                memory.Write(buffer, 0, count);
                            }
                            memory.Position = 0;
                            var encoding = System.Text.Encoding.UTF8;
                            string charset = response.Content.Headers.ContentType.CharSet;
                            if (!string.IsNullOrEmpty(charset)) encoding = System.Text.Encoding.GetEncoding(charset.Trim('"'));
                            using (var reader = new StreamReader(memory, encoding, true)) return reader.ReadToEnd();
                        }
                    }
                }
                throw new IOException("Too many redirects.");
            }
        }

        public async Task<ScanResult> ScanAsync(Uri root, int depth, int maxPages, int maxEntries, CancellationToken token, Action<string> progress)
        {
            if (depth < 0 || depth > 100 || maxPages < 1 || maxEntries < 1) throw new ArgumentOutOfRangeException("Invalid scan limits.");
            var report = new ScanResult { SchemaVersion = 1, StartedUtc = DateTime.UtcNow.ToString("o"), Depth = depth,
                Root = new Entry { Name = root.AbsoluteUri, Url = root.AbsoluteUri, RelativePath = "", IsDirectory = true, Status = "NotScanned" } };
            var queue = new Queue<Tuple<Entry, int>>();
            queue.Enqueue(Tuple.Create(report.Root, 0));
            int attempted = 0;
            while (queue.Count > 0)
            {
                if (token.IsCancellationRequested) { report.Cancelled = true; break; }
                var work = queue.Dequeue();
                Entry node = work.Item1;
                if (attempted >= maxPages) { node.Status = "PageLimit"; report.Limited = true; continue; }
                attempted++;
                try
                {
                    if (progress != null) progress("Scanning " + node.Url);
                    string html = await ReadPage(root, new Uri(node.Url), token);
                    report.PagesRead++;
                    var children = Parse(root, new Uri(node.Url), html);
                    int remaining = maxEntries - report.FilesFound - report.FoldersFound;
                    bool truncated = children.Count > remaining;
                    node.Children = children.Take(remaining).ToList();
                    node.Status = truncated ? "EntryLimit" : "Scanned";
                    report.Limited |= truncated;
                    foreach (var child in node.Children)
                    {
                        if (!child.IsDirectory) { report.FilesFound++; continue; }
                        report.FoldersFound++;
                        if (work.Item2 < depth) queue.Enqueue(Tuple.Create(child, work.Item2 + 1));
                        else child.Status = "DepthLimit";
                    }
                }
                catch (OperationCanceledException)
                {
                    if (token.IsCancellationRequested) { node.Status = "Cancelled"; report.Cancelled = true; break; }
                    node.Status = "Error"; node.Error = "Request timed out after 20 seconds."; report.Errors++;
                }
                catch (Exception ex)
                {
                    if (!(ex is HttpRequestException || ex is IOException || ex is ArgumentException || ex is RegexMatchTimeoutException)) throw;
                    node.Status = "Error"; node.Error = ex.GetBaseException().Message; report.Errors++;
                }
            }
            return report;
        }
    }
}
