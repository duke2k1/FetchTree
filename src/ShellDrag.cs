using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FetchTree
{
    public static class ShellTransferWait
    {
        // Shell GetData is synchronous. Keep paint/progress/cancellation messages moving
        // while the asynchronous network work completes. The window guards against
        // starting another scan, navigation, or transfer during this nested message loop.
        public static T Complete<T>(Task<T> operation)
        {
            if (Application.MessageLoop)
                while (!operation.IsCompleted) { Application.DoEvents(); Thread.Sleep(15); }
            return operation.GetAwaiter().GetResult();
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct FileDescriptor
    {
        public uint Flags;
        public Guid ClassId;
        public int Width, Height, X, Y;
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint SizeHigh, SizeLow;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }

    public static class ShellNative
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterClipboardFormat(string name);
        [DllImport("kernel32.dll")] public static extern IntPtr GlobalAlloc(uint flags, UIntPtr size);
        [DllImport("kernel32.dll")] public static extern IntPtr GlobalLock(IntPtr handle);
        [DllImport("kernel32.dll")] public static extern bool GlobalUnlock(IntPtr handle);
        [DllImport("kernel32.dll")] public static extern IntPtr GlobalFree(IntPtr handle);
        [DllImport("ole32.dll")] public static extern void ReleaseStgMedium(ref STGMEDIUM medium);
        [DllImport("ole32.dll")] public static extern int OleInitialize(IntPtr reserved);
        [DllImport("ole32.dll")] public static extern void OleUninitialize();
        [DllImport("ole32.dll")] public static extern int DoDragDrop(System.Runtime.InteropServices.ComTypes.IDataObject data, IDropSource source, int allowed, out int effect);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        public static extern void SHCreateStreamOnFileEx(string path, uint mode, uint attributes, bool create, IStream template, out IStream stream);
    }

    [ComVisible(true), Guid("00000121-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDropSource
    {
        [PreserveSig] int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool escape, uint keys);
        [PreserveSig] int GiveFeedback(uint effect);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class DropSource : IDropSource
    {
        private readonly CancellationToken token;
        public DropSource(CancellationToken token) { this.token = token; }
        public int QueryContinueDrag(bool escape, uint keys) { return escape || token.IsCancellationRequested ? 0x40101 : (keys & 1) == 0 ? 0x40100 : 0; }
        public int GiveFeedback(uint effect) { return 0x40102; }
    }

    [ComVisible(true), Guid("3D8B0590-F691-11D2-8EA9-006097DF5BD4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDataObjectAsyncCapability
    {
        void SetAsyncMode([MarshalAs(UnmanagedType.Bool)] bool enabled);
        void GetAsyncMode([MarshalAs(UnmanagedType.Bool)] out bool enabled);
        void StartOperation(IBindCtx reserved);
        void InOperation([MarshalAs(UnmanagedType.Bool)] out bool active);
        void EndOperation(int result, IBindCtx reserved, uint effect);
    }

    // Native virtual files: Explorer chooses the destination and performs collision handling.
    // Contents are requested by index, only when a drop target consumes the files.
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class RemoteDataObject : System.Runtime.InteropServices.ComTypes.IDataObject, IDataObjectAsyncCapability
    {
        public static readonly short DescriptorFormat = unchecked((short)ShellNative.RegisterClipboardFormat("FileGroupDescriptorW"));
        public static readonly short ContentsFormat = unchecked((short)ShellNative.RegisterClipboardFormat("FileContents"));
        public static readonly short EffectFormat = unchecked((short)ShellNative.RegisterClipboardFormat("Preferred DropEffect"));
        private readonly Lazy<TransferBatch> batch;
        public RemoteDataObject(Func<TransferBatch> create) { batch = new Lazy<TransferBatch>(create); }
        public bool HasBatch { get { return batch.IsValueCreated; } }
        public TransferBatch Batch { get { return batch.Value; } }
        public string LastError { get; private set; }
        private bool asyncMode;
        private volatile bool asyncStarted, asyncActive;
        private readonly TaskCompletionSource<int> finished = new TaskCompletionSource<int>();
        public bool AsyncStarted { get { return asyncStarted; } }
        public Task<int> OperationFinished { get { return finished.Task; } }
        public void SetAsyncMode(bool enabled) { asyncMode = enabled; }
        public void GetAsyncMode(out bool enabled) { enabled = asyncMode; }
        public void StartOperation(IBindCtx reserved) { asyncStarted = true; asyncActive = true; }
        public void InOperation(out bool active) { active = asyncActive; }
        public void EndOperation(int result, IBindCtx reserved, uint effect)
        {
            asyncActive = false;
            // Don't run UI continuation/cleanup inside Explorer's EndOperation callback.
            Task.Run(() => finished.TrySetResult(result));
        }

        public static FORMATETC Format(short id, TYMED medium, int index)
        {
            return new FORMATETC { cfFormat = id, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = index, tymed = medium, ptd = IntPtr.Zero };
        }

        private static STGMEDIUM Global(int bytes, Action<IntPtr> fill)
        {
            IntPtr handle = ShellNative.GlobalAlloc(0x42, (UIntPtr)bytes);
            if (handle == IntPtr.Zero) throw new OutOfMemoryException();
            IntPtr memory = ShellNative.GlobalLock(handle);
            if (memory == IntPtr.Zero) { ShellNative.GlobalFree(handle); throw new OutOfMemoryException(); }
            try { fill(memory); }
            catch { ShellNative.GlobalUnlock(handle); ShellNative.GlobalFree(handle); throw; }
            ShellNative.GlobalUnlock(handle);
            return new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = handle, pUnkForRelease = null };
        }

        public void GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            bool supported = QueryGetData(ref format) == 0;
            try { GetDataCore(ref format, out medium); }
            catch (Exception ex) { if (supported) LastError = ex.GetBaseException().Message; throw; }
        }

        private void GetDataCore(ref FORMATETC format, out STGMEDIUM medium)
        {
            medium = new STGMEDIUM();
            int check = QueryGetData(ref format);
            if (check != 0) Marshal.ThrowExceptionForHR(check);
            if (format.cfFormat == EffectFormat) { medium = Global(4, p => Marshal.WriteInt32(p, 1)); return; }
            var current = batch.Value;
            if (format.cfFormat == DescriptorFormat)
            {
                int size = Marshal.SizeOf(typeof(FileDescriptor));
                medium = Global(4 + current.Items.Count * size, p => {
                    Marshal.WriteInt32(p, current.Items.Count);
                    for (int i = 0; i < current.Items.Count; i++)
                    {
                        var item = current.Items[i];
                        var descriptor = new FileDescriptor { Flags = 0x80004004, Attributes = item.Directory ? 0x10u : 0x80u, Name = item.Path };
                        Marshal.StructureToPtr(descriptor, IntPtr.Add(p, 4 + i * size), false);
                    }
                });
                return;
            }
            if (format.lindex < 0 || format.lindex >= current.Items.Count || current.Items[format.lindex].Directory)
                Marshal.ThrowExceptionForHR(unchecked((int)0x80040068));
            string file = ShellTransferWait.Complete(current.GetFileAsync(current.Items[format.lindex]));
            IStream stream;
            ShellNative.SHCreateStreamOnFileEx(file, 0x20, 0, false, null, out stream);
            medium = new STGMEDIUM { tymed = TYMED.TYMED_ISTREAM, unionmember = Marshal.GetComInterfaceForObject(stream, typeof(IStream)), pUnkForRelease = null };
            Marshal.ReleaseComObject(stream);
        }

        public int QueryGetData(ref FORMATETC format)
        {
            if (format.dwAspect != DVASPECT.DVASPECT_CONTENT) return unchecked((int)0x8004006B);
            if (format.ptd != IntPtr.Zero) return unchecked((int)0x80040065);
            if (format.cfFormat == ContentsFormat) return (format.tymed & TYMED.TYMED_ISTREAM) == 0 ? unchecked((int)0x80040069) : format.lindex < 0 ? unchecked((int)0x80040068) : 0;
            if (format.cfFormat == DescriptorFormat || format.cfFormat == EffectFormat)
                return format.lindex != -1 ? unchecked((int)0x80040068) : (format.tymed & TYMED.TYMED_HGLOBAL) == 0 ? unchecked((int)0x80040069) : 0;
            return unchecked((int)0x80040064);
        }
        public void GetDataHere(ref FORMATETC f, ref STGMEDIUM m) { throw new COMException("Unsupported", unchecked((int)0x80040069)); }
        public int GetCanonicalFormatEtc(ref FORMATETC input, out FORMATETC output) { output = input; output.ptd = IntPtr.Zero; return 0x40130; }
        public void SetData(ref FORMATETC f, ref STGMEDIUM m, bool release) { if (release) ShellNative.ReleaseStgMedium(ref m); }
        public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
        {
            if (direction != DATADIR.DATADIR_GET) throw new NotImplementedException();
            return new FormatEnumerator(new [] { Format(DescriptorFormat, TYMED.TYMED_HGLOBAL, -1), Format(ContentsFormat, TYMED.TYMED_ISTREAM, -1), Format(EffectFormat, TYMED.TYMED_HGLOBAL, -1) });
        }
        public int DAdvise(ref FORMATETC f, ADVF flags, IAdviseSink sink, out int connection) { connection = 0; return unchecked((int)0x80040003); }
        public void DUnadvise(int connection) { }
        public int EnumDAdvise(out IEnumSTATDATA enumerator) { enumerator = null; return unchecked((int)0x80040003); }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class FormatEnumerator : IEnumFORMATETC
    {
        private readonly FORMATETC[] formats; private int position;
        public FormatEnumerator(FORMATETC[] formats) { this.formats = formats; }
        public int Next(int count, FORMATETC[] output, int[] fetched)
        {
            int n = 0; while (n < count && position < formats.Length) output[n++] = formats[position++];
            if (fetched != null) fetched[0] = n;
            return n == count ? 0 : 1;
        }
        public int Skip(int count) { int old = position; position = Math.Min(formats.Length, position + count); return position - old == count ? 0 : 1; }
        public int Reset() { position = 0; return 0; }
        public void Clone(out IEnumFORMATETC clone) { clone = new FormatEnumerator(formats) { position = position }; }
    }
}
