using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FetchTree
{
    public sealed class TransferItem
    {
        public string Path;
        public string Url;
        public bool Directory;
    }

    public static class TransferPlan
    {
        public static void ValidateName(string name)
        {
            string stem = name.Split('.')[0].ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(name) || name == "." || name == ".." || name.EndsWith(".") || name.EndsWith(" ") ||
                name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || name.Any(char.IsControl) ||
                new [] { "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$" }.Contains(stem) ||
                System.Text.RegularExpressions.Regex.IsMatch(stem, @"^(COM|LPT)[1-9¹²³]$"))
                throw new IOException("This name cannot be saved on Windows: " + name);
        }

        public static List<TransferItem> Flatten(IEnumerable<Entry> roots)
        {
            var result = new List<TransferItem>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in roots) Add(entry, "", result, names);
            return result;
        }

        private static void Add(Entry entry, string parent, List<TransferItem> result, HashSet<string> names)
        {
            ValidateName(entry.Name);
            string path = parent + entry.Name;
            if (path.Length > 220) throw new IOException("Selected folder structure is too deep for Windows drag-and-drop: " + path);
            if (!names.Add(path)) throw new IOException("Two remote items have the same Windows filename: " + path);
            if (result.Count >= 10000) throw new IOException("Selection exceeds 10,000 items. Select a smaller folder.");
            if (entry.IsDirectory && entry.Status != "Scanned") throw new IOException("Folder was not fully scanned: " + entry.Url + " (" + entry.Status + "). " + entry.Error);
            result.Add(new TransferItem { Path = path, Url = entry.Url, Directory = entry.IsDirectory });
            foreach (var child in entry.Children) Add(child, path + "\\", result, names);
        }

        public static async Task<List<TransferItem>> ResolveAsync(Entry[] selection, HttpClient client, CancellationToken token, Action<string> progress)
        {
            var complete = new List<Entry>();
            foreach (var selected in selection)
            {
                token.ThrowIfCancellationRequested();
                if (!selected.IsDirectory) { complete.Add(selected); continue; }
                // A folder transfer must not silently stop at the browsing depth.
                var report = await new Scanner(client).ScanAsync(new Uri(selected.Url), 100, 2000, 10000, token, progress).ConfigureAwait(false);
                if (report.Cancelled) throw new OperationCanceledException(token);
                if (report.Errors > 0 || report.Limited) throw new IOException("Could not fully read " + selected.Name + ". Try a smaller folder or retry the scan.");
                report.Root.Name = selected.Name;
                complete.Add(report.Root);
            }
            return Flatten(complete);
        }
    }

    public sealed class TransferBatch : IDisposable
    {
        public readonly List<TransferItem> Items;
        public readonly string Cache;
        private readonly HttpClient client;
        private readonly CancellationToken token;
        private readonly Action<string, string, long, long?> report;
        private readonly SemaphoreSlim slots;
        private readonly Dictionary<string, Task<string>> files = new Dictionary<string, Task<string>>();
        private bool started;

        public TransferBatch(List<TransferItem> items, HttpClient client, int concurrency, CancellationToken token, Action<string, string, long, long?> report)
        {
            Items = items; this.client = client; this.token = token; this.report = report;
            slots = new SemaphoreSlim(concurrency);
            Cache = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FetchTree", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Cache);
        }

        public void Start()
        {
            lock (files)
            {
                if (started) return;
                started = true;
                foreach (var item in Items.Where(i => !i.Directory))
                {
                    report(item.Path, "Queued", 0, null);
                    var task = DownloadAsync(item);
                    // Observe faults even if Explorer cancels before requesting this file.
                    task.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    files.Add(item.Path, task);
                }
            }
        }

        public Task<string> GetFileAsync(TransferItem item)
        {
            Start();
            lock (files) return files[item.Path];
        }

        public async Task DownloadToAsync(string destination)
        {
            Start();
            Task<string>[] tasks;
            lock (files) tasks = files.Values.ToArray();
            await Task.WhenAll(tasks).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            // The caller supplies a new, unique destination folder; never merge into user data.
            foreach (var item in Items)
            {
                string path = System.IO.Path.Combine(destination, item.Path);
                if (item.Directory) System.IO.Directory.CreateDirectory(path);
                else
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                    File.Copy(await GetFileAsync(item).ConfigureAwait(false), path, false);
                }
            }
        }

        private async Task<string> DownloadAsync(TransferItem item)
        {
            bool acquired = false;
            string path = System.IO.Path.Combine(Cache, Guid.NewGuid().ToString("N"));
            try
            {
                await slots.WaitAsync(token).ConfigureAwait(false); acquired = true;
                token.ThrowIfCancellationRequested();
                report(item.Path, "Downloading", 0, null);
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(60));
                    using (var response = await client.GetAsync(item.Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                    {
                        if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400) throw new IOException("File redirects are not supported; open the final listing URL.");
                        response.EnsureSuccessStatusCode();
                        long? total = response.Content.Headers.ContentLength;
                        using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var output = new FileStream(path + ".part", FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                        {
                            long done = 0; byte[] buffer = new byte[65536]; int read;
                            var last = DateTime.MinValue;
                            while (true)
                            {
                                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                                read = await input.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false);
                                if (read == 0) break;
                                await output.WriteAsync(buffer, 0, read, timeout.Token).ConfigureAwait(false);
                                done += read;
                                if ((DateTime.UtcNow - last).TotalMilliseconds > 150) { report(item.Path, "Downloading", done, total); last = DateTime.UtcNow; }
                            }
                            if (total.HasValue && done != total.Value) throw new IOException("Incomplete response: received " + done + " of " + total + " bytes.");
                            report(item.Path, "Downloaded", done, total);
                        }
                    }
                }
                File.Move(path + ".part", path);
                return path;
            }
            catch (Exception ex)
            {
                report(item.Path, token.IsCancellationRequested ? "Cancelled" : "Failed: " + ex.GetBaseException().Message, 0, null);
                throw;
            }
            finally
            {
                if (File.Exists(path + ".part")) File.Delete(path + ".part");
                if (acquired) slots.Release();
            }
        }

        public void Dispose()
        {
            Task[] pending;
            lock (files) pending = files.Values.Cast<Task>().ToArray();
            Task.WhenAll(pending).ContinueWith(t => {
                var ignored = t.Exception;
                // Delete only this batch's generated cache, after all writers have stopped.
                try { if (System.IO.Directory.Exists(Cache)) System.IO.Directory.Delete(Cache, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                slots.Dispose();
            });
        }

        public void WaitForCompletion()
        {
            Task[] pending;
            lock (files) pending = files.Values.Cast<Task>().ToArray();
            try { Task.WhenAll(pending).GetAwaiter().GetResult(); } catch (Exception) { /* Each file failure is already in the queue. */ }
        }
    }
}
