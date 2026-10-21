using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FetchTree;

internal sealed class FixtureHandler : HttpMessageHandler
{
    public int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Calls++;
        string path = request.RequestUri.AbsolutePath;
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        string html = "";
        if (path == "/root/") html = "<a href='board/'>board</a><a href='broken/'>broken</a><a href='redirect/'>redirect</a><a href='file.bin'>file</a>";
        if (path == "/root/board/") html = "<a href='nested/'>nested</a><a href='firmware.bin'>firmware</a><a href='../'>parent</a>";
        if (path == "/root/broken/") response.StatusCode = HttpStatusCode.Forbidden;
        if (path == "/root/redirect/") { response.StatusCode = HttpStatusCode.Redirect; response.Headers.Location = new Uri("https://elsewhere.test/"); }
        response.Content = new StringContent(html, Encoding.UTF8, "text/html");
        return Task.FromResult(response);
    }
}

internal static class ScannerTests
{
    private static int count;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        count++; Console.WriteLine("PASS: " + name);
    }
    private static void Main(string[] args)
    {
        Uri root = Scanner.NormalizeRoot("https://example.test/root");
        Check(root.AbsoluteUri == "https://example.test/root/", "root trailing slash");
        foreach (string bad in new [] { "file:///C:/", "ftp://example.test/", "https://user:password@example.test/", "https://example.test/?x=1" })
        {
            bool rejected = false;
            try { Scanner.NormalizeRoot(bad); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "reject invalid root " + bad.Split(':')[0]);
        }
        var parsed = Scanner.Parse(root, root, @"<!-- <a href='fake.bin'> -->
            <script>var a = '<a href=""fake2.bin"">';</script>
            <A class='x' HREF='folder/'>folder</A><a href=firmware.bin>file</a>
            <a href='firmware.bin'>duplicate</a><a href='a%20b.bin'>space</a><a href='x&amp;y.bin'>entity</a>
            <a href='../'>parent</a><a href='/root2/escape.bin'>sibling</a>
            <a href='https://other.test/root/no.bin'>external</a><a href='?C=N;O=D'>sort</a>
            <a href='#top'>fragment</a><a href='nested/deep.bin'>non-child</a>
            <a href='%2e%2e/escape.bin'>encoded parent</a><a href='a%2fb.bin'>encoded separator</a>
            <a href='a%5cb.bin'>encoded backslash</a><a href='null%00.bin'>control</a>");
        Check(parsed.Count == 4, "links filtered and deduplicated");
        Check(parsed[0].IsDirectory && parsed[0].Name == "folder", "folders sorted first");
        Check(parsed.Any(e => e.Name == "a b.bin") && parsed.Any(e => e.Name == "x&y.bin"), "URL and HTML decoding");
        var iconFolders = Scanner.Parse(root, root, "<table><tr><td><img src='/icons/folder.gif'></td><td><a href='/root/stable'>stable</a></td></tr></table>");
        Check(iconFolders.Count == 1 && iconFolders[0].IsDirectory && iconFolders[0].Url.EndsWith("/stable/"), "ArduPilot folders without trailing slash");
        using (var handler = new FixtureHandler())
        using (var client = new HttpClient(handler))
        {
            var scanner = new Scanner(client);
            var shallow = scanner.ScanAsync(root, 0, 100, 100, CancellationToken.None, null).Result;
            Check(handler.Calls == 1 && shallow.FilesFound == 1 && shallow.FoldersFound == 3, "depth zero reads only root");
            Check(shallow.Root.Children.Where(e => e.IsDirectory).All(e => e.Status == "DepthLimit"), "unscanned folders marked");
            var deep = scanner.ScanAsync(root, 1, 100, 100, CancellationToken.None, null).Result;
            Check(deep.FilesFound == 2 && deep.FoldersFound == 4 && deep.PagesRead == 2, "recursive discovery");
            Check(deep.Errors == 2, "403 and external redirect reported without stopping siblings");
            var limited = scanner.ScanAsync(root, 2, 1, 100, CancellationToken.None, null).Result;
            Check(limited.Limited && limited.PagesRead == 1, "page budget");
            limited = scanner.ScanAsync(root, 1, 100, 2, CancellationToken.None, null).Result;
            Check(limited.Limited && limited.FilesFound + limited.FoldersFound == 2, "entry budget");
            var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            var cancelled = scanner.ScanAsync(root, 1, 100, 100, cancellation.Token, null).Result;
            Check(cancelled.Cancelled && cancelled.PagesRead == 0, "cancellation returns partial report");
        }
        // Exercise a real HTTP connection, independent of internet availability.
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(() => {
            using (var connection = listener.AcceptTcpClient())
            using (var stream = connection.GetStream())
            {
                var reader = new StreamReader(stream);
                while (!string.IsNullOrEmpty(reader.ReadLine())) { }
                byte[] body = Encoding.UTF8.GetBytes("<a href='firmware.bin'>firmware</a>");
                byte[] headers = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                stream.Write(headers, 0, headers.Length); stream.Write(body, 0, body.Length);
            }
        });
        using (var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }))
        {
            var actual = new Scanner(client).ScanAsync(new Uri("http://127.0.0.1:" + port + "/"), 0, 10, 100, CancellationToken.None, null).Result;
            Check(actual.Errors == 0 && actual.FilesFound == 1, "real HTTP listing end to end");
        }
        server.Wait(); listener.Stop();
        if (args.Length > 0)
        {
            var real = Scanner.Parse(new Uri("https://firmware.ardupilot.org/Plane/"), new Uri("https://firmware.ardupilot.org/Plane/"), File.ReadAllText(args[0]));
            Check(real.Any(e => e.Name == "stable" && e.IsDirectory) && real.Any(e => e.Name == "latest" && e.IsDirectory), "live ArduPilot HTML parsing");
        }
        Console.WriteLine(count + " checks passed.");
    }
}
