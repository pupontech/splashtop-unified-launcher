using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SplashtopUnified.AccountBrowser;
using Xunit;

namespace SplashtopUnified.Extractor.Tests;

/// <summary>
/// Behavioural proof for the repaint wait inside the console inventory extractor.
/// The extractor is generated JavaScript, so these tests run the real production
/// script in a real browser against synthetic, locally fulfilled fixtures and read
/// only the allowlisted message the script posts back.
///
/// The regression they pin: the scroll/pager wait used to poll a fixed 40ms timer.
/// An occluded page clamps <c>setTimeout</c>, so every poll tick became a second or
/// more and a live scrolled walk paid that per step. The wait must instead end on the
/// first observed repaint, with one bounded fallback timeout for a list that never
/// repaints, and the observer must be installed before the action runs so a
/// synchronous repaint cannot be missed.
/// </summary>
public sealed class ObserverRefreshTests
{
    private const string Origin = "https://my.splashtop.com";

    [Fact]
    public async Task ScrollWalkEndsOnMutationRatherThanClampedTimerPolls()
    {
        if (RequireBrowser() is not { } browser) { return; }

        var run = await RunExtractorAsync(browser, "clamped-virtual",
            VirtualListHtml(total: 40, rowHeight: 20, viewport: 48), clampMillis: 600);

        Assert.Empty(run.Errors);
        Assert.Equal("complete", run.Outcome);
        Assert.Equal(40, run.RowCount);
        // One clamped timer callback per wait is the bounded fallback; two per step,
        // once per poll tick, is the fixed-interval poll this replaces.
        Assert.True(run.TimerFires <= 32,
            $"the scroll walk scheduled {run.TimerFires} clamped timer callbacks; a mutation-driven wait schedules almost none");
        Assert.True(run.WalkMillis < 10_000,
            $"walkMillis was {run.WalkMillis}; a clamped-timer poll multiplies the per-step wait");
    }

    [Fact]
    public async Task PagerWalkEndsOnMutationRatherThanClampedTimerPolls()
    {
        if (RequireBrowser() is not { } browser) { return; }

        var run = await RunExtractorAsync(browser, "clamped-paged",
            PagedListHtml(total: 18, size: 2), clampMillis: 600);

        Assert.Empty(run.Errors);
        Assert.Equal("complete", run.Outcome);
        Assert.Equal(18, run.RowCount);
        Assert.True(run.TimerFires <= 24,
            $"the pager walk scheduled {run.TimerFires} clamped timer callbacks; a mutation-driven wait schedules almost none");
        Assert.True(run.WalkMillis < 8_000,
            $"walkMillis was {run.WalkMillis}; a clamped-timer poll multiplies the per-step wait");
    }

    [Fact]
    public async Task ScrollWalkStillReadsEveryRowWithUnclampedTimers()
    {
        if (RequireBrowser() is not { } browser) { return; }

        var run = await RunExtractorAsync(browser, "plain-virtual",
            VirtualListHtml(total: 40, rowHeight: 20, viewport: 48), clampMillis: 0);

        Assert.Empty(run.Errors);
        Assert.Equal("complete", run.Outcome);
        Assert.Equal(40, run.RowCount);
        Assert.True(run.WalkMillis < 5_000, $"walkMillis was {run.WalkMillis}");
    }

    [Fact]
    public async Task PagerWalkStillReadsEveryRowWithUnclampedTimers()
    {
        if (RequireBrowser() is not { } browser) { return; }

        var run = await RunExtractorAsync(browser, "plain-paged",
            PagedListHtml(total: 18, size: 2), clampMillis: 0);

        Assert.Empty(run.Errors);
        Assert.Equal("complete", run.Outcome);
        Assert.Equal(18, run.RowCount);
    }

