using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace NTEVHLauncher;

public partial class MainWindow : Window
{
    internal static readonly string AppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NTEVHLauncher");
    internal static readonly string CacheFolder = Path.Combine(AppDataFolder, "Cache");
    internal static readonly string SettingsPath = Path.Combine(AppDataFolder, "settings.json");
    const string AssetsUrl = "https://github.com/CallMeDangDev/NTE-Viet-Hoa/raw/refs/heads/main/Web/assets.json";

    volatile bool _pageReady;
    string? _pendingBgm, _pendingVideo, _pendingUpdateDate;
    SplashWindow? _splash;

    public MainWindow()
    {
        InitializeComponent();
        Directory.CreateDirectory(CacheFolder);
        Loaded += OnLoaded;
        Closing += (_, _) =>
        {
            
            try
            {
                webView.CoreWebView2?.Profile.ClearBrowsingDataAsync();
                webView.Dispose();
            }
            catch { }
            
            try
            {
                var wv2Dir = Path.Combine(AppDataFolder, "WebView2");
                if (Directory.Exists(wv2Dir))
                    Directory.Delete(wv2Dir, true);
            }
            catch { }
            Environment.Exit(0);
        };
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _splash = new SplashWindow();
        _splash.Show();

        try
        {
            
            Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
                "--remote-debugging-port=0");

            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(AppDataFolder, "WebView2"));
            await webView.EnsureCoreWebView2Async(env);
            App.WebView2BrowserPid = webView.CoreWebView2.BrowserProcessId;

            webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
#if DEBUG
            webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
#else
            webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
#endif
            webView.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
            webView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;

            webView.CoreWebView2.AddHostObjectToScript("launcher", new LauncherBridge(this));
            webView.CoreWebView2.WebMessageReceived += OnWebMessage;

            webView.CoreWebView2.AddWebResourceRequestedFilter("https://app.local/*", CoreWebView2WebResourceContext.All);
            webView.CoreWebView2.WebResourceRequested += OnWebResourceRequested;

            webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "cache.local", CacheFolder, CoreWebView2HostResourceAccessKind.Allow);

            webView.CoreWebView2.DOMContentLoaded += OnDOMContentLoaded;
            webView.CoreWebView2.NavigationStarting += OnNavigationStarting;
            webView.CoreWebView2.Navigate("https://app.local/index.html");

#if DEBUG
            webView.CoreWebView2.OpenDevToolsWindow();
