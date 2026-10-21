using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FetchTree
{
    public sealed class ExplorerWindow : Form
    {
        private readonly TextBox address = new TextBox();
        private readonly NumericUpDown depth = new NumericUpDown();
        private readonly NumericUpDown parallel = new NumericUpDown();
        private readonly TreeView tree = new TreeView();
        private readonly ListView list = new ListView();
        private readonly ListView queue = new ListView();
        private readonly Label location = new Label();
        private readonly ToolStripStatusLabel status = new ToolStripStatusLabel();
        private readonly Label transfers = new Label();
        private readonly Button scan = new Button(), download = new Button();
        private readonly HttpClient client;
        private readonly Dictionary<string, ListViewItem> queueRows = new Dictionary<string, ListViewItem>();
        private readonly Stack<TreeNode> history = new Stack<TreeNode>();
        private TreeNode current;
        private bool scanning, transferring, suppressTree;
        private CancellationTokenSource scanCancel, transferCancel;
        private readonly ImageList smallIcons = new ImageList(), largeIcons = new ImageList();

        public ExplorerWindow(string initialUrl)
        {
            Text = "FetchTree"; Width = 1140; Height = 780; MinimumSize = new Size(850, 600);
            StartPosition = FormStartPosition.CenterScreen; Font = new Font("Segoe UI", 10); BackColor = Color.White;
            AutoScaleMode = AutoScaleMode.Dpi;
            client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate });
            client.Timeout = Timeout.InfiniteTimeSpan; client.DefaultRequestHeaders.UserAgent.ParseAdd("FetchTree/0.2");
            smallIcons.ImageSize = new Size(20, 20); largeIcons.ImageSize = new Size(48, 48);
            smallIcons.ColorDepth = ColorDepth.Depth32Bit; largeIcons.ColorDepth = ColorDepth.Depth32Bit;
            foreach (var size in new [] { 20, 48 })
            {
                var images = size == 20 ? smallIcons : largeIcons;
                images.Images.Add("folder", DrawIcon(size, true)); images.Images.Add("file", DrawIcon(size, false));
            }

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(16, 10, 16, 0) };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 160));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            Controls.Add(layout);
            var title = new Label { Text = "FetchTree", Font = new Font("Segoe UI Semibold", 20), Dock = DockStyle.Left, Width = 170 };
            var titleRow = new Panel { Dock = DockStyle.Fill };
            titleRow.Controls.Add(new Label { Text = "Remote files. Familiar folders.", ForeColor = Color.DimGray, AutoSize = true, Location = new Point(177, 15) });
            titleRow.Controls.Add(title); layout.Controls.Add(titleRow, 0, 0);

            var navigation = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5 };
            navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42)); navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
            navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84)); navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            var back = MakeButton("←"); back.AccessibleName = "Back"; back.Click += async (s, e) => { if (history.Count > 0 && !scanning) await Navigate(history.Pop(), false); };
            var up = MakeButton("↑"); up.AccessibleName = "Up one folder"; up.Click += async (s, e) => { if (current != null && current.Parent != null) await Navigate(current.Parent, true); };
            address.Dock = DockStyle.Fill; address.Margin = new Padding(6, 7, 6, 3); address.Text = initialUrl ?? "https://firmware.ardupilot.org/Plane/"; address.AccessibleName = "Directory URL";
            address.KeyDown += async (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await ScanRoot(); } };
            ConfigureButton(scan, "Scan"); scan.Click += async (s, e) => await ScanRoot();
            var refresh = MakeButton("Refresh"); refresh.Click += async (s, e) => { if (current != null && !scanning) { ((Entry)current.Tag).Status = "NotScanned"; await Navigate(current, false); } };
            navigation.Controls.Add(back, 0, 0); navigation.Controls.Add(up, 1, 0); navigation.Controls.Add(address, 2, 0); navigation.Controls.Add(scan, 3, 0); navigation.Controls.Add(refresh, 4, 0);
            layout.Controls.Add(navigation, 0, 1);

            var commands = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            ConfigureButton(download, "Download to…"); download.Width = 135; download.Click += async (s, e) => await DownloadSelected();
            commands.Controls.Add(download);
            var view = MakeButton("Icon view"); view.Width = 98; view.Click += (s, e) => { list.View = list.View == View.Details ? View.LargeIcon : View.Details; view.Text = list.View == View.Details ? "Icon view" : "Details"; };
            commands.Controls.Add(view);
            commands.Controls.Add(new Label { Text = "Scan depth", AutoSize = true, Margin = new Padding(16, 8, 4, 0) });
            depth.Minimum = 0; depth.Maximum = 10; depth.Value = 0; depth.Width = 48; depth.Margin = new Padding(0, 5, 0, 0); depth.AccessibleName = "Scan depth"; commands.Controls.Add(depth);
            commands.Controls.Add(new Label { Text = "Downloads", AutoSize = true, Margin = new Padding(16, 8, 4, 0) });
            parallel.Minimum = 1; parallel.Maximum = 8; parallel.Value = 3; parallel.Width = 48; parallel.Margin = new Padding(0, 5, 0, 0); parallel.AccessibleName = "Simultaneous downloads"; commands.Controls.Add(parallel);
            var cancel = MakeButton("Cancel"); cancel.Margin = new Padding(16, 0, 0, 0); cancel.Click += (s, e) => { if (scanCancel != null) scanCancel.Cancel(); if (transferCancel != null) transferCancel.Cancel(); };
            commands.Controls.Add(cancel); layout.Controls.Add(commands, 0, 2);

            var split = new SplitContainer { Size = new Size(1000, 400), Dock = DockStyle.Fill, SplitterDistance = 230, Panel1MinSize = 170, FixedPanel = FixedPanel.Panel1, BackColor = Color.FromArgb(225, 229, 235) };
            tree.Dock = DockStyle.Fill; tree.BorderStyle = BorderStyle.None; tree.BackColor = Color.FromArgb(247, 249, 252); tree.ItemHeight = 28; tree.ImageList = smallIcons; tree.HideSelection = false; tree.AccessibleName = "Remote folders";
            tree.AfterSelect += async (s, e) => { if (!suppressTree && !scanning) await Navigate(e.Node, true); };
            tree.BeforeExpand += async (s, e) => { if (e.Node.Tag != null && ((Entry)e.Node.Tag).Status != "Scanned") { e.Cancel = true; if (!scanning) await Navigate(e.Node, false); } };
            split.Panel1.Controls.Add(tree);
            var filePanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(12, 0, 0, 0) };
            location.Dock = DockStyle.Top; location.Height = 36; location.TextAlign = ContentAlignment.MiddleLeft; location.AutoEllipsis = true; location.ForeColor = Color.FromArgb(50, 65, 84); location.Text = "Enter a directory URL, then choose Scan";
            list.Dock = DockStyle.Fill; list.BorderStyle = BorderStyle.None; list.View = View.Details; list.FullRowSelect = true; list.MultiSelect = true; list.HideSelection = false;
            list.SmallImageList = smallIcons; list.LargeImageList = largeIcons; list.Columns.Add("Name", 330); list.Columns.Add("Type", 110); list.Columns.Add("Status", 160); list.AccessibleName = "Files and folders";
            list.ItemActivate += async (s, e) => { if (list.SelectedItems.Count == 1 && ((Entry)list.SelectedItems[0].Tag).IsDirectory && !scanning) { var entry = (Entry)list.SelectedItems[0].Tag; var node = current.Nodes.Cast<TreeNode>().First(n => n.Tag == entry); await Navigate(node, true); } };
            list.ItemDrag += (s, e) => StartDrag();
            list.KeyDown += async (s, e) => { if (e.Control && e.KeyCode == Keys.A) { foreach (ListViewItem item in list.Items) item.Selected = true; e.SuppressKeyPress = true; } if (e.KeyCode == Keys.Back && current != null && current.Parent != null) { e.SuppressKeyPress = true; await Navigate(current.Parent, true); } };
            list.SelectedIndexChanged += (s, e) => { if (!scanning) status.Text = list.SelectedItems.Count + " selected • Ctrl/Shift selects multiple items • Drag to Desktop or Explorer to copy"; };
            var menu = new ContextMenuStrip(); menu.Items.Add("Download selected to…", null, async (s, e) => await DownloadSelected()); menu.Items.Add("Select all", null, (s, e) => { foreach (ListViewItem item in list.Items) item.Selected = true; }); list.ContextMenuStrip = menu;
            filePanel.Controls.Add(list); filePanel.Controls.Add(location); split.Panel2.Controls.Add(filePanel); layout.Controls.Add(split, 0, 3);
            transfers.Dock = DockStyle.Fill; transfers.TextAlign = ContentAlignment.MiddleLeft; transfers.Text = "Transfers   •   Drag selected items to your desktop to download"; transfers.AutoEllipsis = true; layout.Controls.Add(transfers, 0, 4);
            queue.Dock = DockStyle.Fill; queue.View = View.Details; queue.FullRowSelect = true; queue.BorderStyle = BorderStyle.FixedSingle; queue.Columns.Add("File", 420); queue.Columns.Add("Transfer", 400); queue.Columns.Add("Downloaded", 180); queue.AccessibleName = "Download queue"; layout.Controls.Add(queue, 0, 5);
            var bar = new StatusStrip { Dock = DockStyle.Fill }; bar.Items.Add(status); status.Text = "Ready • Folders open with a double-click"; layout.Controls.Add(bar, 0, 6);
            FormClosing += (s, e) => { if (transferring || scanning) { if (scanCancel != null) scanCancel.Cancel(); if (transferCancel != null) transferCancel.Cancel(); e.Cancel = true; status.Text = "Cancelling work. Close again when it finishes."; } };
            FormClosed += (s, e) => { client.Dispose(); smallIcons.Dispose(); largeIcons.Dispose(); };
            if (initialUrl != null) Shown += async (s, e) => await ScanRoot();
        }

        private static Bitmap DrawIcon(int size, bool folder)
        {
            var image = new Bitmap(size, size);
            using (var g = Graphics.FromImage(image))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.ScaleTransform(size / 48f, size / 48f);
                if (folder)
                {
                    using (var dark = new SolidBrush(Color.FromArgb(222, 161, 39))) g.FillPolygon(dark, new [] { new Point(3, 12), new Point(20, 12), new Point(24, 17), new Point(44, 17), new Point(44, 39), new Point(3, 39) });
                    using (var yellow = new SolidBrush(Color.FromArgb(249, 200, 69))) g.FillRectangle(yellow, 3, 20, 41, 21);
                }
                else
                {
                    using (var paper = new SolidBrush(Color.FromArgb(234, 241, 251))) g.FillPolygon(paper, new [] { new Point(11, 5), new Point(30, 5), new Point(39, 14), new Point(39, 43), new Point(11, 43) });
                    using (var line = new Pen(Color.FromArgb(94, 132, 180), 2)) { g.DrawRectangle(line, 11, 14, 28, 29); g.DrawLine(line, 17, 25, 32, 25); g.DrawLine(line, 17, 32, 29, 32); }
                }
            }
            return image;
        }
        private static void ConfigureButton(Button b, string text) { b.Text = text; b.Height = 32; b.Dock = DockStyle.None; b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderColor = Color.FromArgb(217, 223, 232); b.BackColor = Color.White; b.Cursor = Cursors.Hand; }
        private static Button MakeButton(string text) { var b = new Button(); ConfigureButton(b, text); return b; }
        private void Ui(Action action) { if (!IsDisposed && IsHandleCreated) { try { BeginInvoke(action); } catch (InvalidOperationException) { } } }
        private void Error(Exception ex) { status.Text = ex.GetBaseException().Message; MessageBox.Show(this, ex.GetBaseException().Message, "FetchTree", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        private TreeNode MakeNode(Entry entry)
        {
            var node = new TreeNode(entry.Name) { Tag = entry, ImageKey = "folder", SelectedImageKey = "folder" };
            FillNode(node); return node;
        }
        private void FillNode(TreeNode node)
        {
            node.Nodes.Clear(); var entry = (Entry)node.Tag;
            foreach (var child in entry.Children.Where(x => x.IsDirectory)) node.Nodes.Add(MakeNode(child));
            if (entry.Status != "Scanned" && node.Nodes.Count == 0) node.Nodes.Add(new TreeNode("Open to load…"));
        }
        private async Task ScanRoot()
        {
            if (scanning) return;
            try
            {
                Uri root = Scanner.NormalizeRoot(address.Text); scanning = true; scan.Enabled = false; scanCancel = new CancellationTokenSource();
                status.Text = "Scanning…";
                var report = await new Scanner(client).ScanAsync(root, (int)depth.Value, 1000, 10000, scanCancel.Token, text => status.Text = text);
                suppressTree = true; tree.Nodes.Clear(); history.Clear(); current = null;
                var node = MakeNode(report.Root); node.Text = root.Host + root.AbsolutePath; tree.Nodes.Add(node); tree.SelectedNode = node; suppressTree = false;
                scanning = false; await Navigate(node, false, false);
                status.Text = report.Cancelled ? "Scan cancelled • Partial results shown" : report.Errors > 0 || report.Limited ? "Partial scan • Open a folder to retry or load more" : report.FoldersFound + " folders • " + report.FilesFound + " files • Ready";
            }
            catch (Exception ex) { Error(ex); }
            finally { scanning = false; suppressTree = false; scan.Enabled = true; if (scanCancel != null) { scanCancel.Dispose(); scanCancel = null; } }
        }
        private async Task Navigate(TreeNode node, bool remember, bool load = true)
        {
            if (scanning || node == null || node.Tag == null) return;
            try
            {
                var entry = (Entry)node.Tag;
                if (remember && current != null && current != node) history.Push(current);
                if (load && entry.Status != "Scanned")
                {
                    scanning = true; scan.Enabled = false; scanCancel = new CancellationTokenSource(); status.Text = "Opening " + entry.Name + "…";
                    var result = await new Scanner(client).ScanAsync(new Uri(entry.Url), 0, 1, 10000, scanCancel.Token, null);
                    entry.Children = result.Root.Children; entry.Status = result.Root.Status; entry.Error = result.Root.Error; FillNode(node);
                }
                current = node; suppressTree = true; tree.SelectedNode = node; suppressTree = false;
                if (entry.Status == "Scanned") node.Expand();
                location.Text = entry.Url; list.BeginUpdate(); list.Items.Clear();
                foreach (var item in entry.Children)
                {
                    var row = new ListViewItem(item.Name, item.IsDirectory ? "folder" : "file") { Tag = item };
                    row.SubItems.Add(item.IsDirectory ? "Folder" : "File"); row.SubItems.Add(item.IsDirectory ? item.Status == "Scanned" ? "Loaded" : "Open to load" : "Remote"); list.Items.Add(row);
                }
                list.EndUpdate(); status.Text = entry.Error ?? (entry.Children.Count + " items • Ctrl/Shift selects multiple • Drag to copy");
            }
            catch (Exception ex) { Error(ex); }
            finally { scanning = false; scan.Enabled = true; if (scanCancel != null) { scanCancel.Dispose(); scanCancel = null; } }
        }
        private Entry[] Selection() { return list.SelectedItems.Cast<ListViewItem>().Select(i => (Entry)i.Tag).ToArray(); }
        private void Report(string path, string state, long bytes, long? total)
        {
            Ui(() => {
                ListViewItem row;
                if (!queueRows.TryGetValue(path, out row)) { row = new ListViewItem(path); row.SubItems.Add(""); row.SubItems.Add(""); queueRows[path] = row; queue.Items.Add(row); }
                row.SubItems[1].Text = state;
                row.SubItems[2].Text = (bytes / 1024d / 1024d).ToString("0.00") + " MB" + (total.HasValue ? " / " + (total.Value / 1024d / 1024d).ToString("0.00") + " MB" : "");
            });
        }
        private void BeginTransfer() { transferring = true; download.Enabled = false; transferCancel = new CancellationTokenSource(); queue.Items.Clear(); queueRows.Clear(); transfers.Text = "Preparing selected items…"; }
        private void EndTransfer() { transferring = false; download.Enabled = true; transferCancel.Dispose(); transferCancel = null; }
        private async Task DownloadSelected()
        {
            var selection = Selection(); if (transferring || selection.Length == 0) return;
            using (var chooser = new FolderBrowserDialog { Description = "Choose a destination. FetchTree creates a new folder inside it to avoid overwriting existing files." })
            {
                if (chooser.ShowDialog(this) != DialogResult.OK) return;
                BeginTransfer(); TransferBatch batch = null;
                try
                {
                    var plan = await TransferPlan.ResolveAsync(selection, client, transferCancel.Token, text => Ui(() => transfers.Text = text));
                    batch = new TransferBatch(plan, client, (int)parallel.Value, transferCancel.Token, Report);
                    string destination = Path.Combine(chooser.SelectedPath, "FetchTree-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 4));
                    await batch.DownloadToAsync(destination); transfers.Text = "Saved to " + destination;
                }
                catch (OperationCanceledException) { transfers.Text = "Transfer cancelled"; }
                catch (Exception ex) { transfers.Text = "Transfer failed"; Error(ex); }
                finally { if (batch != null) batch.Dispose(); EndTransfer(); }
            }
        }
        private void StartDrag()
        {
            var selection = Selection(); if (transferring || selection.Length == 0) return;
            BeginTransfer(); int concurrency = (int)parallel.Value; var cancellation = transferCancel;
            ShellNative.ReleaseCapture();
            // A dedicated STA keeps the browser and Cancel button responsive while Explorer
            // synchronously requests descriptors and file streams during its drop operation.
            var worker = new Thread(() => {
                int ole = ShellNative.OleInitialize(IntPtr.Zero);
                RemoteDataObject data = null;
                try
                {
                    if (ole < 0) MarshalError(ole);
                    data = new RemoteDataObject(() => {
                        try
                        {
                            var plan = Task.Run(() => TransferPlan.ResolveAsync(selection, client, cancellation.Token, text => Ui(() => transfers.Text = text))).GetAwaiter().GetResult();
                            Ui(() => transfers.Text = "Drop on Desktop or Explorer to copy " + selection.Length + " selected items");
                            return new TransferBatch(plan, client, concurrency, cancellation.Token, Report);
                        }
                        catch (Exception ex) { Ui(() => transfers.Text = "Cannot prepare selection: " + ex.GetBaseException().Message); throw; }
                    });
                    Ui(() => transfers.Text = "Drag to Desktop or Explorer • Esc cancels");
                    int effect; int result = ShellNative.DoDragDrop(data, new DropSource(cancellation.Token), 1, out effect);
                    if (result < 0) MarshalError(result);
                    string dragError = data.LastError;
                    Ui(() => transfers.Text = dragError != null ? "Transfer failed: " + dragError : result == 0x40100 && effect == 1 ? "Drop finished • Check the queue for download errors" : "Drag cancelled");
                }
                catch (Exception ex) { Ui(() => { transfers.Text = "Drag failed"; Error(ex); }); }
                finally
                {
                    cancellation.Cancel();
                    if (data != null && data.HasBatch) { data.Batch.WaitForCompletion(); data.Batch.Dispose(); }
                    if (ole >= 0) ShellNative.OleUninitialize();
                    Ui(EndTransfer);
                }
            });
            worker.IsBackground = true; worker.SetApartmentState(ApartmentState.STA); worker.Start();
        }
        private static void MarshalError(int error) { System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(error); }
    }

    internal static class DesktopProgram
    {
        [STAThread]
        private static void Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ExplorerWindow(args.Length > 0 ? args[0] : null));
        }
    }
}
