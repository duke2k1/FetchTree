using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using FetchTree;

internal sealed class DownloadFixture : HttpMessageHandler
{
    public int Files, Active, Peak;
    public bool Fail;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        if (request.RequestUri.AbsolutePath.EndsWith("/"))
        {
            response.Content = new StringContent(request.RequestUri.AbsolutePath == "/Folder/" ? "<a href='a.txt'>a</a><a href='Nested/'>nested</a>" : "", Encoding.UTF8, "text/html");
            return response;
        }
        Interlocked.Increment(ref Files); int active = Interlocked.Increment(ref Active); Peak = Math.Max(Peak, active);
        try { await Task.Delay(100, token); }
        finally { Interlocked.Decrement(ref Active); }
        if (Fail) response.StatusCode = HttpStatusCode.Forbidden;
        response.Content = new ByteArrayContent(Encoding.UTF8.GetBytes("contents:" + request.RequestUri.AbsolutePath));
        return response;
    }
}

internal static class DesktopTests
{
    private static int checks;
    private static void Check(bool good, string name) { if (!good) throw new Exception("FAIL: " + name); checks++; Console.WriteLine("PASS: " + name); }
    private static Entry FileEntry(string name) { return new Entry { Name = name, Url = "https://test.example/" + name, Status = "Discovered" }; }
    [STAThread]
    private static void Main(string[] args)
    {
        foreach (var name in new [] { "..", "CON", "nul.txt", "a:b", "x/y", "end.", "end ", "COM1.bin" })
        {
            bool rejected = false; try { TransferPlan.ValidateName(name); } catch (IOException) { rejected = true; }
            Check(rejected, "unsafe filename rejected: " + name);
        }
        bool conflict = false;
        try { TransferPlan.Flatten(new [] { FileEntry("A.txt"), FileEntry("a.txt") }); } catch (IOException) { conflict = true; }
        Check(conflict, "Windows case collision rejected");
        bool partial = false;
        try { TransferPlan.Flatten(new [] { new Entry { Name = "Folder", IsDirectory = true, Status = "DepthLimit" } }); } catch (IOException) { partial = true; }
        Check(partial, "incomplete folder is not silently downloaded");

        using (var handler = new DownloadFixture())
        using (var client = new HttpClient(handler))
        {
            var selection = new [] { new Entry { Name = "Folder", IsDirectory = true, Url = "https://test.example/Folder/", Status = "DepthLimit" }, FileEntry("hello.txt"), FileEntry("second.txt") };
            var plan = TransferPlan.ResolveAsync(selection, client, CancellationToken.None, null).Result;
            Check(plan.Count == 5 && plan.Any(i => i.Path == "Folder\\Nested" && i.Directory), "mixed selection recursively includes empty nested folders");
            Check(handler.Files == 0, "planning does not download file contents");
            using (var batch = new TransferBatch(plan, client, 2, CancellationToken.None, (p, s, b, t) => { }))
            {
                var data = new RemoteDataObject(() => batch);
                Check(Marshal.SizeOf(typeof(FileDescriptor)) == 592, "Windows FILEDESCRIPTORW binary layout");
                var descriptor = RemoteDataObject.Format(RemoteDataObject.DescriptorFormat, TYMED.TYMED_HGLOBAL, -1);
                STGMEDIUM medium; data.GetData(ref descriptor, out medium);
                IntPtr memory = ShellNative.GlobalLock(medium.unionmember);
                Check(Marshal.ReadInt32(memory) == 5, "virtual drag exports all selected items and descendants");
                var folder = (FileDescriptor)Marshal.PtrToStructure(IntPtr.Add(memory, 4), typeof(FileDescriptor));
                Check(folder.Name == "Folder" && folder.Attributes == 16, "folder descriptor preserves directory attribute");
                ShellNative.GlobalUnlock(medium.unionmember); ShellNative.ReleaseStgMedium(ref medium);
                Check(handler.Files == 0, "requesting drag descriptors does not download files");
                int index = plan.FindIndex(i => i.Path == "hello.txt");
                var content = RemoteDataObject.Format(RemoteDataObject.ContentsFormat, TYMED.TYMED_ISTREAM, index);
                data.GetData(ref content, out medium);
                var stream = (IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
                byte[] bytes = new byte[256]; IntPtr readCount = Marshal.AllocCoTaskMem(4);
                stream.Read(bytes, bytes.Length, readCount);
                string text = Encoding.UTF8.GetString(bytes, 0, Marshal.ReadInt32(readCount));
                Check(text == "contents:/hello.txt", "indexed virtual file stream returns actual downloaded bytes");
                Marshal.FreeCoTaskMem(readCount); Marshal.ReleaseComObject(stream); ShellNative.ReleaseStgMedium(ref medium);
                Task.WhenAll(plan.Where(i => !i.Directory).Select(batch.GetFileAsync)).Wait();
                Check(handler.Peak == 2 && handler.Files == 3, "bounded parallel downloads and no duplicate transfers");
                string destination = Path.Combine(batch.Cache, "saved");
                batch.DownloadToAsync(destination).Wait();
                Check(File.ReadAllText(Path.Combine(destination, "Folder", "a.txt")) == "contents:/Folder/a.txt", "folder contents preserve hierarchy");
                Check(Directory.Exists(Path.Combine(destination, "Folder", "Nested")), "empty folder preserved");
                bool overwrite = false;
                try { batch.DownloadToAsync(destination).GetAwaiter().GetResult(); } catch (IOException) { overwrite = true; }
                Check(overwrite, "existing destination files are never overwritten");
                content.lindex = 99; bool badIndex = false;
                try { data.GetData(ref content, out medium); } catch (COMException) { badIndex = true; }
                Check(badIndex, "invalid shell stream index rejected");
            }
            handler.Fail = true;
            using (var batch = new TransferBatch(TransferPlan.Flatten(new [] { FileEntry("bad.bin") }), client, 1, CancellationToken.None, (p, s, b, t) => { }))
            {
                bool failed = false; try { batch.GetFileAsync(batch.Items[0]).GetAwaiter().GetResult(); } catch (HttpRequestException) { failed = true; }
                Check(failed && !Directory.GetFiles(batch.Cache).Any(), "HTTP failure leaves no completed or partial file");
            }
            using (var cancel = new CancellationTokenSource())
            using (var batch = new TransferBatch(TransferPlan.Flatten(new [] { FileEntry("cancel.bin") }), client, 1, cancel.Token, (p, s, b, t) => { }))
            {
                cancel.Cancel(); bool cancelled = false;
                try { batch.GetFileAsync(batch.Items[0]).GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled, "cancelled transfer does not produce file contents");
            }
        }
        if (args.Length == 2)
        {
            Application.EnableVisualStyles();
            using (var window = new ExplorerWindow(args[0]))
            {
                window.Show();
                var until = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(50); }
                using (var bitmap = new Bitmap(window.Width, window.Height)) { window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, window.Size)); bitmap.Save(args[1]); }
                window.Close();
            }
            Check(File.Exists(args[1]), "desktop form renders");
        }
        Console.WriteLine(checks + " desktop checks passed.");
    }
}
