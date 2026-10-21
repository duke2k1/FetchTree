using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace FetchTree
{
    internal static class Program
    {
        private static void Print(Entry entry, string indent)
        {
            Console.WriteLine(indent + (entry.IsDirectory ? "[+] " : "    ") + entry.Name +
                (entry.IsDirectory ? " [" + entry.Status + "]" : ""));
            if (entry.Error != null) Console.WriteLine(indent + "    ERROR: " + entry.Error);
            foreach (var child in entry.Children) Print(child, indent + "  ");
        }

        private static int Main(string[] args)
        {
            bool interactive = args.Length == 0;
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
                string url = null, output = null;
                int depth = 1, pages = 1000, entries = 100000;
                if (interactive)
                {
                    Console.WriteLine("FetchTree - directory scanner prototype\n");
                    Console.Write("Directory URL: ");
                    url = Console.ReadLine();
                    if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("Enter a directory URL to scan.");
                    Console.Write("Scan depth [1] (0 = root listing only): ");
                    string value = Console.ReadLine();
                    if (!string.IsNullOrWhiteSpace(value)) depth = int.Parse(value);
                    output = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FetchTree-scan-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
                }
                else
                {
                    if (args[0] == "--help" || args[0] == "-h")
                    {
                        Console.WriteLine("FetchTree.exe URL [--depth 0..100] [--json PATH] [--max-pages N] [--max-entries N]\nDepth 0 reads the root; depth 1 also reads immediate subfolders. Ctrl+C cancels and retains partial results.\nExit codes: 0 success, 1 failure, 2 partial/errors/limits, 130 cancelled.\nRun without arguments for guided prompts.");
                        return 0;
                    }
                    url = args[0];
                    for (int i = 1; i < args.Length; i++)
                    {
                        string option = args[i];
                        if (++i >= args.Length) throw new ArgumentException("Missing value for " + option);
                        switch (option)
                        {
                            case "--depth": depth = int.Parse(args[i]); break;
                            case "--max-pages": pages = int.Parse(args[i]); break;
                            case "--max-entries": entries = int.Parse(args[i]); break;
                            case "--json": output = args[i]; break;
                            default: throw new ArgumentException("Unknown option: " + option);
                        }
                    }
                }
                Uri root = Scanner.NormalizeRoot(url);
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                using (var cancellation = new CancellationTokenSource())
                using (var handler = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate })
                using (var client = new HttpClient(handler))
                {
                    client.Timeout = Timeout.InfiniteTimeSpan;
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("FetchTree/0.1");
                    Console.CancelKeyPress += (sender, e) => { e.Cancel = true; cancellation.Cancel(); };
                    var result = new Scanner(client).ScanAsync(root, depth, pages, entries, cancellation.Token,
                        message => Console.Error.WriteLine(message)).GetAwaiter().GetResult();
                    Print(result.Root, "");
                    Console.WriteLine("\n{0} pages read, {1} folders, {2} files, {3} errors. {4}", result.PagesRead,
                        result.FoldersFound, result.FilesFound, result.Errors, result.Cancelled ? "Cancelled." : result.Limited ? "Scan limits reached." : "Finished.");
                    if (output != null)
                    {
                        var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 512 }.Serialize(result);
                        File.WriteAllText(output, json, new UTF8Encoding(false));
                        Console.WriteLine("Saved: " + Path.GetFullPath(output));
                    }
                    return result.Cancelled ? 130 : result.Errors > 0 || result.Limited ? 2 : 0;
                }
            }
            catch (Exception ex) { Console.Error.WriteLine("FetchTree: " + ex.Message); return 1; }
            finally { if (interactive) { Console.WriteLine("\nPress Enter to close."); Console.ReadLine(); } }
        }
    }
}