#endif
            
            _ = Task.Run(CheckAndDownloadMedia);
            _ = Task.Run(CheckLauncherVersion);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Lỗi khởi tạo WebView2: " + ex.Message);
            _splash?.FadeOutAndClose();
            _splash = null;
            Application.Current.Shutdown(1);
        }
    }

    void OnDOMContentLoaded(object? sender, CoreWebView2DOMContentLoadedEventArgs e)
    {
        _pageReady = true;
        Dispatcher.Invoke(() =>
        {
            Opacity = 1;
            Activate();
            Focus();
            _splash?.FadeOutAndClose();
            _splash = null;
        });

        RunScript(@"
            (function(){
                document.addEventListener('selectstart', e => e.preventDefault());
                document.addEventListener('dragstart', e => e.preventDefault());
                document.addEventListener('keydown', function(e){
                    if(e.key==='F12') { e.preventDefault(); return; }
                    if(e.ctrlKey && e.shiftKey && 'IJC'.includes(e.key.toUpperCase())) { e.preventDefault(); return; }
                    if(e.ctrlKey && 'USus'.includes(e.key)) { e.preventDefault(); return; }
                });
                ['log','warn','error','info','debug','table','dir','trace'].forEach(function(m){
                    console[m] = function(){};
                });
            })();
        ");

        DetectGamePath();
        FlushPendingMedia();
    }

    void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        var uri = new Uri(e.Uri);
        
        if (uri.Host != "app.local" && uri.Host != "cache.local")
            e.Cancel = true;
    }

    [DllImport("user32.dll")]
    static extern nint SendMessage(nint hWnd, int Msg, nint wParam, nint lParam);
    [DllImport("user32.dll")]
    static extern bool ReleaseCapture();
    const int WM_NCLBUTTONDOWN = 0x00A1;
    const int HT_CAPTION = 0x0002;

    void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (e.TryGetWebMessageAsString() == "drag")
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    var hwnd = new WindowInteropHelper(this).Handle;
                    ReleaseCapture();
                    SendMessage(hwnd, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
                }
                catch { }
            });
        }
    }


    static readonly Assembly Asm = Assembly.GetExecutingAssembly();
    const string ResPrefix = "NTEVHLauncher.Resources.Web.";
    static readonly byte[] XorKey = "NTEVH@2026!xK9#mQ"u8.ToArray();

    void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var uri = new Uri(e.Request.Uri);
        var path = uri.AbsolutePath.TrimStart('/');
        var resName = ResPrefix + path.Replace('/', '.');

        var encStream = Asm.GetManifestResourceStream(resName);
        if (encStream == null)
        {
            e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(
                null, 404, "Not Found", "");
            return;
        }

        var enc = new byte[encStream.Length];
        encStream.ReadExactly(enc);
        encStream.Dispose();
        for (int i = 0; i < enc.Length; i++)
            enc[i] ^= XorKey[i % XorKey.Length];

        var mime = GetMimeType(path);
        var ms = new MemoryStream(enc);
        e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(
            ms, 200, "OK",
            $"Content-Type: {mime}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Content-Security-Policy: default-src 'self' https://app.local https://cache.local; " +
            "script-src 'self' https://app.local 'unsafe-inline'; " +
            "style-src 'self' https://app.local 'unsafe-inline'; " +
            "img-src 'self' https://app.local https://cache.local data:; " +
            "media-src 'self' https://cache.local blob:; " +
            "connect-src 'self' https://app.local");
    }

    static string GetMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css"  => "text/css; charset=utf-8",
        ".js"   => "application/javascript; charset=utf-8",
        ".json" => "application/json",
        ".png"  => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".svg"  => "image/svg+xml",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".webp" => "image/webp",
        ".mp4"  => "video/mp4",
        ".mp3"  => "audio/mpeg",
        _       => "application/octet-stream"
    };


    static string JsStr(string s) => JsonSerializer.Serialize(s);

    internal void RunScript(string js)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            try { await webView.CoreWebView2.ExecuteScriptAsync(js); }
            catch { }
        });
    }


    void DetectGamePath()
    {
        string[] paths =
        [
            @"C:\Program Files\Neverness To Everness",
            @"D:\Program Files\Neverness To Everness",
            @"E:\Program Files\Neverness To Everness",
            @"C:\Neverness To Everness",
            @"D:\Neverness To Everness",
            @"E:\Neverness To Everness",
        ];
        var exe1 = @"Client\WindowsNoEditor\HT\Binaries\Win64\HTGame-Win64-Shipping.exe";
        var exe2 = @"Client\WindowsNoEditor\HT\Binaries\Win64\HTGame.exe";
        foreach (var p in paths)
        {
            if (File.Exists(Path.Combine(p, exe1)) || File.Exists(Path.Combine(p, exe2)))
            {
                RunScript($"window.onGamePathDetected({JsStr(p)})");
                return;
            }
        }
    }


    internal async Task RunInstallation(string gamePath, string vhMode, bool backup, bool linuxMode = false)
    {
        try
        {
            var baseDir = Path.Combine(gamePath, @"Client\WindowsNoEditor\HT\Binaries\Win64");
            var dllName = linuxMode ? "version.dll" : "netbios.dll";

            if (!Directory.Exists(baseDir))
                throw new Exception("Không tìm thấy thư mục game. Vui lòng kiểm tra lại đường dẫn.");

            try
            {
                var testFile = Path.Combine(baseDir, "vh_write_test.tmp");
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
            }
            catch (UnauthorizedAccessException)
            {
                
                RunScript("if(window.onAdminRequired) window.onAdminRequired(); else window.onInstallError('Thư mục game đang bị khóa bởi Windows. Vui lòng chạy Launcher bằng Quyền Admin.');");
                return;
            }
            catch (Exception ex)
            {
                throw new Exception("Không thể ghi file vào thư mục game: " + ex.Message);
            }

            var releaseUrl = "https://api.github.com/repos/CallMeDangDev/NTE-Viet-Hoa/releases/latest";

            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
            var json = await http.GetStringAsync(releaseUrl);

            using var doc = JsonDocument.Parse(json);

            var tagName = doc.RootElement.TryGetProperty("tag_name", out var tagProp)
                ? tagProp.GetString() ?? "" : "";

            var toDownload = new List<(string Name, string Url, long Size, string Hash)>();

            var versionCachePath = Path.Combine(AppDataFolder, "versions.json");
            var localCache = new Dictionary<string, string>();
            if (File.Exists(versionCachePath))
            {
                try
                {
                    var cacheJson = File.ReadAllText(versionCachePath);
                    localCache = JsonSerializer.Deserialize<Dictionary<string, string>>(cacheJson) ?? new();
                }
                catch { }
            }

            bool hasCustomFont = localCache.TryGetValue("customFont", out var cf) && cf == "true";

            foreach (var item in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = item.GetProperty("name").GetString() ?? "";
                if (name == "viet_font.ttf" && hasCustomFont) continue;
                if (name == dllName || name == "game_vi.dat" || name == "viet_font.ttf")
                {
                    var url = item.GetProperty("browser_download_url").GetString() ?? "";
                    var size = item.GetProperty("size").GetInt64();
                    
                    var digest = "";
                    if (item.TryGetProperty("digest", out var digestProp) && digestProp.ValueKind == JsonValueKind.String)
                        digest = digestProp.GetString()?.Replace("sha256:", "") ?? "";

                    toDownload.Add((name, url, size, digest));
                }
            }

            if (toDownload.Count == 0)
                throw new Exception("Không tìm thấy file cài đặt trên máy chủ.");

            bool allFilesUpToDate = true;
            foreach (var (name, _, _, hash) in toDownload)
            {
                var destPath = Path.Combine(baseDir, name);
                
                if (!File.Exists(destPath))
                {
                    allFilesUpToDate = false;
                    break;
                }
                
                if (!string.IsNullOrEmpty(hash))
                {
                    if (!localCache.TryGetValue(name, out var localHash) || localHash != hash)
                    {
                        allFilesUpToDate = false;
                        break;
                    }
                }
            }

            if (allFilesUpToDate)
            {
                if (!string.IsNullOrEmpty(tagName))
                {
                    localCache["_vhVersion"] = tagName;
                    File.WriteAllText(versionCachePath, JsonSerializer.Serialize(localCache));
                }
                RunScript($"window.onProgressUpdate(100, {JsStr("Bạn đang sử dụng phiên bản mới nhất!")}, '', '')");
                await Task.Delay(1500);
                RunScript("window.onInstallComplete()");
                return;
            }

            
            var needsUpdateSet = new HashSet<string>();
            long totalBytes = 0;
            foreach (var (name, _, size, hash) in toDownload)
            {
                var destPath = Path.Combine(baseDir, name);
                bool needsUpdate = !File.Exists(destPath) ||
                                   string.IsNullOrEmpty(hash) ||
                                   !localCache.TryGetValue(name, out var cachedHash) ||
                                   cachedHash != hash;
                if (needsUpdate)
                {
                    needsUpdateSet.Add(name);
                    totalBytes += size;
                }
            }

            long totalDownloaded = 0;
            var sw = Stopwatch.StartNew();
            long lastDownloaded = 0;

            foreach (var (name, url, size, hash) in toDownload)
            {
                var destPath = Path.Combine(baseDir, name);

                if (!needsUpdateSet.Contains(name))
                    continue;
                
                var tmpPath = destPath + ".tmp";

                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();

                await using var netStream = await resp.Content.ReadAsStreamAsync();
                await using var fileStream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);

                var buffer = new byte[65536];
                int bytesRead;

                while ((bytesRead = await netStream.ReadAsync(buffer)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                    totalDownloaded += bytesRead;

                    if (sw.ElapsedMilliseconds >= 350)
                    {
                        var pct = totalBytes > 0 ? (int)((totalDownloaded * 100) / totalBytes) : 0;
                        var speed = (totalDownloaded - lastDownloaded) / sw.Elapsed.TotalSeconds / 1_048_576.0;
                        var progressText = $"{totalDownloaded / 1_048_576.0:F1} / {totalBytes / 1_048_576.0:F1} MB";
                        
                        RunScript($"window.onProgressUpdate({pct}, " +
                                  $"{JsStr($"Đang tải: {name}")}, " +
                                  $"{JsStr($"{speed:F1} MB/s")}, {JsStr(progressText)})");

                        lastDownloaded = totalDownloaded;
                        sw.Restart();
                    }
                }
                
                fileStream.Close(); File.Move(tmpPath, destPath, true);
                if (!string.IsNullOrEmpty(hash))
                    localCache[name] = hash;
            }

            if (!string.IsNullOrEmpty(tagName))
                localCache["_vhVersion"] = tagName;
            File.WriteAllText(versionCachePath, JsonSerializer.Serialize(localCache));

            RunScript($"window.onProgressUpdate(100, {JsStr("Hoàn tất cài đặt!")}, '', '')");
            await Task.Delay(1000);
            RunScript("window.onInstallComplete()");
        }
        catch (Exception ex)
        {
            RunScript($"window.onInstallError({JsStr(ex.Message)})");
        }
    }


    internal void LaunchGame(string gamePath)
    {
        try
        {
            var exeDir = Path.Combine(gamePath, @"Client\WindowsNoEditor\HT\Binaries\Win64");
            string? full = null;
            foreach (var n in new[] { "HTGame-Win64-Shipping.exe", "HTGame.exe" })
            {
                var candidate = Path.Combine(exeDir, n);
                if (File.Exists(candidate)) { full = candidate; break; }
            }
            if (full != null)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = full,
                    Arguments = "",
                    WorkingDirectory = Path.GetDirectoryName(full),
                    UseShellExecute = true
                });
                Dispatcher.Invoke(() => WindowState = WindowState.Minimized);
            }
            else
            {
                RunScript($"window.onInstallError({JsStr("Không tìm thấy file game: HTGame-Win64-Shipping.exe")})");
            }
        }
        catch (Exception ex)
        {
            RunScript($"window.onInstallError({JsStr("Lỗi khởi chạy: " + ex.Message)})");
        }
    }


    const string LauncherReleasesApiUrl = "https://api.github.com/repos/CallMeDangDev/NTEVHLauncher/releases/latest";
    const string LauncherReleasesPageUrl = "https://github.com/CallMeDangDev/NTEVHLauncher/releases";

    internal async Task CheckLauncherVersion()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NTEVHLauncher/1.0");
            var json = await http.GetStringAsync(LauncherReleasesApiUrl);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("tag_name", out var tagProp)) return;
            var tag = tagProp.GetString()?.TrimStart('v', 'V') ?? "";
            if (string.IsNullOrEmpty(tag)) return;

            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
            var currentNorm = new Version(current.Major, current.Minor, Math.Max(current.Build, 0));
            if (!Version.TryParse(tag, out var latest)) return;

            if (latest > currentNorm)
            {
                while (!_pageReady) await Task.Delay(100);
                var downloadUrl = $"https://github.com/CallMeDangDev/NTEVHLauncher/releases/download/v{tag}/NTEVHLauncher-v{tag}.zip";
                RunScript($"window.onLauncherUpdateAvailable({JsStr('v' + tag)}, {JsStr(downloadUrl)})");
            }
        }
        catch { }
    }


    const string VHReleasesApiUrl = "https://api.github.com/repos/CallMeDangDev/NTE-Viet-Hoa/releases/latest";

    internal async Task FetchVHReleaseNotes()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NTEVHLauncher/1.0");
            var json = await http.GetStringAsync(VHReleasesApiUrl);
            using var doc = JsonDocument.Parse(json);

            var tag  = doc.RootElement.TryGetProperty("tag_name",     out var tp) ? tp.GetString() ?? "" : "";
            var date = doc.RootElement.TryGetProperty("published_at", out var dp) ? dp.GetString() ?? "" : "";
            var body = doc.RootElement.TryGetProperty("body",         out var bp) ? bp.GetString() ?? "" : "";
            var name = doc.RootElement.TryGetProperty("name",         out var np) ? np.GetString() ?? "" : "";

            while (!_pageReady) await Task.Delay(100);
            RunScript($"window.onVHReleaseNotes({JsStr(tag)}, {JsStr(date)}, {JsStr(body)}, {JsStr(name)})");
        }
        catch { }
    }


    internal async Task PerformLauncherUpdate(string version, string zipUrl)
    {
        try
        {
            var updateDir = Path.Combine(Path.GetTempPath(), "NTEVHLauncher_update");
            if (Directory.Exists(updateDir)) Directory.Delete(updateDir, true);
            Directory.CreateDirectory(updateDir);
            var zipPath = Path.Combine(updateDir, "update.zip");

            RunScript("window.onLauncherUpdateProgress(0, '\u0110ang tải xuống...')");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NTEVHLauncher/1.0");

            using var resp = await http.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? -1;

            await using (var net = await resp.Content.ReadAsStreamAsync())
            await using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
            {
                var buf = new byte[65536];
                long got = 0;
                var sw = Stopwatch.StartNew();
                int read;
                while ((read = await net.ReadAsync(buf)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, read));
                    got += read;
                    if (sw.ElapsedMilliseconds >= 300)
                    {
                        int pct = total > 0 ? (int)(got * 100 / total) : 0;
                        var sizeText = total > 0
                            ? $"{got / 1_048_576.0:F1} / {total / 1_048_576.0:F1} MB"
                            : $"{got / 1_048_576.0:F1} MB";
                        RunScript($"window.onLauncherUpdateProgress({pct}, {JsStr(sizeText)})");
                        sw.Restart();
                    }
                }
            }

            RunScript("window.onLauncherUpdateProgress(95, 'Đang giải nén...')");
            var extractDir = Path.Combine(updateDir, "extracted");
            ZipFile.ExtractToDirectory(zipPath, extractDir);

            var newExe = Directory.GetFiles(extractDir, "NTEVHLauncher.exe", SearchOption.AllDirectories)
                                   .FirstOrDefault()
                         ?? throw new Exception("Không tìm thấy NTEVHLauncher.exe trong file zip.");

            var currentExe = Process.GetCurrentProcess().MainModule?.FileName
                             ?? throw new Exception("Không xác định được đường dấn exe hiện tại.");
            var currentPid = Environment.ProcessId;

            var scriptPath = Path.Combine(updateDir, "updater.ps1");
            var newExeEscaped = newExe.Replace("'", "''");
            var currentExeEscaped = currentExe.Replace("'", "''");
            var scriptContent =
                $"$launcherPid = {currentPid}\n" +
                $"$newExe     = '{newExeEscaped}'\n" +
                $"$targetExe  = '{currentExeEscaped}'\n" +
                "# Wait for launcher process to fully exit\n" +
                "while ($null -ne (Get-Process -Id $launcherPid -ErrorAction SilentlyContinue)) {\n" +
                "    Start-Sleep -Milliseconds 300\n" +
                "}\n" +
                "Start-Sleep -Milliseconds 500\n" +
                "try {\n" +
                "    Copy-Item -Path $newExe -Destination $targetExe -Force\n" +
                "    Start-Process -FilePath $targetExe\n" +
                "} catch {\n" +
                "    Add-Type -AssemblyName PresentationFramework\n" +
                "    [System.Windows.MessageBox]::Show(\"C\u1eadp nh\u1eadt th\u1ea5t b\u1ea1i: $_\", \"NTEVHLauncher Updater\")\n" +
                "}\n" +
                "# Cleanup\n" +
                "Start-Sleep -Seconds 2\n" +
                $"Remove-Item -Recurse -Force '{updateDir.Replace("'", "''")}' -ErrorAction SilentlyContinue\n";
            File.WriteAllText(scriptPath, scriptContent, System.Text.Encoding.UTF8);

            RunScript("window.onLauncherUpdateProgress(100, 'Khởi động lại...')");
            await Task.Delay(800);

            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-ExecutionPolicy Bypass -WindowStyle Hidden -NonInteractive -File \"{scriptPath}\"",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });

            Dispatcher.Invoke(() => Application.Current.Shutdown());
        }
        catch (Exception ex)
        {
            RunScript($"window.onLauncherUpdateError({JsStr(ex.Message)})");
        }
    }

    async Task CheckAndDownloadMedia()
    {
        SignalMediaReady();

        RunScript("window.onMediaStatus('checking', '')");
        var toDownload = new List<(string Name, string Url, string Hash)>();

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NTEVHLauncher/1.0");
            var json = await http.GetStringAsync(AssetsUrl);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("update_date", out var updateDateProp))
            {
                var updateDate = updateDateProp.GetString() ?? "";
                if (!string.IsNullOrEmpty(updateDate))
                {
                    if (_pageReady)
                        RunScript($"window.onUpdateDate({JsStr(updateDate)})");
                    else
                        _pendingUpdateDate = updateDate;
                }
            }

            foreach (var item in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = item.GetProperty("name").GetString() ?? "";
                if (name is "bgm.mp3" or "bg-video.mp4")
                {
                    var url = item.GetProperty("url").GetString() ?? "";
                    var hash = item.GetProperty("sha256").GetString() ?? "";
                    var dest = Path.Combine(CacheFolder, name);
                    if (!File.Exists(dest) || !VerifySha256(dest, hash))
                        toDownload.Add((name, url, hash));
                }
            }
        }
        catch { }

        if (toDownload.Count > 0)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NTEVHLauncher/1.0");
            foreach (var (name, url, _) in toDownload)
            {
                try
                {
                    await DownloadWithProgress(http, url, Path.Combine(CacheFolder, name), name);
                }
                catch (Exception ex)
                {
                    RunScript($"window.onMediaStatus('error', " +
                              $"{JsStr("Lỗi tải " + name + ": " + ex.Message)})");
                }
            }
            SignalMediaReady(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
        }

        RunScript("window.onMediaStatus('ready', '')");
    }

    async Task DownloadWithProgress(HttpClient http, string url, string dest, string name)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? -1;
        var tmp = dest + ".tmp";

        await using (var net = await resp.Content.ReadAsStreamAsync())
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write,
                                             FileShare.None, 65536, useAsync: true))
        {
            var buf = new byte[65536];
            long got = 0, lastGot = 0;
            var sw = Stopwatch.StartNew();
            int read;
            while ((read = await net.ReadAsync(buf)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, read));
                got += read;
                if (sw.ElapsedMilliseconds >= 350)
                {
                    int pct = total > 0 ? (int)(got * 100 / total) : 0;
                    var spd = (got - lastGot) / sw.Elapsed.TotalSeconds / 1_048_576.0;
                    var size = total > 0
                        ? $"{got / 1_048_576.0:F1} / {total / 1_048_576.0:F1} MB"
                        : $"{got / 1_048_576.0:F1} MB";
                    RunScript($"window.onMediaProgress({pct}, " +
                              $"{JsStr("Đang tải " + name + "...")}, " +
                              $"{JsStr($"{spd:F1} MB/s")}, {JsStr(size)})");
                    lastGot = got;
                    sw.Restart();
                }
            }
        }

        if (File.Exists(dest)) File.Delete(dest);
        File.Move(tmp, dest);
    }

    void SignalMediaReady(string? cacheBuster = null)
    {
        var qs = cacheBuster != null ? "?v=" + cacheBuster : "";
        var bgm = File.Exists(Path.Combine(CacheFolder, "bgm.mp3")) ? $"https://cache.local/bgm.mp3{qs}" : "";
        var video = File.Exists(Path.Combine(CacheFolder, "bg-video.mp4")) ? $"https://cache.local/bg-video.mp4{qs}" : "";

        if (_pageReady)
            RunScript($"window.onMediaReady({JsStr(bgm)}, {JsStr(video)})");
        else
            (_pendingBgm, _pendingVideo) = (bgm, video);
    }

    void FlushPendingMedia()
    {        if (_pendingUpdateDate != null)
        {
            RunScript($"window.onUpdateDate({JsStr(_pendingUpdateDate)})");
            _pendingUpdateDate = null;
        }        if (_pendingBgm != null || _pendingVideo != null)
        {
            RunScript($"window.onMediaReady({JsStr(_pendingBgm ?? "")}, {JsStr(_pendingVideo ?? "")})");
            _pendingBgm = _pendingVideo = null;
        }
    }


    static bool VerifySha256(string path, string expected)
    {
        try
        {
            using var sha = SHA256.Create();
            using var fs = File.OpenRead(path);
            var hash = sha.ComputeHash(fs);
            return Convert.ToHexString(hash).Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}


[ClassInterface(ClassInterfaceType.AutoDual)]
[ComVisible(true)]
public class LauncherBridge
{
    readonly MainWindow _w;
    internal LauncherBridge(MainWindow w) => _w = w;

    public void MinimizeWindow() =>
        _w.Dispatcher.Invoke(() => _w.WindowState = WindowState.Minimized);

    public void CloseWindow() =>
        _w.Dispatcher.Invoke(() => Application.Current.Shutdown());

        public string BrowseGameFolder() =>
        _w.Dispatcher.Invoke(() =>
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Chọn thư mục cài đặt Neverness to Everness"
            };
            
            if (dlg.ShowDialog(_w) == true)
            {
                var path = dlg.FolderName;
                var exe1 = @"Client\WindowsNoEditor\HT\Binaries\Win64\HTGame-Win64-Shipping.exe";
                var exe2 = @"Client\WindowsNoEditor\HT\Binaries\Win64\HTGame.exe";
                
                string? Check(string p) =>
                    (System.IO.File.Exists(Path.Combine(p, exe1)) || System.IO.File.Exists(Path.Combine(p, exe2)))
                    ? p : null;
                
                var valid = Check(path);
                if (valid == null)
                {
                    var parent = new DirectoryInfo(path).Parent;
                    while (parent != null && valid == null)
                    {
                        valid = Check(parent.FullName);
                        parent = parent.Parent;
                    }
                }
                
                return valid ?? "?INVALID";
            }
            return "";
        });

    public void OpenUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            (uri.Scheme == "https" || uri.Scheme == "http"))
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
    }

    public void SaveSettings(string json)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MainWindow.SettingsPath)!);
            File.WriteAllText(MainWindow.SettingsPath, json);
        }
        catch { }
    }

    public string LoadSettings()
    {
        try
        {
            return File.Exists(MainWindow.SettingsPath)
                ? File.ReadAllText(MainWindow.SettingsPath) : "";
        }
        catch { return ""; }
    }

    public bool ShowConfirm(string message) =>
        _w.Dispatcher.Invoke(() =>
        {
            var dlg = new ConfirmDialog(message, _w);
            dlg.ShowDialog();
            return dlg.Confirmed;
        });

    public string GetAppVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public string GetVhVersion()
    {
        try
        {
            var path = Path.Combine(MainWindow.AppDataFolder, "versions.json");
            if (!File.Exists(path)) return "";
            var json = File.ReadAllText(path);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return dict?.TryGetValue("_vhVersion", out var v) == true ? v ?? "" : "";
        }
        catch { return ""; }
    }

    public void CheckLauncherUpdate() => Task.Run(() => _w.CheckLauncherVersion());

    public void GetVHReleaseNotes() => Task.Run(() => _w.FetchVHReleaseNotes());

    public void PerformLauncherUpdate(string version, string zipUrl) =>
        Task.Run(() => _w.PerformLauncherUpdate(version, zipUrl));

    public void StartInstallation(string gamePath, string vhMode, bool backup, bool linuxMode) =>
        Task.Run(() => _w.RunInstallation(gamePath, vhMode, backup, linuxMode));

    public void LaunchGame(string gamePath) =>
        _w.LaunchGame(gamePath);

    public void ForceQuitGame()
    {
        var names = new[] { "HTGame-Win64-Shipping", "HTGame" };
        foreach (var name in names)
            foreach (var p in Process.GetProcessesByName(name))
                try { p.Kill(true); } catch { }
    }

    public string Uninstall(string gamePath)
    {
        try
        {
            var baseDir = Path.Combine(gamePath, @"Client\WindowsNoEditor\HT\Binaries\Win64");
            var filesToRemove = new[] { "netbios.dll", "version.dll", "game_vi.dat", "viet_font.ttf", "viet_font.bak.ttf" };

            foreach (var f in filesToRemove)
            {
                var filePath = Path.Combine(baseDir, f);
                if (File.Exists(filePath))
                    File.Delete(filePath);
            }

            var versionCache = Path.Combine(MainWindow.AppDataFolder, "versions.json");
            if (File.Exists(versionCache))
                File.Delete(versionCache);

            return "ok";
        }
        catch (UnauthorizedAccessException)
        {
            return "Không có quyền xoá file. Vui lòng chạy bằng Admin.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public void RestartAsAdmin()
    {
        _w.Dispatcher.Invoke(() =>
        {
            try
            {
                var exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exe))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exe,
                        UseShellExecute = true,
                        Verb = "runas"
                    });
                    Application.Current.Shutdown();
                }
            }
            catch {  }
        });
    }


    public string BrowseFontFile() =>
        _w.Dispatcher.Invoke(() =>
        {
            var dlg = new OpenFileDialog
            {
                Title  = "Chọn file font",
                Filter = "Font files (*.ttf;*.otf)|*.ttf;*.otf|All files (*.*)|*.*"
            };
            return dlg.ShowDialog(_w) == true ? dlg.FileName : "";
        });

    public string GetCustomFontName(string gamePath)
    {
        try
        {
            var cachePath = Path.Combine(MainWindow.AppDataFolder, "versions.json");
            if (!File.Exists(cachePath)) return "";
            var json = File.ReadAllText(cachePath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (dict?.TryGetValue("customFont", out var cf) == true && cf == "true")
                return dict.TryGetValue("customFontName", out var name) ? name ?? "CustomFont" : "CustomFont";
            return "";
        }
        catch { return ""; }
    }

    public void CreateFontPak(string fontFilePath, string gamePath, string pakName) =>
        Task.Run(async () =>
        {
            try
            {
                _w.RunScript("window.onFontPakProgress('Đang đọc file font...')");

                if (!File.Exists(fontFilePath))
                    throw new FileNotFoundException("Không tìm thấy file font: " + fontFilePath);

                byte[] fontData = await File.ReadAllBytesAsync(fontFilePath);
                if (fontData.Length == 0)
                    throw new InvalidDataException("File font rỗng.");

                _w.RunScript("window.onFontPakProgress('Đang cài đặt font...')");

                var baseDir = Path.Combine(gamePath, @"Client\WindowsNoEditor\HT\Binaries\Win64");
                Directory.CreateDirectory(baseDir);

                var fontDest   = Path.Combine(baseDir, "viet_font.ttf");
                var fontBackup = Path.Combine(baseDir, "viet_font.bak.ttf");

                if (File.Exists(fontDest) && !File.Exists(fontBackup))
                    File.Copy(fontDest, fontBackup, overwrite: false);

                await File.WriteAllBytesAsync(fontDest, fontData);

                var cachePath = Path.Combine(MainWindow.AppDataFolder, "versions.json");
                var dict = new Dictionary<string, string>();
                if (File.Exists(cachePath))
                    try { dict = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(cachePath)) ?? dict; } catch { }
                dict["customFont"]     = "true";
                dict["customFontName"] = pakName;
                File.WriteAllText(cachePath, JsonSerializer.Serialize(dict));

                long fontSize = new FileInfo(fontDest).Length;
                string sizeStr = fontSize < 1_048_576
                    ? $"{fontSize / 1024.0:F1} KB"
                    : $"{fontSize / 1_048_576.0:F2} MB";

                var escapedPath = JsonSerializer.Serialize(fontDest);
                var escapedSize = JsonSerializer.Serialize(sizeStr);
                _w.RunScript($"window.onFontPakDone({escapedPath}, {escapedSize})");
            }
            catch (Exception ex)
            {
                var escaped = JsonSerializer.Serialize(ex.Message);
                _w.RunScript($"window.onFontPakError({escaped})");
            }
        });

    public void RemoveCustomFont(string gamePath) =>
        Task.Run(() =>
        {
            try
            {
                var baseDir    = Path.Combine(gamePath, @"Client\WindowsNoEditor\HT\Binaries\Win64");
                var fontDest   = Path.Combine(baseDir, "viet_font.ttf");
                var fontBackup = Path.Combine(baseDir, "viet_font.bak.ttf");

                if (File.Exists(fontBackup))
                {
                    File.Copy(fontBackup, fontDest, overwrite: true);
                    File.Delete(fontBackup);
                }
                else if (File.Exists(fontDest))
                {
                    File.Delete(fontDest);
                }

                var cachePath = Path.Combine(MainWindow.AppDataFolder, "versions.json");
                if (File.Exists(cachePath))
                {
                    try
                    {
                        var json = File.ReadAllText(cachePath);
                        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
                        dict.Remove("customFont");
                        dict.Remove("customFontName");
                        dict.Remove("viet_font.ttf");
                        File.WriteAllText(cachePath, JsonSerializer.Serialize(dict));
                    }
                    catch { }
                }

                _w.RunScript("window.onFontRevertDone()");
            }
            catch (Exception ex)
            {
                var escaped = JsonSerializer.Serialize(ex.Message);
                _w.RunScript($"window.onFontRevertError({escaped})");
            }
        });

}