    /// <summary>
    /// A fully rendered static list in a scrollable owner never repaints. The walk
    /// must still terminate on its bounded fallback instead of hanging, and it must
    /// stay partial rather than claim completeness it cannot prove.
    /// </summary>
    [Fact]
    public async Task ScrollWalkOfAListThatNeverRepaintsStaysBounded()
    {
        if (RequireBrowser() is not { } browser) { return; }

        var run = await RunExtractorAsync(browser, "never-repaints",
            StaticScrollableListHtml(), clampMillis: 0);

        Assert.Empty(run.Errors);
        Assert.Equal("incomplete", run.Outcome);
        Assert.Equal("scrolled", run.Mode);
        Assert.Equal(5, run.RowCount);
        Assert.False(run.WalkedToEnd);
        Assert.True(run.WalkMillis < 6_000, $"walkMillis was {run.WalkMillis}; the no-progress wait is not bounded");
    }

    private static string? RequireBrowser()
    {
        var browser = FindBrowser();
        if (browser is null)
        {
            // Missing browser infrastructure is not a passing behavioral proof.
            Assert.Fail("Observer refresh proof requires Chromium or Edge. Set SPLASHTOP_TEST_BROWSER to an installed browser executable.");
        }

        return browser;
    }

    private static string? FindBrowser()
    {
        foreach (var variable in new[] { "SPLASHTOP_TEST_BROWSER", "CHROMIUM_PATH", "CHROME_PATH", "EDGE_PATH" })
        {
            var configured = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) { return configured; }
        }

        var names = OperatingSystem.IsWindows()
            ? new[] { "msedge.exe", "chrome.exe", "chromium.exe" }
            : new[] { "chromium", "chromium-browser", "google-chrome", "google-chrome-stable", "chrome" };
        foreach (var name in names)
        {
            var found = FindOnPath(name);
            if (found is not null) { return found; }
        }

