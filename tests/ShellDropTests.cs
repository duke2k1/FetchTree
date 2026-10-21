using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using FetchTree;

[ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object result);
    void GetParent(out IShellItem parent);
    void GetDisplayName(uint mode, out IntPtr name);
    void GetAttributes(uint mask, out uint attributes);
    void Compare(IShellItem other, uint hint, out int order);
}
[StructLayout(LayoutKind.Sequential)] internal struct PointL { public int X, Y; }
[ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDropTarget
{
    void DragEnter(System.Runtime.InteropServices.ComTypes.IDataObject data, uint keys, PointL point, ref uint effect);
    void DragOver(uint keys, PointL point, ref uint effect);
    void DragLeave();
    void Drop(System.Runtime.InteropServices.ComTypes.IDataObject data, uint keys, PointL point, ref uint effect);
}
internal static class ShellDropTests
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IShellItem item);

    [STAThread]
    private static void Main(string[] args)
    {
        string destination = Path.GetFullPath(args[0]);
        if (Directory.Exists(destination)) throw new IOException("Shell test requires a new destination.");
        Directory.CreateDirectory(destination);
        int ole = ShellNative.OleInitialize(IntPtr.Zero);
        try
        {
            using (var handler = new DownloadFixture())
            using (var client = new HttpClient(handler))
            {
                var selected = new [] { new Entry { Name = "Folder", Url = "https://test.example/Folder/", IsDirectory = true, Status = "DepthLimit" }, new Entry { Name = "hello.txt", Url = "https://test.example/hello.txt" }, new Entry { Name = "second.txt", Url = "https://test.example/second.txt" } };
                var plan = TransferPlan.ResolveAsync(selected, client, CancellationToken.None, null).Result;
                using (var batch = new TransferBatch(plan, client, 3, CancellationToken.None, (p, s, b, t) => Console.WriteLine(p + ": " + s)))
                {
                    var data = new RemoteDataObject(() => batch);
                    data.SetAsyncMode(true);
                    Guid shellId = typeof(IShellItem).GUID;
                    IShellItem item; SHCreateItemFromParsingName(destination, IntPtr.Zero, ref shellId, out item);
                    Guid handlerId = new Guid("3981e225-f559-11d3-8e3a-00c04f6837d5"); Guid dropId = typeof(IDropTarget).GUID;
                    object targetObject; item.BindToHandler(IntPtr.Zero, ref handlerId, ref dropId, out targetObject);
                    var target = (IDropTarget)targetObject;
                    uint effect = 1; target.DragEnter(data, 1, new PointL(), ref effect);
                    if (effect != 1) throw new Exception("Explorer refused Copy: " + effect);
                    effect = 1; target.Drop(data, 0, new PointL(), ref effect);
                    Console.WriteLine("Drop effect: " + effect);
                    if (!data.AsyncStarted) throw new Exception("Explorer did not negotiate asynchronous copying.");
                    int ticks = 0;
                    var until = DateTime.UtcNow.AddSeconds(30);
                    while (!data.OperationFinished.IsCompleted && DateTime.UtcNow < until)
                    {
                        System.Windows.Forms.Application.DoEvents();
                        ticks++; Thread.Sleep(10);
                    }
                    if (!data.OperationFinished.IsCompleted) throw new Exception("Async Shell drop timed out.");
                    if (data.OperationFinished.Result < 0) Marshal.ThrowExceptionForHR(data.OperationFinished.Result);
                    Console.WriteLine("PASS: Explorer returned from Drop before copy completion; UI message pump continued for " + ticks + " ticks.");
                    if (!File.Exists(Path.Combine(destination, "hello.txt")) || !File.Exists(Path.Combine(destination, "second.txt")) ||
                        !File.Exists(Path.Combine(destination, "Folder", "a.txt")) || !Directory.Exists(Path.Combine(destination, "Folder", "Nested")))
                        throw new Exception("Explorer did not preserve all selected files and folders.");
                    if (File.ReadAllText(Path.Combine(destination, "hello.txt")) != "contents:/hello.txt") throw new Exception("Dropped file content differs.");
                    Console.WriteLine("PASS: actual Windows Shell drop target copied mixed multi-selection, nested files and empty folders.");
                    Marshal.ReleaseComObject(targetObject); Marshal.ReleaseComObject(item);
                }
            }
        }
        finally { if (ole >= 0) ShellNative.OleUninitialize(); }
    }
}
