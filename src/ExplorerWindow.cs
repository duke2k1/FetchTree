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
        private readonly Button scan = new Button();
        private readonly HttpClient client;
        private readonly Dictionary<string, ListViewItem> queueRows = new Dictionary<string, ListViewItem>();
        private readonly Stack<TreeNode> history = new Stack<TreeNode>();
        private TreeNode current;
        private bool scanning, transferring, suppressTree, dragging;
        private CancellationTokenSource scanCancel, transferCancel;
        private readonly ImageList smallIcons = new ImageList(), largeIcons = new ImageList();

        public ExplorerWindow(string initialUrl)
        {
            Text = "FetchTree • Remote file explorer"; Width = 1180; Height = 820; MinimumSize = new Size(940, 660);
            StartPosition = FormStartPosition.CenterScreen; Font = new Font("Segoe UI", 10); BackColor = Color.FromArgb(242, 246, 251);
            AutoScaleMode = AutoScaleMode.Dpi;
            using (var stream = typeof(ExplorerWindow).Assembly.GetManifestResourceStream("FetchTree.ico")) { if (stream != null) Icon = new Icon(stream); }
            client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate });
            client.Timeout = Timeout.InfiniteTimeSpan; client.DefaultRequestHeaders.UserAgent.ParseAdd("FetchTree/0.3");
            smallIcons.ImageSize = new Size(20, 20); largeIcons.ImageSize = new Size(48, 48);
            smallIcons.ColorDepth = ColorDepth.Depth32Bit; largeIcons.ColorDepth = ColorDepth.Depth32Bit;
            foreach (var size in new [] { 20, 48 })
            {
                var images = size == 20 ? smallIcons : largeIcons;
                images.Images.Add("folder", DrawIcon(size, true)); images.Images.Add("file", DrawIcon(size, false));
            }
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 9, Padding = new Padding(12, 0, 12, 0) };
            foreach (int height in new [] { 30, 100, 48, 44 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            foreach (int height in new [] { 34, 140, 28, 0 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            Controls.Add(layout);

            var menu = new MenuStrip { Dock = DockStyle.Fill, BackColor = BackColor, Font = Font, Padding = new Padding(0), GripStyle = ToolStripGripStyle.Hidden };
            var fileMenu = new ToolStripMenuItem("File");
            var openMenu = new ToolStripMenuItem("Enter directory URL", null, (s, e) => { address.Focus(); address.SelectAll(); }) { ShortcutKeys = Keys.Control | Keys.L };
            fileMenu.DropDownItems.Add(openMenu);
            var refreshMenu = new ToolStripMenuItem("Refresh folder", null, async (s, e) => await RefreshCurrent()) { ShortcutKeys = Keys.F5 }; fileMenu.DropDownItems.Add(refreshMenu);
            fileMenu.DropDownItems.Add(new ToolStripSeparator()); fileMenu.DropDownItems.Add("Exit", null, (s, e) => Close());
            var editMenu = new ToolStripMenuItem("Edit"); editMenu.DropDownItems.Add("Select all", null, (s, e) => SelectAllItems()); editMenu.DropDownItems.Add("Clear selection", null, (s, e) => { foreach (ListViewItem item in list.Items) item.Selected = false; });
            var viewMenu = new ToolStripMenuItem("View"); viewMenu.DropDownItems.Add("Details", null, (s, e) => list.View = View.Details); viewMenu.DropDownItems.Add("Large icons", null, (s, e) => list.View = View.LargeIcon);
            var helpMenu = new ToolStripMenuItem("Help"); helpMenu.DropDownItems.Add("How to download", null, (s, e) => MessageBox.Show(this, "Scan a directory, select files or folders with Ctrl/Shift, then drag them to Desktop or a File Explorer folder. Keep FetchTree open until the queue finishes.\n\nCancel stops pending downloads.", "Using FetchTree"));
            helpMenu.DropDownItems.Add("About FetchTree", null, (s, e) => MessageBox.Show(this, "FetchTree 0.3\nRemote file explorer\n\nDeveloped by David Charnock", "About FetchTree", MessageBoxButtons.OK, MessageBoxIcon.Information));
            menu.Items.AddRange(new ToolStripItem[] { fileMenu, editMenu, viewMenu, helpMenu }); MainMenuStrip = menu; layout.Controls.Add(menu, 0, 0);

            var header = new BrandPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 6) };
            var logo = new PictureBox { Location = new Point(18, 11), Size = new Size(72, 72), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent };
            using (var stream = typeof(ExplorerWindow).Assembly.GetManifestResourceStream("FetchTree.logo.png")) { if (stream != null) { using (var image = Image.FromStream(stream)) logo.Image = new Bitmap(image); } }
            header.Controls.Add(logo);
            header.Controls.Add(new Label { Text = "FetchTree", ForeColor = Color.White, BackColor = Color.Transparent, Font = new Font("Segoe UI Semibold", 27), AutoSize = true, Location = new Point(105, 8) });
            header.Controls.Add(new Label { Text = "EXPLORE REMOTE. KEEP IT LOCAL.", ForeColor = Color.FromArgb(127, 211, 225), BackColor = Color.Transparent, Font = new Font("Segoe UI", 9), AutoSize = true, Location = new Point(110, 61) });
            var credit = new Label { Text = "Developed by David Charnock", ForeColor = Color.FromArgb(197, 219, 239), BackColor = Color.Transparent, AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Width = 290, Height = 40, Anchor = AnchorStyles.Right | AnchorStyles.Top };
            header.Controls.Add(credit); header.Resize += (s, e) => credit.Location = new Point(header.Width - credit.Width - 24, 27);
            layout.Controls.Add(header, 0, 1);

            var navigation = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = new Padding(0) };
            navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84)); navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            navigation.Controls.Add(new Label { Text = "LOCATION", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(82, 103, 126), Font = new Font("Segoe UI Semibold", 9) }, 0, 0);
            address.Dock = DockStyle.Fill; address.Margin = new Padding(0, 8, 10, 0); address.Text = initialUrl ?? ""; address.AccessibleName = "Directory URL";
            address.KeyDown += async (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await ScanRoot(); } };
            ConfigureButton(scan, "Connect"); scan.Width = 96; scan.BackColor = Color.FromArgb(12, 112, 147); scan.ForeColor = Color.White; scan.Margin = new Padding(0, 4, 0, 0); scan.Click += async (s, e) => await ScanRoot();
            navigation.Controls.Add(address, 1, 0); navigation.Controls.Add(scan, 2, 0); layout.Controls.Add(navigation, 0, 2);

            var commands = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
            var refresh = MakeButton("Refresh"); refresh.Width = 90; refresh.Click += async (s, e) => await RefreshCurrent(); commands.Controls.Add(refresh);
            var view = MakeButton("Switch view"); view.Width = 110; view.Click += (s, e) => list.View = list.View == View.Details ? View.LargeIcon : View.Details; commands.Controls.Add(view);
            commands.Controls.Add(new Label { Text = "Scan depth", AutoSize = true, Margin = new Padding(20, 8, 6, 0), ForeColor = Color.FromArgb(70, 90, 112) });
            depth.Minimum = 0; depth.Maximum = 10; depth.Value = 0; depth.Width = 52; depth.Margin = new Padding(0, 5, 0, 0); depth.AccessibleName = "Scan depth"; commands.Controls.Add(depth);
            commands.Controls.Add(new Label { Text = "Parallel downloads", AutoSize = true, Margin = new Padding(20, 8, 6, 0), ForeColor = Color.FromArgb(70, 90, 112) });
            parallel.Minimum = 1; parallel.Maximum = 8; parallel.Value = 3; parallel.Width = 52; parallel.Margin = new Padding(0, 5, 0, 0); parallel.AccessibleName = "Simultaneous downloads"; commands.Controls.Add(parallel);
            var cancel = MakeButton("Cancel transfers"); cancel.Width = 140; cancel.Margin = new Padding(20, 0, 0, 0); cancel.Click += (s, e) => { if (scanCancel != null) scanCancel.Cancel(); if (transferCancel != null) transferCancel.Cancel(); }; commands.Controls.Add(cancel); layout.Controls.Add(commands, 0, 3);

            var split = new SplitContainer { Size = new Size(1000, 400), Dock = DockStyle.Fill, SplitterDistance = 220, Panel1MinSize = 170, FixedPanel = FixedPanel.Panel1, BackColor = Color.FromArgb(218, 228, 238), Margin = new Padding(0) };
            tree.Dock = DockStyle.Fill; tree.BorderStyle = BorderStyle.None; tree.BackColor = Color.FromArgb(233, 240, 248); tree.ForeColor = Color.FromArgb(40, 64, 86); tree.ItemHeight = 30; tree.ImageList = smallIcons; tree.HideSelection = false; tree.AccessibleName = "Remote folders";
            tree.AfterSelect += async (s, e) => { if (!suppressTree && !scanning) await Navigate(e.Node, true); };
            tree.BeforeExpand += async (s, e) => { if (e.Node.Tag != null && ((Entry)e.Node.Tag).Status != "Scanned") { e.Cancel = true; if (!scanning) await Navigate(e.Node, false); } };
            split.Panel1.Controls.Add(tree);
            var filePanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(10, 0, 0, 0) };
            var breadcrumb = new TableLayoutPanel { Dock = DockStyle.Top, Height = 42, ColumnCount = 3, BackColor = Color.White };
            breadcrumb.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46)); breadcrumb.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46)); breadcrumb.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var back = MakeButton("←"); back.Width = 40; back.AccessibleName = "Back"; back.Click += async (s, e) => { if (history.Count > 0 && !scanning && !dragging) await Navigate(history.Pop(), false); };
            var up = MakeButton("↑"); up.Width = 40; up.AccessibleName = "Up one folder"; up.Click += async (s, e) => { if (current != null && current.Parent != null) await Navigate(current.Parent, true); };
            location.Dock = DockStyle.Fill; location.TextAlign = ContentAlignment.MiddleLeft; location.AutoEllipsis = true; location.ForeColor = Color.FromArgb(57, 87, 114); location.Text = "Your remote folders will appear here";
            breadcrumb.Controls.Add(back, 0, 0); breadcrumb.Controls.Add(up, 1, 0); breadcrumb.Controls.Add(location, 2, 0);
            list.Dock = DockStyle.Fill; list.BorderStyle = BorderStyle.None; list.View = View.Details; list.FullRowSelect = true; list.MultiSelect = true; list.HideSelection = false; list.ForeColor = Color.FromArgb(35, 54, 76);
            list.SmallImageList = smallIcons; list.LargeImageList = largeIcons; list.Columns.Add("Name", 330); list.Columns.Add("Type", 110); list.Columns.Add("Status", 160); list.AccessibleName = "Files and folders";
            list.ItemActivate += async (s, e) => { if (list.SelectedItems.Count == 1 && ((Entry)list.SelectedItems[0].Tag).IsDirectory && !scanning) { var entry = (Entry)list.SelectedItems[0].Tag; var node = current.Nodes.Cast<TreeNode>().First(n => n.Tag == entry); await Navigate(node, true); } };
            list.ItemDrag += (s, e) => StartDrag();
            list.KeyDown += async (s, e) => { if (e.Control && e.KeyCode == Keys.A) { SelectAllItems(); e.SuppressKeyPress = true; } if (e.KeyCode == Keys.Back && current != null && current.Parent != null) { e.SuppressKeyPress = true; await Navigate(current.Parent, true); } };
            list.SelectedIndexChanged += (s, e) => { if (!scanning) status.Text = list.SelectedItems.Count + " selected • Ctrl/Shift selects multiple • Drag to Desktop or Explorer"; };
            var context = new ContextMenuStrip(); context.Items.Add("Select all", null, (s, e) => SelectAllItems()); context.Items.Add("Clear selection", null, (s, e) => { foreach (ListViewItem item in list.Items) item.Selected = false; }); list.ContextMenuStrip = context;
            filePanel.Controls.Add(list); filePanel.Controls.Add(breadcrumb); split.Panel2.Controls.Add(filePanel); layout.Controls.Add(split, 0, 4);
            transfers.Dock = DockStyle.Fill; transfers.TextAlign = ContentAlignment.MiddleLeft; transfers.Text = "TRANSFERS   •   Select, drag, and drop to download"; transfers.ForeColor = Color.FromArgb(40, 90, 120); transfers.AutoEllipsis = true; layout.Controls.Add(transfers, 0, 5);
            queue.Dock = DockStyle.Fill; queue.View = View.Details; queue.FullRowSelect = true; queue.BorderStyle = BorderStyle.None; queue.Columns.Add("File", 400); queue.Columns.Add("Transfer", 400); queue.Columns.Add("Downloaded", 180); queue.AccessibleName = "Download queue"; layout.Controls.Add(queue, 0, 6);
            var bar = new StatusStrip { Dock = DockStyle.Fill, BackColor = BackColor }; bar.Items.Add(status); status.Text = "Ready • Enter a directory URL to begin"; layout.Controls.Add(bar, 0, 7);
            FormClosing += (s, e) => { if (transferring || scanning) { if (scanCancel != null) scanCancel.Cancel(); if (transferCancel != null) transferCancel.Cancel(); e.Cancel = true; status.Text = "Cancelling work. Close again when it finishes."; } };
            FormClosed += (s, e) => { client.Dispose(); smallIcons.Dispose(); largeIcons.Dispose(); if (logo.Image != null) logo.Image.Dispose(); };
            if (initialUrl != null) Shown += async (s, e) => await ScanRoot();
        }

        private void SelectAllItems() { foreach (ListViewItem item in list.Items) item.Selected = true; }
        private async Task RefreshCurrent()
        {
            if (current != null && !scanning && !dragging) { ((Entry)current.Tag).Status = "NotScanned"; await Navigate(current, false); }
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
            if (scanning || dragging) return;
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
            if (scanning || dragging || node == null || node.Tag == null) return;
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
        private void BeginTransfer() { transferring = true; transferCancel = new CancellationTokenSource(); queue.Items.Clear(); queueRows.Clear(); transfers.Text = "Preparing selected items…"; }
        private void EndTransfer() { transferring = false; transferCancel.Dispose(); transferCancel = null; }
        private async void StartDrag()
        {
            var selection = Selection(); if (transferring || scanning || selection.Length == 0) return;
            BeginTransfer(); int concurrency = (int)parallel.Value; var cancellation = transferCancel;
            RemoteDataObject data = null;
            try
            {
                data = new RemoteDataObject(() => {
                    var plan = ShellTransferWait.Complete(Task.Run(() => TransferPlan.ResolveAsync(selection, client, cancellation.Token, text => Ui(() => transfers.Text = text))));
                    return new TransferBatch(plan, client, concurrency, cancellation.Token, Report);
                });
                data.SetAsyncMode(true);
                transfers.Text = "Drag to Desktop or Explorer • Esc cancels";
                dragging = true;
                Application.OleRequired();
                int effect;
                int result = ShellNative.DoDragDrop(data, new DropSource(cancellation.Token), 1, out effect);
                dragging = false;
                if (result < 0) MarshalError(result);
                if (data.AsyncStarted)
                {
                    transfers.Text = "Copying in background • You can continue browsing";
                    int copyResult = await data.OperationFinished;
                    if (copyResult < 0) MarshalError(copyResult);
                }
                transfers.Text = data.LastError != null ? "Transfer failed: " + data.LastError : result == 0x40100 && effect == 1 ? "Copy complete" : "Drag cancelled";
            }
            catch (OperationCanceledException) { transfers.Text = "Transfer cancelled"; }
            catch (Exception ex) { transfers.Text = "Transfer failed: " + ex.GetBaseException().Message; }
            finally
            {
                dragging = false;
                cancellation.Cancel();
            }
            if (data != null && data.HasBatch)
            {
                await Task.Run(() => data.Batch.WaitForCompletion());
                data.Batch.Dispose();
            }
            EndTransfer();
            GC.KeepAlive(data);
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
