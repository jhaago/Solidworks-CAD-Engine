using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SolidWorksCadEngine.Adapter
{
    /// <summary>Generic, local MCP stdio session. One instance owns execution for one job; never retries a request.</summary>
    public sealed class McpBackendSession : IDisposable
    {
        private const string Protocol = "2025-06-18";
        private readonly IEngineWorker _worker;
        private readonly FileCadExecutionBoundary _boundary;
        private readonly TimeSpan _timeout;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly object _sync = new object();
        private readonly object _disposeSync = new object();
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private bool _resourcesReleased;
        private readonly ConcurrentQueue<string> _stderr = new ConcurrentQueue<string>();
        private readonly Dictionary<string, JObject> _tools = new Dictionary<string, JObject>(StringComparer.Ordinal);
        private TaskCompletionSource<JObject> _pending;
        private long _pendingId;
        private long _sequence;
        private Exception _terminal;
        private bool _disposed;
        public JObject ServerInfo { get; private set; }
        public IReadOnlyDictionary<string, JObject> Tools => _tools;
        public string[] StderrTail => _stderr.ToArray();

        private McpBackendSession(IEngineWorker worker, FileCadExecutionBoundary boundary, TimeSpan timeout)
        { _worker = worker; _boundary = boundary; _timeout = timeout; }

        /// <summary>Takes ownership of boundary, including on startup failure. EngineOptions.Enabled defaults to false.</summary>
        public static async Task<McpBackendSession> StartAsync(EngineOptions options, FileCadExecutionBoundary boundary,
            CancellationToken cancellation = default(CancellationToken))
            => await StartAsync(options, boundary, new LocalEngineWorkerFactory(), cancellation).ConfigureAwait(false);

        public static async Task<McpBackendSession> StartAsync(EngineOptions options, FileCadExecutionBoundary boundary,
            IEngineWorkerFactory workerFactory, CancellationToken cancellation = default(CancellationToken))
        {
            McpBackendSession session = null;
            IEngineWorker worker = null;
            try
            {
                if (options == null || !options.Enabled) throw new InvalidOperationException("The upstream backend is opt-in and disabled.");
                if (string.IsNullOrWhiteSpace(options.FileName)) throw new ArgumentException("Configure a trusted local engine executable.");
                if (options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout.TotalMilliseconds > int.MaxValue)
                    throw new ArgumentOutOfRangeException(nameof(options.RequestTimeout));
                cancellation.ThrowIfCancellationRequested();
                var start = new ProcessStartInfo(options.FileName, options.Arguments ?? "") {
                    UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
                    RedirectStandardError = true, CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                };
                if (!string.IsNullOrWhiteSpace(options.WorkingDirectory)) start.WorkingDirectory = options.WorkingDirectory;
                // Never inherit an accidentally enabled macro or a catalog restricted by another client.
                start.EnvironmentVariables["SWMCP_VBA"] = "off";
                start.EnvironmentVariables["SWMCP_TOOLSETS"] = "session,documents,inspect,sketch,features,reference,bodies,sheetmetal,weldments,assembly,drawing,export,properties,simulation";
                worker = workerFactory.Start(start);
                session = new McpBackendSession(worker, boundary, options.RequestTimeout);
                _ = session.ReadOutputAsync();
                _ = session.ReadStderrAsync();
                await session.InitializeAsync(cancellation).ConfigureAwait(false);
                return session;
            }
            catch
            {
                if (session != null) session.Dispose();
                else { if (worker != null) worker.Dispose(); boundary?.Dispose(); }
                throw;
            }
        }

        private async Task InitializeAsync(CancellationToken cancellation)
        {
            var response = await RequestAsync("initialize", new JObject {
                ["protocolVersion"] = Protocol, ["capabilities"] = new JObject(),
                ["clientInfo"] = new JObject { ["name"] = "cad-engine-adapter", ["version"] = "0.1.0" }
            }, false, cancellation).ConfigureAwait(false);
            ServerInfo = Result(response);
            if ((string)ServerInfo["protocolVersion"] != Protocol ||
                (string)ServerInfo["serverInfo"]?["name"] != "solidworks-mcp" || ServerInfo["capabilities"]?["tools"] == null)
                throw new InvalidDataException("The local process did not negotiate the expected SOLIDWORKS MCP contract.");
            await SendAsync(new JObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }).ConfigureAwait(false);
            string cursor = null;
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            do
            {
                var args = new JObject(); if (cursor != null) args["cursor"] = cursor;
                var page = Result(await RequestAsync("tools/list", args, false, cancellation).ConfigureAwait(false));
                if (!(page["tools"] is JArray definitions)) throw new InvalidDataException("Missing MCP tool catalog.");
                foreach (var item in definitions)
                {
                    var name = (string)item["name"];
                    if (string.IsNullOrEmpty(name) || !(item["inputSchema"] is JObject) || _tools.ContainsKey(name))
                        throw new InvalidDataException("Invalid or duplicate MCP tool definition.");
                    _tools.Add(name, (JObject)item.DeepClone());
                }
                cursor = (string)page["nextCursor"];
                if (cursor != null && (!cursors.Add(cursor) || cursors.Count > 1000))
                    throw new InvalidDataException("MCP discovery pagination did not terminate.");
            } while (cursor != null);
        }

        public async Task<JObject> CallToolAsync(string name, JObject arguments,
            CancellationToken cancellation = default(CancellationToken))
        {
            if (name == "vba_run") throw new InvalidOperationException("Arbitrary scripting is disabled for this backend.");
            if (name == null || !_tools.ContainsKey(name)) throw new ArgumentException("Tool was not discovered: " + name, nameof(name));
            var response = await RequestAsync("tools/call", new JObject { ["name"] = name,
                ["arguments"] = arguments ?? new JObject() }, true, cancellation).ConfigureAwait(false);
            return Result(response);
        }

        private static JObject Result(JObject response)
        {
            if (response["error"] is JObject error) throw new McpRpcException(error);
            return response["result"] as JObject ?? throw new InvalidDataException("MCP response has no object result.");
        }

        private async Task<JObject> RequestAsync(string method, JObject parameters, bool nativeCall, CancellationToken cancellation)
        {
            // A queued cancellation has no native effect and must not poison the active call.
            await _gate.WaitAsync(cancellation).ConfigureAwait(false);
            bool intent = false;
            try
            {
                if (_boundary.IsQuarantined) throw new UncertainOutcomeException("Native outcome is uncertain. Reconciliation is required; replay is forbidden.");
                if (_disposed) throw new ObjectDisposedException(nameof(McpBackendSession));
                cancellation.ThrowIfCancellationRequested();
                TaskCompletionSource<JObject> completion;
                long id = Interlocked.Increment(ref _sequence);
                lock (_sync)
                {
                    if (_terminal != null) throw new IOException("MCP worker is unavailable.", _terminal);
                    completion = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _pendingId = id; _pending = completion;
                }
                if (nativeCall)
                {
                    // If recording fails, no bytes have been sent. The partial marker still fails closed.
                    _boundary.BeforeCall(id.ToString(), (string)parameters["name"], (JObject)parameters["arguments"]);
                    intent = true;
                }
                var request = new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters };
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _shutdown.Token))
                {
                    var expiry = Task.Delay(_timeout, deadline.Token);
                    var exchange = ExchangeAsync(request, completion.Task);
                    if (await Task.WhenAny(exchange, expiry).ConfigureAwait(false) != exchange)
                    {
                        if (nativeCall)
                        {
                            // Advisory only: the external SOLIDWORKS process may still complete COM work.
                            _ = SendCancellationAsync(id);
                        }
                        _ = exchange.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                        if (cancellation.IsCancellationRequested) throw new OperationCanceledException(cancellation);
                        if (_shutdown.IsCancellationRequested) throw new ObjectDisposedException(nameof(McpBackendSession));
                        throw new TimeoutException("MCP " + method + " exceeded " + _timeout.TotalSeconds + " seconds.");
                    }
                    var response = await exchange.ConfigureAwait(false);
                    deadline.Cancel();
                    if (nativeCall)
                    {
                        if (response["error"] == null) ValidateToolResult(response);
                        bool uncertain = response["error"] != null || IsUncertainResult((JObject)response["result"]);
                        if (uncertain) _boundary.Quarantine("Engine returned an RPC failure or uncertain tool outcome.", response);
                        else _boundary.CompleteCall(response);
                    }
                    return response;
                }
            }
            catch (Exception ex) when (intent && !(ex is UncertainOutcomeException))
            {
                _boundary.Quarantine(ex.GetType().Name + ": " + ex.Message);
                StopWorker();
                throw new UncertainOutcomeException("The tool may have completed in SOLIDWORKS. Do not replay or switch backends; reconcile the native state.", ex);
            }
            finally
            {
                lock (_sync) { _pending = null; }
                _gate.Release();
            }
        }

        private static void ValidateToolResult(JObject response)
        {
            var result = response["result"] as JObject;
            if (result == null || !(result["content"] is JArray blocks) ||
                (result["isError"] != null && result["isError"].Type != JTokenType.Boolean) ||
                (result["structuredContent"] != null && !(result["structuredContent"] is JObject)))
                throw new InvalidDataException("Malformed MCP tools/call result; native outcome cannot be established.");
            foreach (var block in blocks)
            {
                if (!(block is JObject) || block["type"]?.Type != JTokenType.String)
                    throw new InvalidDataException("Malformed MCP content block.");
                string kind = (string)block["type"];
                bool valid;
                switch (kind)
                {
                    case "text": valid = block["text"]?.Type == JTokenType.String; break;
                    case "image": case "audio":
                        valid = block["data"]?.Type == JTokenType.String && block["mimeType"]?.Type == JTokenType.String; break;
                    case "resource": valid = block["resource"] is JObject; break;
                    case "resource_link": valid = block["uri"]?.Type == JTokenType.String && block["name"]?.Type == JTokenType.String; break;
                    default: valid = false; break;
                }
                if (!valid) throw new InvalidDataException("Malformed or unnegotiated MCP content block: " + kind);
            }
        }

        private static bool IsUncertainResult(JObject result)
        {
            if (result["isError"]?.Value<bool>() != true) return false;
            var text = string.Join("\n", (result["content"] as JArray ?? new JArray())
                .Where(b => (string)b["type"] == "text").Select(b => (string)b["text"]));
            // Upstream INTERNAL may be thrown while serializing a successful native mutation; a dialog can
            // also outlive the response. Only explicit pre-execution rejections establish a safe failure.
            string code = text.Split(':')[0].Trim();
            switch (code)
            {
                case "INVALID_ARGUMENT": case "ENTITY_NOT_FOUND": case "ENTITY_AMBIGUOUS":
                case "WRONG_DOC_TYPE": case "NO_ACTIVE_DOC": case "SKETCH_STATE":
                case "NOT_SUPPORTED": case "ADDIN_UNAVAILABLE": case "DISABLED":
                case "NOT_OWNED": case "DIRTY": case "BUSY": case "USER_BUSY":
                    return false;
                default: return true;
            }
        }

        private async Task<JObject> ExchangeAsync(JObject request, Task<JObject> response)
        { await SendAsync(request).ConfigureAwait(false); return await response.ConfigureAwait(false); }

        private async Task SendAsync(JObject message)
        {
            await _worker.Input.WriteLineAsync(message.ToString(Formatting.None)).ConfigureAwait(false);
            await _worker.Input.FlushAsync().ConfigureAwait(false);
        }

        private async Task SendCancellationAsync(long id)
        {
            try { await SendAsync(new JObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/cancelled",
                ["params"] = new JObject { ["requestId"] = id, ["reason"] = "Client cancelled or timed out; outcome requires reconciliation." } }).ConfigureAwait(false); }
            catch (Exception) { /* Best effort; persistent marker is authoritative. */ }
        }

        private async Task ReadOutputAsync()
        {
            try
            {
                string line;
                while ((line = await _worker.Output.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var message = JObject.Parse(line);
                    if ((string)message["jsonrpc"] != "2.0") throw new InvalidDataException("Invalid MCP JSON-RPC version.");
                    if (message["method"] != null) continue; // No client callbacks were negotiated.
                    lock (_sync)
                    {
                        if (message["id"]?.Type == JTokenType.Integer && (long)message["id"] == _pendingId)
                            _pending?.TrySetResult(message);
                    }
                }
                FailWorker(new EndOfStreamException("The MCP worker closed stdout."));
            }
            catch (Exception ex) { FailWorker(ex); }
        }

        private async Task ReadStderrAsync()
        {
            try
            {
                string line;
                while ((line = await _worker.Error.ReadLineAsync().ConfigureAwait(false)) != null)
                { _stderr.Enqueue(line); while (_stderr.Count > 64) _stderr.TryDequeue(out _); }
            }
            catch (Exception) { /* Stderr cannot interfere with JSON-RPC. */ }
        }

        private void FailWorker(Exception error)
        { lock (_sync) { _terminal = error; _pending?.TrySetException(error); } }
        private void StopWorker()
        { try { _worker.RequestStop(); } catch (InvalidOperationException) { } }

        public void Dispose()
        {
            lock (_disposeSync)
            {
                lock (_sync)
                {
                    if (_resourcesReleased) return;
                    _disposed = true;
                }
                _shutdown.Cancel();
                bool exited = false;
                try { _worker.RequestStop(); exited = _worker.WaitForExit(2000); }
                catch (Exception ex) { _boundary.Quarantine("Cannot confirm worker termination: " + ex.Message); }
                // Request cancellation must finish before lease release: it may still be preparing its dispatch record.
                bool drained = _gate.Wait(2000);
                if (!exited || !drained)
                {
                    if (drained) _gate.Release();
                    _boundary.Quarantine("Worker termination or request drain is unconfirmed; lease retained.");
                    throw new UncertainOutcomeException("Worker termination is unconfirmed. CAD lease retained; call Dispose again after stopping the worker.");
                }
                try
                {
                    _worker.Dispose();
                    _boundary.Dispose();
                    _shutdown.Dispose();
                    lock (_sync) { _resourcesReleased = true; }
                }
                finally { _gate.Release(); }
            }
        }
    }
}