        foreach (var folder in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                 })
        {
            if (string.IsNullOrEmpty(folder)) { continue; }
            foreach (var relative in new[] { @"Microsoft\Edge\Application\msedge.exe", @"Google\Chrome\Application\chrome.exe" })
            {
                var candidate = Path.Combine(folder, relative);
                if (File.Exists(candidate)) { return candidate; }
            }
        }

        return null;
    }

    private static string? FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) { return null; }
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) { return candidate; }
            }
            catch (ArgumentException) { }
            catch (IOException) { }
        }

        return null;
    }

    private static async Task<FixtureRun> RunExtractorAsync(string browser, string fixtureName, string html, int clampMillis)
    {
        var url = $"{Origin}/{fixtureName}.html";
        var preload = """
            (function () {
              window.__posts = [];
              window.__errors = [];
              window.__timerFires = 0;
              window.addEventListener('error', function (event) { window.__errors.push(String(event.message || 'error')); });
              window.addEventListener('unhandledrejection', function (event) { window.__errors.push(String(event.reason || 'rejection')); });
              window.chrome = window.chrome || {};
              window.chrome.webview = { postMessage: function (message) { window.__posts.push(String(message)); } };
              // Occluded pages clamp timers; the walk must not depend on a fast poll.
              var realSetTimeout = window.setTimeout.bind(window);
              var clamp = __CLAMP__;
              window.setTimeout = function (callback, delay) {
                return realSetTimeout(function () { window.__timerFires++; callback(); }, Math.max(Number(delay) || 0, clamp));
              };
            })();
            """.Replace("__CLAMP__", clampMillis.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        await using var replay = await BrowserReplay.StartAsync(browser, url, Encoding.UTF8.GetBytes(html), preload);
        await replay.WaitForReadyAsync(30);
        await replay.EvaluateAsync(ConsoleInventoryExtractor.CreateExtractScript("observer-refresh-test"), awaitPromise: true, timeoutSeconds: 150);
        var state = await replay.EvaluateAsync(
            "JSON.stringify({posts: window.__posts || [], errors: window.__errors || [], timerFires: window.__timerFires || 0})",
            awaitPromise: false, timeoutSeconds: 30);
        using var document = JsonDocument.Parse(state?.GetString() ?? "{}");
        var timerFires = document.RootElement.GetProperty("timerFires").GetInt32();
        var errors = document.RootElement.GetProperty("errors").EnumerateArray().Select(item => item.GetString() ?? "error").ToArray();

        string? final = null;
        foreach (var post in document.RootElement.GetProperty("posts").EnumerateArray())
        {
            var text = post.GetString();
            if (text is null) { continue; }
            try
            {
                using var payload = JsonDocument.Parse(text);
                if (string.Equals(payload.RootElement.GetProperty("source").GetString(), "splashtop-console-inventory", StringComparison.Ordinal))
                {
                    final = text;
                }
            }
            catch (JsonException) { }
        }

        Assert.True(final is not null, $"the extractor posted no correlated inventory payload; page errors: {string.Join("; ", errors)}");
        using var parsed = JsonDocument.Parse(final!);
        var root = parsed.RootElement;
        return new FixtureRun(
            root.GetProperty("outcome").GetString(),
            root.GetProperty("rowCount").GetInt32(),
            root.GetProperty("mode").GetString(),
            root.GetProperty("walkMillis").GetInt64(),
            root.GetProperty("walkedToEnd").GetBoolean(),
            timerFires,
            errors);
    }

    private sealed record FixtureRun(string? Outcome, int RowCount, string? Mode, long WalkMillis, bool WalkedToEnd, int TimerFires, string[] Errors);

    // Synthetic fixtures. The markup is invented from the documented column labels;
    // no row text represents live account data.

    private static string VirtualListHtml(int total, int rowHeight, int viewport)
    {
        var rows = string.Join(",", Enumerable.Range(1, total).Select(i =>
            $"{{n:'Fixture Node {i:D2}',d:'fixture-node-{i}',g:'Synthetic',t:''}}"));
        return """
            <!doctype html><html><head><meta charset='utf-8'><title>Synthetic virtual list</title>
            <style>tr{height:__RH__px}</style></head><body>
            <div id='scroller' style='height:__VH__px;overflow-y:auto'>
            <div style='position:relative;height:__CANVAS__px'>
            <table id='computers' style='position:absolute;left:0;right:0;top:0'>
            <thead><tr><th>Computer Name</th><th>Device Name</th><th>Group</th><th>Notes</th><th></th></tr></thead>
            <tbody id='rows'></tbody></table></div></div>
            <p role='status'>__TOTAL__ computers</p>
            <script>
            var all=[__ROWS__];var RH=__RH__;var VH=__VH__;
            var scroller=document.getElementById('scroller');var table=document.getElementById('computers');
            function render(){var start=Math.floor(scroller.scrollTop/RH);var count=Math.ceil(VH/RH)+1;var slice=all.slice(start,start+count);
            document.getElementById('rows').innerHTML=slice.map(function(r){return '<tr style="height:'+RH+'px"><td>'+r.n+'</td><td>'+r.d+'</td><td>'+r.g+'</td><td>'+r.t+'</td><td><button aria-label="Connect">Connect</button></td></tr>';}).join('');
            table.style.transform='translateY('+(start*RH)+'px)';}
            scroller.addEventListener('scroll',render);render();
            </script></body></html>
            """
            .Replace("__ROWS__", rows, StringComparison.Ordinal)
            .Replace("__RH__", rowHeight.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__VH__", viewport.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__CANVAS__", (total * rowHeight).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__TOTAL__", total.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static string PagedListHtml(int total, int size)
    {
        var rows = string.Join(",", Enumerable.Range(1, total).Select(i =>
            $"{{n:'Fixture Host {i:D2}',d:'fixture-{i}',g:'Synthetic',t:''}}"));
        return """
            <!doctype html><html><head><meta charset='utf-8'><title>Synthetic paged list</title></head><body>
            <table id='computers'><thead><tr><th>Computer Name</th><th>Device Name</th><th>Group</th><th>Notes</th><th></th></tr></thead>
            <tbody id='rows'></tbody></table>
            <nav id='pager' aria-label='pagination'><button id='prev' aria-label='Previous page'>Previous</button>
            <span id='range'></span><button id='next' aria-label='Next page'>Next</button></nav>
            <script>
            var all=[__ROWS__];var size=__SIZE__;var page=0;
            function render(){var slice=all.slice(page*size,page*size+size);
            document.getElementById('rows').innerHTML=slice.map(function(r){return '<tr><td>'+r.n+'</td><td>'+r.d+'</td><td>'+r.g+'</td><td>'+r.t+'</td><td><button aria-label="Connect">Connect</button></td></tr>';}).join('');
            document.getElementById('range').textContent=(page*size+1)+'-'+Math.min(all.length,(page+1)*size)+' of '+all.length;
            document.getElementById('next').disabled=(page+1)*size>=all.length;
            document.getElementById('prev').disabled=page===0;}
            document.getElementById('next').onclick=function(){page++;render();};
            document.getElementById('prev').onclick=function(){page--;render();};render();
            </script></body></html>
            """
            .Replace("__ROWS__", rows, StringComparison.Ordinal)
            .Replace("__SIZE__", size.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static string StaticScrollableListHtml()
    {
        var rows = string.Concat(Enumerable.Range(1, 5).Select(i =>
            $"<tr style='height:40px'><td>Static Node {i:D2}</td><td>static-node-{i}</td><td>Synthetic</td><td></td><td><button aria-label='Connect'>Connect</button></td></tr>"));
        return """
            <!doctype html><html><head><meta charset='utf-8'><title>Synthetic static scrollable list</title></head><body>
            <div id='scroller' style='height:80px;overflow-y:auto'>
            <table id='computers'><thead><tr><th>Computer Name</th><th>Device Name</th><th>Group</th><th>Notes</th><th></th></tr></thead>
            <tbody>__ROWS__</tbody></table></div></body></html>
            """
            .Replace("__ROWS__", rows, StringComparison.Ordinal);
    }

    /// <summary>
    /// The smallest Chrome DevTools Protocol client that can run the production
    /// extractor against a locally fulfilled synthetic fixture. The fixture document
    /// is the only fulfilled request; every other request, including any real network
    /// access, is refused, and DNS is denied so nothing can leave the machine.
    /// </summary>
    private sealed class BrowserReplay : IAsyncDisposable
    {
        private readonly Process process;
        private readonly string profile;
        private readonly ClientWebSocket socket = new();
        private readonly SemaphoreSlim sendLock = new(1, 1);
        private readonly Dictionary<int, TaskCompletionSource<JsonElement>> pending = new();
        private readonly CancellationTokenSource lifetime = new();
        private Func<string, JsonElement, Task>? onEvent;
        private int nextCommandId;

        private BrowserReplay(Process process, string profile) { this.process = process; this.profile = profile; }

        public static async Task<BrowserReplay> StartAsync(string browserPath, string fixtureUrl, byte[] fixtureHtml, string preload)
        {
            var profile = Path.Combine(Path.GetTempPath(), "splashtop-observer-replay-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(profile);
            var startInfo = new ProcessStartInfo(browserPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("--headless=new");
            startInfo.ArgumentList.Add("--no-sandbox");
            startInfo.ArgumentList.Add("--disable-gpu");
            startInfo.ArgumentList.Add("--disable-extensions");
            startInfo.ArgumentList.Add("--disable-background-networking");
            startInfo.ArgumentList.Add("--disable-component-update");
            startInfo.ArgumentList.Add("--disable-sync");
            startInfo.ArgumentList.Add("--no-first-run");
            startInfo.ArgumentList.Add("--no-default-browser-check");
            startInfo.ArgumentList.Add("--no-proxy-server");
            startInfo.ArgumentList.Add("--host-resolver-rules=MAP * ~NOTFOUND");
            startInfo.ArgumentList.Add("--remote-debugging-port=0");
            startInfo.ArgumentList.Add("--remote-allow-origins=*");
            startInfo.ArgumentList.Add($"--user-data-dir={profile}");
            startInfo.ArgumentList.Add("about:blank");

            var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {browserPath}.");
            var replay = new BrowserReplay(process, profile);
            try
            {
                var port = await replay.WaitForDebuggingPortAsync();
                var target = await OpenTargetAsync(port);
                replay.socket.Options.SetRequestHeader("Origin", "http://localhost");
                await replay.socket.ConnectAsync(new Uri(target), replay.lifetime.Token);
                _ = replay.ReadLoopAsync();
                await replay.SendAsync("Page.enable", null, 20);
                await replay.SendAsync("Runtime.enable", null, 20);
                await replay.SendAsync("Fetch.enable", new Dictionary<string, object?>
                {
                    ["patterns"] = new object?[] { new Dictionary<string, object?> { ["urlPattern"] = "*", ["requestStage"] = "Request" } }
                }, 20);
                await replay.SendAsync("Page.addScriptToEvaluateOnNewDocument", new Dictionary<string, object?> { ["source"] = preload }, 20);
                replay.onEvent = (method, parameters) => replay.HandleFetchAsync(method, parameters, fixtureUrl, fixtureHtml);
                await replay.SendAsync("Page.navigate", new Dictionary<string, object?> { ["url"] = fixtureUrl }, 30);
                return replay;
            }
            catch
            {
                await replay.DisposeAsync();
                throw;
            }
        }

        public async Task WaitForReadyAsync(int timeoutSeconds)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            string? last = null;
            while (DateTime.UtcNow < deadline)
            {
                var state = await EvaluateAsync("document.readyState", awaitPromise: false, timeoutSeconds: 20);
                last = state?.GetString();
                if (string.Equals(last, "complete", StringComparison.Ordinal))
                {
                    return;
                }

                await Task.Delay(50);
            }

            throw new TimeoutException($"The synthetic fixture document never reached readyState complete (last={last}, eventErrors={string.Join("; ", eventErrors)}).");
        }

        private readonly List<string> eventErrors = new();

        public async Task<JsonElement?> EvaluateAsync(string expression, bool awaitPromise, int timeoutSeconds)
        {
            var result = await SendAsync("Runtime.evaluate", new Dictionary<string, object?>
            {
                ["expression"] = expression,
                ["returnByValue"] = true,
                ["awaitPromise"] = awaitPromise,
                ["userGesture"] = false
            }, timeoutSeconds);
            if (result.TryGetProperty("exceptionDetails", out var exception))
            {
                throw new InvalidOperationException("The page script failed: " + exception.ToString());
            }

            // Runtime.evaluate returns a RemoteObject; with returnByValue the unwrapped
            // value is what the caller wants.
            if (result.TryGetProperty("result", out var remote) &&
                remote.TryGetProperty("value", out var value) &&
                value.ValueKind is not JsonValueKind.Undefined)
            {
                return value.Clone();
            }

            return null;
        }

        private async Task<int> WaitForDebuggingPortAsync()
        {
            var marker = Path.Combine(profile, "DevToolsActivePort");
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                if (process.HasExited)
                {
                    throw new InvalidOperationException($"The browser exited early with code {process.ExitCode}.");
                }

                if (File.Exists(marker))
                {
                    var first = (await File.ReadAllLinesAsync(marker))[0];
                    return int.Parse(first, CultureInfo.InvariantCulture);
                }

                await Task.Delay(50);
            }

            throw new TimeoutException("The browser never exposed a DevTools debugging port.");
        }

        private static async Task<string> OpenTargetAsync(int port)
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var request = new HttpRequestMessage(HttpMethod.Put, $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/json/new?about:blank");
            using var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.GetProperty("webSocketDebuggerUrl").GetString()
                ?? throw new InvalidOperationException("The browser target exposed no debugging websocket.");
        }

        private async Task ReadLoopAsync()
        {
            var buffer = new byte[512 * 1024];
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    var text = new StringBuilder();
                    WebSocketReceiveResult received;
                    do
                    {
                        received = await socket.ReceiveAsync(buffer, lifetime.Token);
                        if (received.MessageType == WebSocketMessageType.Close)
                        {
                            FailPending();
                            return;
                        }

                        text.Append(Encoding.UTF8.GetString(buffer, 0, received.Count));
                    }
                    while (!received.EndOfMessage);

                    using var document = JsonDocument.Parse(text.ToString());
                    var root = document.RootElement;
                    if (root.TryGetProperty("id", out var idElement))
                    {
                        TaskCompletionSource<JsonElement>? completion;
                        lock (pending)
                        {
                            pending.Remove(idElement.GetInt32(), out completion);
                        }

                        if (completion is not null)
                        {
                            if (root.TryGetProperty("error", out var error))
                            {
                                completion.TrySetException(new InvalidOperationException("CDP error: " + error.ToString()));
                            }
                            else
                            {
                                completion.TrySetResult(root.GetProperty("result").Clone());
                            }
                        }
                    }
                    else if (root.TryGetProperty("method", out var methodElement) && onEvent is not null)
                    {
                        var method = methodElement.GetString() ?? string.Empty;
                        var parameters = root.TryGetProperty("params", out var value) ? value.Clone() : default;
                        // Fire and forget: the handler only replies, so it must never wait
                        // for a response that this loop is the only reader of.
                        _ = Task.Run(async () =>
                        {
                            try { await onEvent(method, parameters); }
                            catch (Exception error) { lock (eventErrors) { eventErrors.Add(error.Message); } }
                        });
                    }
                }
            }
            catch (Exception)
            {
                FailPending();
            }
        }

        private async Task HandleFetchAsync(string method, JsonElement parameters, string fixtureUrl, byte[] fixtureHtml)
        {
            if (!string.Equals(method, "Fetch.requestPaused", StringComparison.Ordinal) ||
                parameters.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var requestId = parameters.GetProperty("requestId").GetString() ?? string.Empty;
            var request = parameters.GetProperty("request");
            var url = request.GetProperty("url").GetString() ?? string.Empty;
            var resourceType = parameters.TryGetProperty("resourceType", out var type) ? type.GetString() : null;
            var isFixtureDocument = string.Equals(resourceType, "Document", StringComparison.Ordinal) &&
                string.Equals(url, fixtureUrl, StringComparison.Ordinal);
            if (isFixtureDocument)
            {
                await SendAsync("Fetch.fulfillRequest", new Dictionary<string, object?>
                {
                    ["requestId"] = requestId,
                    ["responseCode"] = 200,
                    ["responseHeaders"] = new object?[]
                    {
                        new Dictionary<string, object?> { ["name"] = "Content-Type", ["value"] = "text/html; charset=utf-8" },
                        new Dictionary<string, object?> { ["name"] = "Cache-Control", ["value"] = "no-store" }
                    },
                    ["body"] = Convert.ToBase64String(fixtureHtml)
                }, 30);
            }
            else
            {
                await SendAsync("Fetch.failRequest", new Dictionary<string, object?>
                {
                    ["requestId"] = requestId,
                    ["errorReason"] = "BlockedByClient"
                }, 30);
            }
        }

        private async Task<JsonElement> SendAsync(string method, object? parameters, int timeoutSeconds)
        {
            var id = Interlocked.Increment(ref nextCommandId);
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (pending)
            {
                pending[id] = completion;
            }

            var payload = JsonSerializer.Serialize(new { id, method, @params = parameters });
            var bytes = Encoding.UTF8.GetBytes(payload);
            await sendLock.WaitAsync(lifetime.Token);
            try
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, lifetime.Token);
            }
            finally
            {
                sendLock.Release();
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, timeout.Token);
            try
            {
                return await completion.Task.WaitAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                lock (pending)
                {
                    pending.Remove(id, out _);
                }

                throw new TimeoutException($"The CDP command {method} timed out after {timeoutSeconds.ToString(CultureInfo.InvariantCulture)}s.");
            }
        }

        private void FailPending()
        {
            lock (pending)
            {
                foreach (var completion in pending.Values)
                {
                    completion.TrySetException(new InvalidOperationException("The browser debugging connection closed."));
                }

                pending.Clear();
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                lifetime.Cancel();
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var close = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", close.Token);
                }
            }
            catch (Exception)
            {
                // Teardown is best effort; the browser is killed below regardless.
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(10_000);
                }
            }
            catch (Exception)
            {
                // Best effort teardown.
            }

            try
            {
                Directory.Delete(profile, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            socket.Dispose();
            lifetime.Dispose();
            sendLock.Dispose();
            process.Dispose();
        }
    }
}
