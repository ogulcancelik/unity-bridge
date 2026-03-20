// UnityBridge - Minimal HTTP bridge for AI-driven Unity Editor control
// Drop into Assets/Editor/ and it just works. No packages, no config.
// Listens on http://localhost:7778 for JSON commands.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Compilation;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityBridge
{
    // ─────────────────────────────────────────────────────────────
    //  Boot
    // ─────────────────────────────────────────────────────────────
    [InitializeOnLoad]
    public static class Bridge
    {
        const int Port = 7778;
        const string MenuToggle = "Tools/Unity Bridge/Enabled";

        static HttpListener _listener;
        static Thread _thread;
        static readonly ConcurrentQueue<PendingCommand> _queue = new();
        static volatile bool _running;
        static volatile bool _stopping;
        static volatile bool _listenerDied;
        static int _retryCount;

        const string ShutdownMsg = "Bridge is shutting down";
        const string DeferredSentinel = "__DEFERRED__";

        enum StopReason { Disabled, DomainReload, Quitting }

        // Deferred refresh: holds the HTTP connection open until compilation finishes
        static PendingCommand _deferredRefresh;
        static int _deferredIdleFrames;    // consecutive frames where !busy after grace
        static bool _deferredSawCompiling;
        static double _deferredRefreshStart;

        // Deferred capture sequence: multi-frame pause/capture/wait/resume
        static PendingCommand _deferredSequence;
        static CaptureSequenceState _sequenceState;

        /// <summary>Called by Cmd.Refresh to begin deferred response.</summary>
        internal static string BeginDeferredRefresh()
        {
            _deferredIdleFrames = 0;
            _deferredSawCompiling = false;
            _deferredRefreshStart = EditorApplication.timeSinceStartup;
            return DeferredSentinel;
        }

        /// <summary>Called by Cmd.CaptureSequence to begin deferred multi-frame sequence.</summary>
        internal static string BeginDeferredSequence(CaptureSequenceState state)
        {
            if (_deferredSequence != null)
                return Json.Obj("success", false, "error", "Another capture_sequence is already running");
            _sequenceState = state;
            return DeferredSentinel;
        }

        /// <summary>True when a capture sequence is in progress (rejects other commands).</summary>
        internal static bool IsSequenceActive => _deferredSequence != null;

        /// <summary>
        /// Multi-frame capture sequence state machine.
        /// Ticked each editor frame from ProcessQueue.
        /// </summary>
        internal class CaptureSequenceState
        {
            // Config
            public string Source;         // game, scene, main, custom
            public int Width = 1920;
            public int Height = 1080;
            public float[] Position;      // for custom source
            public float[] Rotation;      // for custom source
            public float Fov = 60f;
            public bool Ortho;
            public float OrthoSize = 40f;
            public List<SeqStep> Steps;
            public string DebugDir;

            // Runtime
            public int CurrentStep;
            public double StepStartTime;
            public bool WaitingForFile;
            public string WaitingFilePath;
            public bool WasPausedBefore;  // restore state on cleanup
            public double SequenceStartTime;
            public List<CaptureResult> Captures = new();
            public string Error;

            public struct SeqStep
            {
                public string Action;     // pause, resume, wait, step, capture
                public float Seconds;     // for wait
                public string Filename;   // for capture
                public int Frames;        // for step
            }

            public struct CaptureResult
            {
                public string Filename;
                public string Path;
            }

            /// <summary>Tick one frame. Returns true when sequence is complete.</summary>
            public bool Tick()
            {
                if (Error != null) return true;
                if (CurrentStep >= Steps.Count) return true;

                double now = EditorApplication.timeSinceStartup;
                var step = Steps[CurrentStep];

                switch (step.Action)
                {
                    case "pause":
                        EditorApplication.isPaused = true;
                        CurrentStep++;
                        StepStartTime = now;
                        return false;

                    case "resume":
                        EditorApplication.isPaused = false;
                        CurrentStep++;
                        StepStartTime = now;
                        return false;

                    case "step":
                    {
                        int frames = step.Frames > 0 ? step.Frames : 1;
                        for (int i = 0; i < frames; i++)
                            EditorApplication.Step();
                        CurrentStep++;
                        StepStartTime = now;
                        return false;
                    }

                    case "wait":
                    {
                        if (StepStartTime == 0) StepStartTime = now;
                        if (now - StepStartTime >= step.Seconds)
                        {
                            CurrentStep++;
                            StepStartTime = now;
                        }
                        return false;
                    }

                    case "capture":
                    {
                        if (WaitingForFile)
                        {
                            // Waiting for ScreenCapture file to appear
                            if (File.Exists(WaitingFilePath) && new FileInfo(WaitingFilePath).Length > 0)
                            {
                                Captures.Add(new CaptureResult { Filename = step.Filename, Path = WaitingFilePath });
                                WaitingForFile = false;
                                CurrentStep++;
                                StepStartTime = now;
                            }
                            else if (now - StepStartTime > 5.0)
                            {
                                Error = $"Timeout waiting for screenshot file: {WaitingFilePath}";
                            }
                            return false;
                        }

                        // Execute capture based on source
                        string filename = step.Filename ?? $"seq_{Captures.Count:D4}.png";
                        string fullPath = System.IO.Path.Combine(DebugDir, filename);

                        if (Source == "game")
                        {
                            if (!EditorApplication.isPlaying)
                            {
                                Error = "source:game requires play mode";
                                return true;
                            }
                            // Force repaint so we get fresh frame
                            InternalEditorUtility.RepaintAllViews();
                            // Delete existing file to detect when new one arrives
                            if (File.Exists(fullPath)) File.Delete(fullPath);
                            ScreenCapture.CaptureScreenshot(fullPath);
                            WaitingForFile = true;
                            WaitingFilePath = fullPath;
                            StepStartTime = now;
                            return false;
                        }

                        // scene, main, custom — RT render (synchronous)
                        string captureError = RenderToFile(fullPath);
                        if (captureError != null)
                        {
                            Error = captureError;
                            return true;
                        }
                        Captures.Add(new CaptureResult { Filename = filename, Path = fullPath });
                        CurrentStep++;
                        StepStartTime = now;
                        return false;
                    }

                    default:
                        Error = $"Unknown action: {step.Action}";
                        return true;
                }
            }

            string RenderToFile(string fullPath)
            {
                Camera cam = null;
                GameObject tempGo = null;

                try
                {
                    if (Source == "main")
                    {
                        cam = Camera.main;
                        if (cam == null) return "Camera.main is null";
                    }
                    else if (Source == "scene")
                    {
                        var sv = SceneView.lastActiveSceneView;
                        if (sv == null) return "No active Scene View";
                        cam = sv.camera;
                    }
                    else // custom
                    {
                        tempGo = new GameObject("__SeqCaptureCam");
                        tempGo.hideFlags = HideFlags.HideAndDontSave;
                        cam = tempGo.AddComponent<Camera>();
                        if (Position != null && Position.Length >= 3)
                            cam.transform.position = new Vector3(Position[0], Position[1], Position[2]);
                        if (Rotation != null && Rotation.Length >= 3)
                            cam.transform.eulerAngles = new Vector3(Rotation[0], Rotation[1], Rotation[2]);
                        cam.orthographic = Ortho;
                        cam.orthographicSize = OrthoSize;
                        cam.fieldOfView = Fov;
                        cam.nearClipPlane = 0.1f;
                        cam.farClipPlane = 500f;
                        cam.clearFlags = CameraClearFlags.SolidColor;
                        cam.backgroundColor = new Color(0.3f, 0.3f, 0.35f);

                        // Add URP camera data if available
                        var urpType = Type.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, Unity.RenderPipelines.Universal.Runtime");
                        if (urpType != null) tempGo.AddComponent(urpType);
                    }

                    var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32);
                    cam.targetTexture = rt;
                    cam.Render();

                    RenderTexture.active = rt;
                    var tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                    tex.Apply();

                    cam.targetTexture = null;
                    RenderTexture.active = null;
                    UnityEngine.Object.DestroyImmediate(rt);

                    var bytes = tex.EncodeToPNG();
                    UnityEngine.Object.DestroyImmediate(tex);
                    File.WriteAllBytes(fullPath, bytes);

                    return null; // success
                }
                catch (Exception ex)
                {
                    return ex.Message;
                }
                finally
                {
                    if (tempGo != null) UnityEngine.Object.DestroyImmediate(tempGo);
                }
            }

            public string BuildResponse()
            {
                var sb = new StringBuilder();
                sb.Append("{\"success\":").Append(Error == null ? "true" : "false");
                if (Error != null)
                    sb.Append(",\"error\":\"").Append(Json.Esc(Error)).Append('"');

                double elapsed = EditorApplication.timeSinceStartup - SequenceStartTime;
                sb.Append(",\"duration_ms\":").Append((int)(elapsed * 1000));
                sb.Append(",\"captures\":[");
                for (int i = 0; i < Captures.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"filename\":\"").Append(Json.Esc(Captures[i].Filename))
                      .Append("\",\"path\":\"").Append(Json.Esc(Captures[i].Path))
                      .Append("\"}");
                }
                sb.Append("]}");
                return sb.ToString();
            }
        }

        // ── Event ring buffer (thread-safe, read from HTTP thread) ──
        static readonly object _eventLock = new();
        static readonly List<BridgeEvent> _events = new();
        static long _eventCursor;
        const int MaxEvents = 1000;

        struct BridgeEvent
        {
            public long Id;
            public double Time;
            public string Type;     // log, compile_start, compile_end, play_mode, scene_change
            public string Severity; // info, warning, error (for logs)
            public string Message;
        }

        internal static void PushEvent(string type, string message, string severity = null)
        {
            lock (_eventLock)
            {
                _events.Add(new BridgeEvent
                {
                    Id = ++_eventCursor,
                    Time = EditorApplication.timeSinceStartup,
                    Type = type,
                    Severity = severity,
                    Message = message?.Length > 500 ? message.Substring(0, 500) : message
                });
                if (_events.Count > MaxEvents)
                    _events.RemoveRange(0, _events.Count - MaxEvents);
            }
        }

        static string GetEvents(long since, int limit)
        {
            lock (_eventLock)
            {
                var sb = new StringBuilder();
                sb.Append("{\"success\":true,\"cursor\":").Append(_eventCursor).Append(",\"events\":[");

                // Binary-ish scan: events are ordered by Id, find first with Id > since
                int startIdx = _events.Count; // default: no matches
                for (int i = 0; i < _events.Count; i++)
                {
                    if (_events[i].Id > since) { startIdx = i; break; }
                }

                bool first = true;
                int endIdx = Math.Min(startIdx + limit, _events.Count);
                for (int i = startIdx; i < endIdx; i++)
                {
                    var e = _events[i];
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"id\":").Append(e.Id)
                      .Append(",\"time\":").Append(e.Time.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
                      .Append(",\"type\":\"").Append(e.Type).Append('"');
                    if (e.Severity != null)
                        sb.Append(",\"severity\":\"").Append(e.Severity).Append('"');
                    sb.Append(",\"message\":\"").Append(Json.Esc(e.Message)).Append("\"}");
                }
                sb.Append("]}");
                return sb.ToString();
            }
        }

        // Cached on main thread for use by HTTP thread
        static string _unityVersion;
        static string _productName;

        // ── Lifecycle ──────────────────────────────────────────
        static Bridge()
        {
            // Cache values that can only be read from main thread
            _unityVersion = Application.unityVersion;
            _productName = Application.productName;

            // Restore toggle state (default: on)
            bool enabled = EditorPrefs.GetBool("UnityBridge.Enabled", true);
            Menu.SetChecked(MenuToggle, enabled);
            // Defer start to next editor update so socket is fully released after domain reload
            // (update runs even when Unity is unfocused, unlike delayCall)
            if (enabled)
            {
                _nextRetryTime = EditorApplication.timeSinceStartup + 0.1;
                EditorApplication.update += RetryTick;
            }

            EditorApplication.update += ProcessQueue;
            EditorApplication.quitting += () => Stop(StopReason.Quitting);
            AssemblyReloadEvents.beforeAssemblyReload += () => Stop(StopReason.DomainReload);

            // Event hooks
            Application.logMessageReceivedThreaded += OnLogMessage;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            CompilationPipeline.compilationStarted += _ => PushEvent("compile_start", "Compilation started");
            CompilationPipeline.compilationFinished += _ => PushEvent("compile_end", "Compilation finished");
            EditorSceneManager.sceneOpened += (scene, _) => PushEvent("scene_change", $"Opened: {scene.name} ({scene.path})");
            EditorSceneManager.sceneSaved += scene => PushEvent("scene_change", $"Saved: {scene.name}");
        }

        static void OnLogMessage(string message, string stackTrace, LogType type)
        {
            if (message.StartsWith("[UnityBridge]")) return;
            string severity = type switch
            {
                LogType.Error => "error",
                LogType.Exception => "error",
                LogType.Warning => "warning",
                LogType.Assert => "error",
                _ => "info"
            };
            PushEvent("log", message, severity);
        }

        static void OnPlayModeChanged(PlayModeStateChange state)
        {
            string msg = state switch
            {
                PlayModeStateChange.EnteredPlayMode => "Entered play mode",
                PlayModeStateChange.ExitingPlayMode => "Exiting play mode",
                PlayModeStateChange.EnteredEditMode => "Entered edit mode",
                PlayModeStateChange.ExitingEditMode => "Exiting edit mode",
                _ => state.ToString()
            };
            PushEvent("play_mode", msg);
        }

        [MenuItem(MenuToggle, priority = 100)]
        static void ToggleEnabled()
        {
            bool current = EditorPrefs.GetBool("UnityBridge.Enabled", true);
            bool next = !current;
            EditorPrefs.SetBool("UnityBridge.Enabled", next);
            Menu.SetChecked(MenuToggle, next);
            if (next)
            {
                _stopping = false;
                StartWithRetry();
            }
            else
            {
                EditorApplication.update -= RetryTick;
                _retryCount = 0;
                Stop(StopReason.Disabled);
            }
        }

        [MenuItem("Tools/Unity Bridge/Restart", priority = 101)]
        static void Restart()
        {
            Stop(StopReason.Disabled);
            _stopping = false;
            EditorPrefs.SetBool("UnityBridge.Enabled", true);
            Menu.SetChecked(MenuToggle, true);
            StartWithRetry();
        }

        static double _nextRetryTime;

        static void StartWithRetry()
        {
            // Respect disabled state (stale RetryTick could fire after toggle)
            if (!EditorPrefs.GetBool("UnityBridge.Enabled", true)) { _retryCount = 0; return; }
            if (_running || _stopping) return;

            try
            {
                Start();
                _retryCount = 0;
                EditorApplication.update -= RetryTick; // clean up if we had retries pending
            }
            catch (Exception ex)
            {
                _retryCount++;
                // Exponential backoff: 0.5s, 1s, 2s, 4s, then cap at 5s — never give up
                double delay = Math.Min(0.5 * Math.Pow(2, _retryCount - 1), 5.0);
                _nextRetryTime = EditorApplication.timeSinceStartup + delay;

                if (_retryCount <= 5)
                    Debug.LogWarning($"[UnityBridge] Port {Port} in use, retry #{_retryCount} in {delay:F1}s — {ex.Message}");
                else if (_retryCount % 10 == 0)
                    Debug.LogWarning($"[UnityBridge] Still retrying (attempt #{_retryCount})...");

                // RetryTick runs every frame, checks if it's time to retry
                EditorApplication.update -= RetryTick;
                EditorApplication.update += RetryTick;
            }
        }

        static void RetryTick()
        {
            if (_running || _stopping || EditorApplication.timeSinceStartup < _nextRetryTime)
                return;
            EditorApplication.update -= RetryTick;
            StartWithRetry();
        }

        static void Start()
        {
            if (_running) return;
            _stopping = false;
            _listenerDied = false;
            var listener = new HttpListener();
            try
            {
                listener.Prefixes.Add($"http://localhost:{Port}/");
                listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
                listener.Start();
            }
            catch
            {
                try { listener.Close(); } catch { }
                throw;
            }
            _listener = listener;
            _running = true;
            _thread = new Thread(ListenLoop) { IsBackground = true, Name = "UnityBridge" };
            _thread.Start();
            Debug.Log($"[UnityBridge] Listening on http://localhost:{Port}");
        }

        static void Stop(StopReason reason = StopReason.Disabled)
        {
            _stopping = true;
            EditorApplication.update -= RetryTick;
            _retryCount = 0;

            // Complete deferred capture sequence before closing
            var seq = _deferredSequence;
            _deferredSequence = null;
            if (seq != null && _sequenceState != null)
            {
                // Restore pause state
                EditorApplication.isPaused = _sequenceState.WasPausedBefore;
                seq.Result = Json.Obj("success", false, "error", "Bridge shutting down during sequence");
                seq.Complete();
                _sequenceState = null;
            }

            // Complete deferred refresh before closing listener (best-effort response)
            var deferred = _deferredRefresh;
            _deferredRefresh = null;
            if (deferred != null)
            {
                double elapsed = EditorApplication.timeSinceStartup - _deferredRefreshStart;
                bool isDomainReload = reason == StopReason.DomainReload;
                deferred.Result = Json.Obj(
                    "success", true,
                    "message", isDomainReload
                        ? "Compilation finished; domain reload imminent"
                        : "Bridge shutting down",
                    "domain_reload", isDomainReload,
                    "duration_ms", (int)(elapsed * 1000)
                );
                deferred.Complete();
                // Give HTTP thread a moment to flush the response
                Thread.Sleep(50);
            }

            if (_running)
            {
                _running = false;
                try { _listener?.Stop(); } catch { }
                try { _listener?.Close(); } catch { }
                _listener = null;

                // Wait for background thread to exit so socket is fully released
                try { _thread?.Join(3000); } catch { }
                if (_thread != null && _thread.IsAlive)
                    Debug.LogWarning("[UnityBridge] Listener thread did not exit within 3s");
                _thread = null;
            }

            // Cancel all pending commands so waiters don't hang
            DrainQueue(ShutdownMsg);

            Debug.Log("[UnityBridge] Stopped");
        }

        static void DrainQueue(string reason)
        {
            while (_queue.TryDequeue(out var cmd))
            {
                if (cmd.TryCancel())
                {
                    cmd.Error = reason;
                    cmd.Result = Json.Obj("success", false, "error", reason);
                }
                try { cmd.Done.Set(); } catch { }
                try { cmd.Done.Dispose(); } catch { }
            }
        }

        // ── HTTP listener (background thread) ──────────────────
        static void ListenLoop()
        {
            bool unexpected = false;
            while (_running)
            {
                try
                {
                    var ctx = _listener?.GetContext();
                    if (ctx == null) continue;
                    ThreadPool.QueueUserWorkItem(_ => HandleRequest(ctx));
                }
                catch (HttpListenerException)
                {
                    unexpected = _running && !_stopping;
                    break;
                }
                catch (ObjectDisposedException)
                {
                    unexpected = _running && !_stopping;
                    break;
                }
                catch (Exception ex)
                {
                    if (_running && !_stopping)
                        Debug.LogError($"[UnityBridge] Listen error: {ex.Message}");
                    unexpected = _running && !_stopping;
                    break;
                }
            }

            if (unexpected)
            {
                _running = false;
                _listenerDied = true; // picked up by ProcessQueue on main thread
            }
        }

        static void HandleRequest(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;
            res.ContentType = "application/json";

            try
            {
                // Reject browser-originated requests. Local CLI/tools typically send no Origin header;
                // websites do, which closes the main localhost CSRF hole without adding auth/config.
                if (!string.IsNullOrEmpty(req.Headers["Origin"]))
                {
                    Respond(res, 403, Json.Obj("error",
                        "Browser-originated requests are not allowed. Use a local tool/script instead of a web page."));
                    return;
                }

                if (req.HttpMethod == "OPTIONS")
                {
                    Respond(res, 405, Json.Obj("error", "OPTIONS not supported"));
                    return;
                }

                string path = req.Url.AbsolutePath.TrimEnd('/');

                if (path == "/health" && req.HttpMethod == "GET")
                {
                    Respond(res, 200, Json.Obj(
                        "status", "ok",
                        "unity", _unityVersion,
                        "project", _productName
                    ));
                    return;
                }

                if (path == "/api" && req.HttpMethod == "GET")
                {
                    Respond(res, 200, ApiSchema.Get());
                    return;
                }

                if (path == "/events" && req.HttpMethod == "GET")
                {
                    long since = 0;
                    int limit = 100;
                    var qs = req.QueryString;
                    if (qs["since"] != null) long.TryParse(qs["since"], out since);
                    if (qs["limit"] != null) int.TryParse(qs["limit"], out limit);
                    limit = Math.Min(limit, 500);
                    Respond(res, 200, GetEvents(since, limit));
                    return;
                }

                if (path == "/command" && req.HttpMethod == "POST")
                {
                    if (!_running || _stopping)
                    {
                        Respond(res, 503, Json.Obj("error", ShutdownMsg));
                        return;
                    }

                    string body;
                    using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
                        body = reader.ReadToEnd();

                    var pending = new PendingCommand(body);
                    _queue.Enqueue(pending);

                    // Wait for main thread to process (120s to allow for compilation)
                    if (pending.Done.WaitOne(120000))
                    {
                        int status = pending.Error == ShutdownMsg ? 503
                                   : pending.Error != null ? 400
                                   : 200;
                        Respond(res, status, pending.Result ?? Json.Obj("success", false, "error", "Empty result"));
                        try { pending.Done.Dispose(); } catch { }
                    }
                    else
                    {
                        // Prevent zombie: mark as timed-out so ProcessQueue skips it
                        bool wasStillQueued = pending.TryTimeout();
                        Respond(res, 504, Json.Obj("error",
                            wasStillQueued
                                ? "Timeout waiting for Unity main thread"
                                : "Command exceeded timeout while executing"));
                        // Only dispose if we successfully canceled (not mid-processing)
                        if (wasStillQueued)
                            try { pending.Done.Dispose(); } catch { }
                    }
                    return;
                }

                Respond(res, 404, Json.Obj("error", $"Unknown endpoint: {path}"));
            }
            catch (Exception ex)
            {
                try { Respond(res, 500, Json.Obj("error", ex.Message)); } catch { }
            }
        }

        static void Respond(HttpListenerResponse res, int status, string json)
        {
            res.StatusCode = status;
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            res.ContentLength64 = bytes.Length;
            res.OutputStream.Write(bytes, 0, bytes.Length);
            res.Close();
        }

        // ── Main thread processing ────────────────────────────
        static void ProcessQueue()
        {
            // Auto-restart listener if it died unexpectedly
            if (_listenerDied)
            {
                _listenerDied = false;
                if (EditorPrefs.GetBool("UnityBridge.Enabled", true) && !_stopping)
                {
                    Debug.LogWarning("[UnityBridge] Listener died unexpectedly — restarting...");
                    StartWithRetry();
                }
            }

            // ── Check deferred refresh ──
            if (_deferredRefresh != null)
            {
                double elapsed = EditorApplication.timeSinceStartup - _deferredRefreshStart;
                bool busy = EditorApplication.isCompiling || EditorApplication.isUpdating;

                if (busy)
                {
                    _deferredSawCompiling = true;
                    _deferredIdleFrames = 0;
                }
                else
                {
                    _deferredIdleFrames++;
                }

                // Settle when:
                // - Was busy then idle for 2+ consecutive frames, OR
                // - Never went busy after 1s grace period (nothing to compile/import)
                bool settled = _deferredSawCompiling
                    ? _deferredIdleFrames >= 2
                    : elapsed > 1.0;

                if (settled)
                {
                    _deferredRefresh.Result = Json.Obj(
                        "success", true,
                        "message", _deferredSawCompiling ? "Refresh completed (compiled)" : "Refresh completed",
                        "domain_reload", false,
                        "duration_ms", (int)(elapsed * 1000)
                    );
                    _deferredRefresh.Complete();
                    _deferredRefresh = null;
                }
            }

            // ── Check deferred capture sequence ──
            if (_deferredSequence != null && _sequenceState != null)
            {
                bool done = _sequenceState.Tick();
                if (done)
                {
                    // Restore pause state
                    EditorApplication.isPaused = _sequenceState.WasPausedBefore;
                    _deferredSequence.Result = _sequenceState.BuildResponse();
                    _deferredSequence.Complete();
                    _deferredSequence = null;
                    _sequenceState = null;
                }
            }

            // Process up to 10 commands per frame to stay responsive
            int count = 0;
            while (count < 10 && _queue.TryDequeue(out var cmd))
            {
                count++;

                // Skip commands that timed out or were canceled while queued
                if (!cmd.TryBeginProcessing())
                    continue;

                // Reject mutating commands while capture sequence is active
                if (_deferredSequence != null)
                {
                    cmd.Result = Json.Obj("success", false, "error", "capture_sequence in progress");
                    cmd.Complete();
                    continue;
                }

                try
                {
                    cmd.Result = CommandRouter.Execute(cmd.Body);
                }
                catch (Exception ex)
                {
                    cmd.Error = ex.Message;
                    cmd.Result = Json.Obj("success", false, "error", ex.Message);
                }
                finally
                {
                    // Deferred commands are completed later by the deferred check above
                    if (cmd.Result == DeferredSentinel)
                    {
                        // Check if this is a capture_sequence (has _sequenceState set)
                        if (_sequenceState != null)
                        {
                            _deferredSequence = cmd;
                        }
                        else
                        {
                            // Cancel any previous deferred refresh (shouldn't happen, but be safe)
                            var prev = _deferredRefresh;
                            if (prev != null)
                            {
                                prev.Result = Json.Obj("success", false, "error", "Superseded by new refresh");
                                prev.Complete();
                            }
                            _deferredRefresh = cmd;
                        }
                    }
                    else
                    {
                        cmd.Complete();
                    }
                }
            }
        }

        class PendingCommand
        {
            public readonly string Body;
            public string Result;
            public string Error;
            public readonly ManualResetEvent Done = new(false);

            // 0=queued, 1=processing, 2=completed, 3=timed-out, 4=canceled
            int _state;

            public PendingCommand(string body) { Body = body; }
            public bool TryBeginProcessing() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;
            public bool TryTimeout() => Interlocked.CompareExchange(ref _state, 3, 0) == 0;
            public bool TryCancel() => Interlocked.CompareExchange(ref _state, 4, 0) == 0;

            public void Complete()
            {
                Interlocked.CompareExchange(ref _state, 2, 1);
                try { Done.Set(); } catch { }
            }
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  Command Router
    // ─────────────────────────────────────────────────────────────
    static class CommandRouter
    {
        public static string Execute(string body)
        {
            var json = MiniJson.Deserialize(body) as Dictionary<string, object>;
            if (json == null)
                return Json.Obj("success", false, "error", "Invalid JSON");

            string type = json.GetStr("type");
            var p = json.GetDict("params") ?? new Dictionary<string, object>();

            return type switch
            {
                // Scene
                "get_hierarchy" => Cmd.GetHierarchy(p),
                "get_scene_info" => Cmd.GetSceneInfo(p),
                "load_scene" => Cmd.LoadScene(p),
                "save_scene" => Cmd.SaveScene(p),

                // GameObject
                "create_gameobject" => Cmd.CreateGameObject(p),
                "delete_gameobject" => Cmd.DeleteGameObject(p),
                "find_gameobjects" => Cmd.FindGameObjects(p),
                "modify_gameobject" => Cmd.ModifyGameObject(p),
                "duplicate_gameobject" => Cmd.DuplicateGameObject(p),

                // Components
                "add_component" => Cmd.AddComponent(p),
                "remove_component" => Cmd.RemoveComponent(p),
                "get_components" => Cmd.GetComponents(p),
                "set_property" => Cmd.SetProperty(p),
                "get_property" => Cmd.GetProperty(p),

                // Editor
                "editor_state" => Cmd.EditorState(p),
                "play" => Cmd.SetPlayMode(p, true),
                "pause" => Cmd.SetPause(p),
                "stop" => Cmd.SetPlayMode(p, false),
                "refresh" => Cmd.Refresh(p),
                "read_console" => Cmd.ReadConsole(p),
                "execute_menu_item" => Cmd.ExecuteMenuItem(p),

                // Assets
                "create_asset" => Cmd.CreateAsset(p),
                "find_assets" => Cmd.FindAssets(p),

                // Screenshot
                "screenshot" => Cmd.Screenshot(p),
                "capture" => Cmd.Capture(p),
                "capture_sequence" => Cmd.CaptureSequence(p),

                // Selection
                "get_selection" => Cmd.GetSelection(p),
                "set_selection" => Cmd.SetSelection(p),

                // Project info
                "get_project_info" => Cmd.GetProjectInfo(p),
                "get_tags" => Cmd.GetTags(p),
                "get_layers" => Cmd.GetLayers(p),
                "add_tag" => Cmd.AddTag(p),
                "add_layer" => Cmd.AddLayer(p),

                // Prefabs
                "create_prefab" => Cmd.CreatePrefab(p),
                "instantiate_prefab" => Cmd.InstantiatePrefab(p),

                // Batch
                "batch" => Cmd.Batch(p),

                // Escape hatch: call any static method
                "execute_method" => Cmd.ExecuteMethod(p),

                _ => Json.Obj("success", false, "error", $"Unknown command: {type}")
            };
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  Command Implementations
    // ─────────────────────────────────────────────────────────────
    static class Cmd
    {
        // ── Scene ──────────────────────────────────────────────
        public static string GetHierarchy(Dictionary<string, object> p)
        {
            var scene = SceneManager.GetActiveScene();
            string view = p.GetStr("view", "summary");   // summary | standard | full
            int maxDepth = p.GetInt("depth", 10);
            int limit = p.GetInt("limit", 1000);

            // Optional subtree root
            GameObject[] roots;
            string rootTarget = p.GetStr("root");
            if (!string.IsNullOrEmpty(rootTarget))
            {
                var rootGo = FindOne(rootTarget);
                if (rootGo == null) return Json.Obj("success", false, "error", $"Root not found: {rootTarget}");
                roots = new[] { rootGo };
            }
            else
            {
                roots = scene.GetRootGameObjects();
            }

            // Optional filters
            var filterDict = p.GetDict("filter");
            string nameContains = filterDict?.GetStr("name_contains");
            string hasComponent = filterDict?.GetStr("has_component");

            var sb = new StringBuilder();
            sb.Append("{\"success\":true,\"scene\":\"").Append(EscJson(scene.name))
              .Append("\",\"path\":\"").Append(EscJson(scene.path))
              .Append("\",\"objects\":[");

            bool first = true;
            int count = 0;
            foreach (var root in roots)
                CollectFlat(root, sb, ref first, ref count, 0, maxDepth, limit, view, nameContains, hasComponent);

            sb.Append("],\"count\":").Append(count);
            if (count >= limit) sb.Append(",\"truncated\":true");
            sb.Append('}');
            return sb.ToString();
        }

        static void CollectFlat(GameObject go, StringBuilder sb, ref bool first, ref int count,
            int depth, int maxDepth, int limit, string view, string nameContains, string hasComponent)
        {
            if (count >= limit || depth > maxDepth) return;

            // Apply filters
            bool passesFilter = true;
            if (nameContains != null && go.name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0)
                passesFilter = false;
            if (hasComponent != null && go.GetComponent(hasComponent) == null)
                passesFilter = false;

            if (passesFilter)
            {
                if (!first) sb.Append(',');
                first = false;
                count++;

                var t = go.transform;
                int parentId = t.parent != null ? t.parent.gameObject.GetInstanceID() : 0;

                // summary: id, name, parent, active, children count
                sb.Append("{\"id\":").Append(go.GetInstanceID())
                  .Append(",\"n\":\"").Append(EscJson(go.name))
                  .Append("\",\"a\":").Append(go.activeSelf ? "true" : "false")
                  .Append(",\"p\":").Append(parentId)
                  .Append(",\"d\":").Append(depth)
                  .Append(",\"cc\":").Append(t.childCount);

                if (view == "standard" || view == "full")
                {
                    sb.Append(",\"tag\":\"").Append(EscJson(go.tag))
                      .Append("\",\"layer\":").Append(go.layer)
                      .Append(",\"pos\":[").Append(t.position.x).Append(',').Append(t.position.y).Append(',').Append(t.position.z).Append(']');
                }

                if (view == "full")
                {
                    sb.Append(",\"rot\":[").Append(t.eulerAngles.x).Append(',').Append(t.eulerAngles.y).Append(',').Append(t.eulerAngles.z)
                      .Append("],\"scl\":[").Append(t.localScale.x).Append(',').Append(t.localScale.y).Append(',').Append(t.localScale.z)
                      .Append("],\"comp\":[");
                    var comps = go.GetComponents<Component>();
                    for (int i = 0; i < comps.Length; i++)
                    {
                        if (comps[i] == null) continue;
                        if (i > 0) sb.Append(',');
                        sb.Append('"').Append(EscJson(comps[i].GetType().Name)).Append('"');
                    }
                    sb.Append(']');
                }

                sb.Append('}');
            }

            // Always recurse into children (filters may match deeper)
            for (int i = 0; i < go.transform.childCount; i++)
                CollectFlat(go.transform.GetChild(i).gameObject, sb, ref first, ref count,
                    depth + 1, maxDepth, limit, view, nameContains, hasComponent);
        }

        public static string GetSceneInfo(Dictionary<string, object> p)
        {
            var scene = SceneManager.GetActiveScene();
            return Json.Obj(
                "success", true,
                "name", scene.name,
                "path", scene.path,
                "isDirty", scene.isDirty,
                "rootCount", scene.rootCount,
                "isLoaded", scene.isLoaded
            );
        }

        public static string LoadScene(Dictionary<string, object> p)
        {
            string path = p.GetStr("path");
            if (string.IsNullOrEmpty(path)) return Json.Obj("success", false, "error", "Missing 'path'");
            EditorSceneManager.OpenScene(path);
            return Json.Obj("success", true, "message", $"Loaded scene: {path}");
        }

        public static string SaveScene(Dictionary<string, object> p)
        {
            EditorSceneManager.SaveOpenScenes();
            return Json.Obj("success", true, "message", "Scenes saved");
        }

        // ── GameObject ─────────────────────────────────────────
        public static string CreateGameObject(Dictionary<string, object> p)
        {
            string name = p.GetStr("name", "GameObject");
            string primitive = p.GetStr("primitive_type");

            GameObject go;
            if (!string.IsNullOrEmpty(primitive))
            {
                if (Enum.TryParse<PrimitiveType>(primitive, true, out var pt))
                    go = GameObject.CreatePrimitive(pt);
                else
                    return Json.Obj("success", false, "error", $"Unknown primitive: {primitive}");
            }
            else
            {
                go = new GameObject(name);
            }

            go.name = name;
            Undo.RegisterCreatedObjectUndo(go, $"Create {name}");

            ApplyTransform(go, p);

            string parent = p.GetStr("parent");
            if (!string.IsNullOrEmpty(parent))
            {
                var parentGo = FindOne(parent);
                if (parentGo != null) go.transform.SetParent(parentGo.transform, true);
            }

            // Add components
            var comps = p.GetList("components");
            if (comps != null)
            {
                foreach (var c in comps)
                {
                    string typeName = c?.ToString();
                    if (string.IsNullOrEmpty(typeName)) continue;
                    var type = ResolveType(typeName);
                    if (type != null) Undo.AddComponent(go, type);
                }
            }

            EditorUtility.SetDirty(go);
            MarkSceneDirty(go);

            return Json.Obj("success", true, "id", go.GetInstanceID(), "name", go.name);
        }

        public static string DeleteGameObject(Dictionary<string, object> p)
        {
            var go = FindTarget(p);
            if (go == null) return Json.Obj("success", false, "error", "GameObject not found");
            string name = go.name;
            Undo.DestroyObjectImmediate(go);
            return Json.Obj("success", true, "message", $"Deleted {name}");
        }

        public static string FindGameObjects(Dictionary<string, object> p)
        {
            string search = p.GetStr("search", "");
            string method = p.GetStr("method", "by_name");
            bool includeInactive = p.GetBool("include_inactive", false);

            var results = new List<GameObject>();
            var allGos = Resources.FindObjectsOfTypeAll<GameObject>();

            foreach (var go in allGos)
            {
                // Filter out non-scene objects (prefabs, hidden)
                if (!includeInactive && !go.activeInHierarchy) continue;
                if (go.scene.name == null) continue; // Not in a scene
                if (go.hideFlags != HideFlags.None) continue;

                bool match = method switch
                {
                    "by_name" => go.name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0,
                    "by_tag" => string.Equals(go.tag, search, StringComparison.OrdinalIgnoreCase),
                    "by_layer" => go.layer == LayerMask.NameToLayer(search),
                    "by_component" => go.GetComponent(search) != null,
                    _ => go.name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
                };

                if (match) results.Add(go);
            }

            var sb = new StringBuilder();
            sb.Append("{\"success\":true,\"count\":").Append(results.Count).Append(",\"results\":[");
            for (int i = 0; i < results.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var g = results[i];
                sb.Append("{\"id\":").Append(g.GetInstanceID())
                  .Append(",\"name\":\"").Append(EscJson(g.name))
                  .Append("\",\"path\":\"").Append(EscJson(GetPath(g)))
                  .Append("\"}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        public static string ModifyGameObject(Dictionary<string, object> p)
        {
            var go = FindTarget(p);
            if (go == null) return Json.Obj("success", false, "error", "GameObject not found");

            Undo.RecordObject(go, "Modify GameObject");
            Undo.RecordObject(go.transform, "Modify Transform");

            string newName = p.GetStr("name");
            if (newName != null) go.name = newName;

            string tag = p.GetStr("tag");
            if (tag != null) go.tag = tag;

            if (p.ContainsKey("active"))
                go.SetActive(p.GetBool("active", true));

            if (p.ContainsKey("layer"))
            {
                string layerName = p.GetStr("layer");
                int layer = LayerMask.NameToLayer(layerName);
                if (layer >= 0) go.layer = layer;
            }

            ApplyTransform(go, p);

            string parent = p.GetStr("parent");
            if (parent != null)
            {
                if (parent == "" || parent == "null")
                    go.transform.SetParent(null);
                else
                {
                    var parentGo = FindOne(parent);
                    if (parentGo != null) go.transform.SetParent(parentGo.transform, true);
                }
            }

            EditorUtility.SetDirty(go);
            MarkSceneDirty(go);

            return Json.Obj("success", true, "id", go.GetInstanceID(), "name", go.name);
        }

        public static string DuplicateGameObject(Dictionary<string, object> p)
        {
            var go = FindTarget(p);
            if (go == null) return Json.Obj("success", false, "error", "GameObject not found");

            var dup = UnityEngine.Object.Instantiate(go);
            dup.name = p.GetStr("name", go.name + "_Copy");
            Undo.RegisterCreatedObjectUndo(dup, "Duplicate");

            if (go.transform.parent != null)
                dup.transform.SetParent(go.transform.parent);

            ApplyTransform(dup, p);
            EditorUtility.SetDirty(dup);
            MarkSceneDirty(dup);

            return Json.Obj("success", true, "id", dup.GetInstanceID(), "name", dup.name);
        }

        // ── Components ─────────────────────────────────────────
        public static string AddComponent(Dictionary<string, object> p)
        {
            var go = FindTarget(p);
            if (go == null) return Json.Obj("success", false, "error", "GameObject not found");

            string typeName = p.GetStr("component_type");
            if (string.IsNullOrEmpty(typeName)) return Json.Obj("success", false, "error", "Missing 'component_type'");

            var type = ResolveType(typeName);
            if (type == null) return Json.Obj("success", false, "error", $"Type not found: {typeName}");

            var comp = Undo.AddComponent(go, type);
            if (comp == null) return Json.Obj("success", false, "error", $"Failed to add {typeName}");

            EditorUtility.SetDirty(go);
            MarkSceneDirty(go);

            return Json.Obj("success", true, "message", $"Added {typeName} to {go.name}");
        }

        public static string RemoveComponent(Dictionary<string, object> p)
        {
            var go = FindTarget(p);
            if (go == null) return Json.Obj("success", false, "error", "GameObject not found");

            string typeName = p.GetStr("component_type");
            var type = ResolveType(typeName);
            if (type == null) return Json.Obj("success", false, "error", $"Type not found: {typeName}");

            var comp = go.GetComponent(type);
            if (comp == null) return Json.Obj("success", false, "error", $"{typeName} not found on {go.name}");

            Undo.DestroyObjectImmediate(comp);
            return Json.Obj("success", true, "message", $"Removed {typeName} from {go.name}");
        }

        public static string GetComponents(Dictionary<string, object> p)
        {
            var go = FindTarget(p);
            if (go == null) return Json.Obj("success", false, "error", "GameObject not found");

            bool detailed = p.GetBool("detailed", false);
            var comps = go.GetComponents<Component>();

            var sb = new StringBuilder();
            sb.Append("{\"success\":true,\"gameobject\":\"").Append(EscJson(go.name))
              .Append("\",\"components\":[");

            for (int i = 0; i < comps.Length; i++)
            {
                if (comps[i] == null) continue;
                if (i > 0) sb.Append(',');
                var c = comps[i];
                sb.Append("{\"type\":\"").Append(EscJson(c.GetType().Name))
                  .Append("\",\"fullType\":\"").Append(EscJson(c.GetType().FullName)).Append('"');

                if (detailed)
                {
                    sb.Append(",\"properties\":{");
                    SerializeProperties(c, sb);
                    sb.Append('}');
                }
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        public static string SetProperty(Dictionary<string, object> p)
        {
            var go = FindTarget(p);
            if (go == null) return Json.Obj("success", false, "error", "GameObject not found");

            string compType = p.GetStr("component_type");
            string propName = p.GetStr("property");
            object value = p.ContainsKey("value") ? p["value"] : null;

            if (string.IsNullOrEmpty(compType)) return Json.Obj("success", false, "error", "Missing 'component_type'");
            if (string.IsNullOrEmpty(propName)) return Json.Obj("success", false, "error", "Missing 'property'");

            Component comp;
            if (compType == "Transform")
                comp = go.transform;
            else
            {
                var type = ResolveType(compType);
                if (type == null) return Json.Obj("success", false, "error", $"Type not found: {compType}");
                comp = go.GetComponent(type);
            }

            if (comp == null) return Json.Obj("success", false, "error", $"Component {compType} not found on {go.name}");

            Undo.RecordObject(comp, $"Set {propName}");

            string error = ReflectionSetProperty(comp, propName, value);
            if (error != null) return Json.Obj("success", false, "error", error);

            EditorUtility.SetDirty(comp);
            MarkSceneDirty(go);

            return Json.Obj("success", true, "message", $"Set {compType}.{propName} on {go.name}");
        }

        public static string GetProperty(Dictionary<string, object> p)
        {
            var go = FindTarget(p);
            if (go == null) return Json.Obj("success", false, "error", "GameObject not found");

            string compType = p.GetStr("component_type");
            string propName = p.GetStr("property");

            if (string.IsNullOrEmpty(compType)) return Json.Obj("success", false, "error", "Missing 'component_type'");
            if (string.IsNullOrEmpty(propName)) return Json.Obj("success", false, "error", "Missing 'property'");

            Component comp;
            if (compType == "Transform")
                comp = go.transform;
            else
            {
                var type = ResolveType(compType);
                if (type == null) return Json.Obj("success", false, "error", $"Type not found: {compType}");
                comp = go.GetComponent(type);
            }
            if (comp == null) return Json.Obj("success", false, "error", $"Component {compType} not found");

            var result = ReflectionGetProperty(comp, propName);
            if (result.error != null) return Json.Obj("success", false, "error", result.error);

            return Json.Obj("success", true, "property", propName, "value", result.value);
        }

        // ── Editor ─────────────────────────────────────────────
        public static string EditorState(Dictionary<string, object> p)
        {
            return Json.Obj(
                "success", true,
                "isPlaying", EditorApplication.isPlaying,
                "isPaused", EditorApplication.isPaused,
                "isCompiling", EditorApplication.isCompiling,
                "isUpdating", EditorApplication.isUpdating,
                "platform", EditorUserBuildSettings.activeBuildTarget.ToString(),
                "unityVersion", Application.unityVersion,
                "projectPath", Application.dataPath,
                "sceneName", SceneManager.GetActiveScene().name,
                "scenePath", SceneManager.GetActiveScene().path
            );
        }

        public static string SetPlayMode(Dictionary<string, object> p, bool play)
        {
            EditorApplication.isPlaying = play;
            return Json.Obj("success", true, "isPlaying", play);
        }

        public static string SetPause(Dictionary<string, object> p)
        {
            EditorApplication.isPaused = !EditorApplication.isPaused;
            return Json.Obj("success", true, "isPaused", EditorApplication.isPaused);
        }

        public static string Refresh(Dictionary<string, object> p)
        {
            AssetDatabase.Refresh();
            // ProcessQueue sees the sentinel, stores the PendingCommand, and
            // polls isCompiling/isUpdating each frame until settled
            return Bridge.BeginDeferredRefresh();
        }

        public static string ReadConsole(Dictionary<string, object> p)
        {
            // Unity doesn't expose console logs via public API easily.
            // We use reflection to access the internal LogEntries class.
            try
            {
                int count = p.GetInt("count", 20);
                var logEntriesType = System.Type.GetType("UnityEditor.LogEntries, UnityEditor");
                if (logEntriesType == null)
                    return Json.Obj("success", false, "error", "Cannot access LogEntries");

                // Get total count
                var getCount = logEntriesType.GetMethod("GetCount",
                    BindingFlags.Public | BindingFlags.Static);
                // StartGettingEntries / GetEntryInternal / EndGettingEntries
                var startMethod = logEntriesType.GetMethod("StartGettingEntries",
                    BindingFlags.Public | BindingFlags.Static);
                var endMethod = logEntriesType.GetMethod("EndGettingEntries",
                    BindingFlags.Public | BindingFlags.Static);

                int totalCount = (int)getCount.Invoke(null, null);
                startMethod.Invoke(null, null);

                int startIdx = Math.Max(0, totalCount - count);
                var sb = new StringBuilder();
                sb.Append("{\"success\":true,\"total\":").Append(totalCount).Append(",\"entries\":[");

                // Use LogEntry struct
                var logEntryType = System.Type.GetType("UnityEditor.LogEntry, UnityEditor");
                var getEntry = logEntriesType.GetMethod("GetEntryInternal",
                    BindingFlags.Public | BindingFlags.Static);

                if (logEntryType != null && getEntry != null)
                {
                    var entry = Activator.CreateInstance(logEntryType);
                    var msgField = logEntryType.GetField("message",
                        BindingFlags.Public | BindingFlags.Instance);
                    var modeField = logEntryType.GetField("mode",
                        BindingFlags.Public | BindingFlags.Instance);

                    for (int i = startIdx; i < totalCount; i++)
                    {
                        getEntry.Invoke(null, new object[] { i, entry });
                        string msg = msgField?.GetValue(entry) as string ?? "";
                        int mode = (int)(modeField?.GetValue(entry) ?? 0);

                        // mode: bit flags, but roughly: errors have bit 0, warnings bit 1, log bit 2
                        string logType = "Log";
                        if ((mode & (1 << 0)) != 0) logType = "Error";
                        else if ((mode & (1 << 1)) != 0) logType = "Warning";

                        if (i > startIdx) sb.Append(',');
                        sb.Append("{\"type\":\"").Append(logType)
                          .Append("\",\"message\":\"").Append(EscJson(msg.Length > 500 ? msg.Substring(0, 500) : msg))
                          .Append("\"}");
                    }
                }

                endMethod.Invoke(null, null);
                sb.Append("]}");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return Json.Obj("success", false, "error", $"ReadConsole failed: {ex.Message}");
            }
        }

        public static string ExecuteMenuItem(Dictionary<string, object> p)
        {
            string menu = p.GetStr("menu_path");
            if (string.IsNullOrEmpty(menu)) return Json.Obj("success", false, "error", "Missing 'menu_path'");

            bool result = EditorApplication.ExecuteMenuItem(menu);
            return Json.Obj("success", result, "message", result ? $"Executed: {menu}" : $"Menu item not found: {menu}");
        }

        // ── Assets ─────────────────────────────────────────────
        public static string CreateAsset(Dictionary<string, object> p)
        {
            string assetType = p.GetStr("asset_type");
            string path = p.GetStr("path");

            if (string.IsNullOrEmpty(path)) return Json.Obj("success", false, "error", "Missing 'path'");

            switch (assetType?.ToLower())
            {
                case "material":
                    var mat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
                    AssetDatabase.CreateAsset(mat, path);
                    AssetDatabase.Refresh();
                    return Json.Obj("success", true, "path", path, "message", "Material created");

                case "folder":
                    string parentFolder = Path.GetDirectoryName(path)?.Replace('\\', '/');
                    string folderName = Path.GetFileName(path);
                    AssetDatabase.CreateFolder(parentFolder, folderName);
                    return Json.Obj("success", true, "path", path, "message", "Folder created");

                default:
                    return Json.Obj("success", false, "error", $"Unsupported asset_type: {assetType}. Supported: material, folder");
            }
        }

        public static string FindAssets(Dictionary<string, object> p)
        {
            string filter = p.GetStr("filter", "");
            string searchIn = p.GetStr("search_in", "Assets");

            var guids = AssetDatabase.FindAssets(filter, new[] { searchIn });
            var sb = new StringBuilder();
            sb.Append("{\"success\":true,\"count\":").Append(guids.Length).Append(",\"assets\":[");

            int max = Math.Min(guids.Length, p.GetInt("max_results", 50));
            for (int i = 0; i < max; i++)
            {
                if (i > 0) sb.Append(',');
                string assetPath = AssetDatabase.GUIDToAssetPath(guids[i]);
                sb.Append("{\"guid\":\"").Append(guids[i])
                  .Append("\",\"path\":\"").Append(EscJson(assetPath))
                  .Append("\"}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        // ── Screenshot ─────────────────────────────────────────
        public static string Screenshot(Dictionary<string, object> p)
        {
            string filename = p.GetStr("filename", "screenshot.png");
            string path = p.GetStr("path", "Assets");
            string fullPath = Path.Combine(Application.dataPath, "..", path, filename);

            // Capture the scene view
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
                return Json.Obj("success", false, "error", "No active Scene View");

            int width = p.GetInt("width", 1920);
            int height = p.GetInt("height", 1080);

            var cam = sceneView.camera;
            var rt = new RenderTexture(width, height, 24);
            cam.targetTexture = rt;
            cam.Render();

            RenderTexture.active = rt;
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();

            cam.targetTexture = null;
            RenderTexture.active = null;
            UnityEngine.Object.DestroyImmediate(rt);

            var bytes = tex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tex);
            File.WriteAllBytes(fullPath, bytes);

            return Json.Obj("success", true, "path", fullPath);
        }

        // ── Capture (debug screenshot with custom camera) ──────
        public static string Capture(Dictionary<string, object> p)
        {
            // Output: always _debug/capture.png (overwritten each time)
            string debugDir = Path.Combine(Application.dataPath, "..", "_debug");
            Directory.CreateDirectory(debugDir);
            string filename = p.GetStr("filename", "capture.png");
            string fullPath = Path.Combine(debugDir, filename);

            int width = p.GetInt("width", 1920);
            int height = p.GetInt("height", 1080);

            // Camera params
            float[] pos = p.GetFloatArray("position");       // [x,y,z] world position
            float[] rot = p.GetFloatArray("rotation");       // [x,y,z] euler degrees
            bool ortho = p.GetBool("ortho", false);
            float orthoSize = (float)p.GetDouble("ortho_size", 40);
            float fov = (float)p.GetDouble("fov", 60);

            // Preset modes: "topdown", "isometric" — override position/rotation
            string mode = p.GetStr("mode");
            float[] center = p.GetFloatArray("center") ?? new float[] { 0, 0 };  // [x,z]
            float size = (float)p.GetDouble("size", 80);  // world units to fit

            if (mode == "topdown")
            {
                pos = new float[] { center[0], 100, center[1] };
                rot = new float[] { 90, 0, 0 };
                ortho = true;
                orthoSize = size / 2f * ((float)height / width);  // fit width
                if (orthoSize < size / 2f) orthoSize = size / 2f; // ensure we see full area
            }
            else if (mode == "isometric")
            {
                // 45° angle, looking at center from the south
                float dist = size * 0.7f;
                pos = new float[] { center[0], dist * 0.7f, center[1] - dist * 0.5f };
                rot = new float[] { 45, 0, 0 };
                ortho = true;
                orthoSize = size / 2f;
            }
            else if (mode == "front")
            {
                float dist = size * 0.6f;
                pos = new float[] { center[0], size * 0.2f, center[1] - dist };
                rot = new float[] { 15, 0, 0 };
                fov = 60;
            }

            // Fallback: if no position given at all, use scene view camera
            if (pos == null)
            {
                var sv = SceneView.lastActiveSceneView;
                if (sv == null)
                    return Json.Obj("success", false, "error", "No position and no active Scene View");
                pos = new float[] { sv.camera.transform.position.x, sv.camera.transform.position.y, sv.camera.transform.position.z };
                rot = new float[] { sv.camera.transform.eulerAngles.x, sv.camera.transform.eulerAngles.y, sv.camera.transform.eulerAngles.z };
            }
            if (rot == null) rot = new float[] { 90, 0, 0 };

            // Create temp camera
            var go = new GameObject("__BridgeCaptureCam");
            go.hideFlags = HideFlags.HideAndDontSave;
            var cam = go.AddComponent<Camera>();
            cam.transform.position = new Vector3(pos[0], pos[1], pos[2]);
            cam.transform.eulerAngles = new Vector3(rot[0], rot[1], rot[2]);
            cam.orthographic = ortho;
            cam.orthographicSize = orthoSize;
            cam.fieldOfView = fov;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 500f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.3f, 0.3f, 0.35f);

            // Add URP camera data if available
            var urpType = Type.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, Unity.RenderPipelines.Universal.Runtime");
            if (urpType != null)
                go.AddComponent(urpType);

            // Render
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;
            cam.Render();

            RenderTexture.active = rt;
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();

            cam.targetTexture = null;
            RenderTexture.active = null;
            UnityEngine.Object.DestroyImmediate(rt);
            UnityEngine.Object.DestroyImmediate(go);

            var captureBytes = tex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tex);

            File.WriteAllBytes(fullPath, captureBytes);
            return Json.Obj("success", true, "path", fullPath);
        }

        // ── Capture Sequence (multi-frame deferred) ───────────
        public static string CaptureSequence(Dictionary<string, object> p)
        {
            if (Bridge.IsSequenceActive)
                return Json.Obj("success", false, "error", "Another capture_sequence is already running");

            string source = p.GetStr("source", "game");
            if (source != "game" && source != "scene" && source != "main" && source != "custom")
                return Json.Obj("success", false, "error", $"Invalid source: {source}. Use: game, scene, main, custom");

            var stepList = p.GetList("steps");
            if (stepList == null || stepList.Count == 0)
                return Json.Obj("success", false, "error", "Missing or empty 'steps' array");

            // Parse steps + validate total wait time
            var steps = new List<Bridge.CaptureSequenceState.SeqStep>();
            float totalWait = 0f;
            foreach (var raw in stepList)
            {
                if (raw is not Dictionary<string, object> sd)
                    return Json.Obj("success", false, "error", "Each step must be a JSON object");

                string action = sd.GetStr("action");
                if (action == null)
                    return Json.Obj("success", false, "error", "Each step needs an 'action'");

                var step = new Bridge.CaptureSequenceState.SeqStep { Action = action };

                switch (action)
                {
                    case "pause":
                    case "resume":
                        break;
                    case "wait":
                        step.Seconds = (float)sd.GetDouble("seconds", 0.1);
                        totalWait += step.Seconds;
                        break;
                    case "step":
                        step.Frames = sd.GetInt("frames", 1);
                        break;
                    case "capture":
                        step.Filename = sd.GetStr("filename");
                        // Sanitize filename
                        if (step.Filename != null)
                        {
                            step.Filename = step.Filename.Replace("..", "").Replace("/", "").Replace("\\", "");
                            if (!step.Filename.EndsWith(".png")) step.Filename += ".png";
                        }
                        break;
                    default:
                        return Json.Obj("success", false, "error", $"Unknown step action: {action}");
                }

                steps.Add(step);
            }

            if (totalWait > 90f)
                return Json.Obj("success", false, "error", $"Total wait time ({totalWait:F1}s) exceeds 90s limit");

            string debugDir = Path.Combine(Application.dataPath, "..", "_debug");
            Directory.CreateDirectory(debugDir);

            var state = new Bridge.CaptureSequenceState
            {
                Source = source,
                Width = p.GetInt("width", 1920),
                Height = p.GetInt("height", 1080),
                Position = p.GetFloatArray("position"),
                Rotation = p.GetFloatArray("rotation"),
                Fov = (float)p.GetDouble("fov", 60),
                Ortho = p.GetBool("ortho", false),
                OrthoSize = (float)p.GetDouble("ortho_size", 40),
                Steps = steps,
                DebugDir = debugDir,
                CurrentStep = 0,
                WasPausedBefore = EditorApplication.isPaused,
                SequenceStartTime = EditorApplication.timeSinceStartup,
            };

            return Bridge.BeginDeferredSequence(state);
        }

        // ── Escape hatch ───────────────────────────────────────
        public static string ExecuteMethod(Dictionary<string, object> p)
        {
            string typeName = p.GetStr("type_name");
            string methodName = p.GetStr("method_name");

            if (string.IsNullOrEmpty(typeName)) return Json.Obj("success", false, "error", "Missing 'type_name'");
            if (string.IsNullOrEmpty(methodName)) return Json.Obj("success", false, "error", "Missing 'method_name'");

            var type = ResolveType(typeName);
            if (type == null) return Json.Obj("success", false, "error", $"Type not found: {typeName}");

            var args = p.GetList("args");
            var argTypeNames = p.GetList("arg_types");
            var flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic;

            MethodInfo method;
            if (argTypeNames != null)
            {
                // Exact signature match via arg_types
                var argTypes = new Type[argTypeNames.Count];
                for (int i = 0; i < argTypeNames.Count; i++)
                {
                    argTypes[i] = ResolveType(argTypeNames[i].ToString());
                    if (argTypes[i] == null)
                        return Json.Obj("success", false, "error", $"arg_types[{i}] not found: {argTypeNames[i]}");
                }
                method = type.GetMethod(methodName, flags, null, argTypes, null);
                if (method == null)
                    return Json.Obj("success", false, "error", $"No matching overload: {typeName}.{methodName}({string.Join(", ", argTypeNames)})");
            }
            else if (args != null)
            {
                // Match by name + arg count
                var candidates = type.GetMethods(flags)
                    .Where(m => m.Name == methodName && m.GetParameters().Length == args.Count)
                    .ToArray();
                if (candidates.Length == 0)
                    return Json.Obj("success", false, "error", $"No overload of {typeName}.{methodName} takes {args.Count} args");
                if (candidates.Length > 1)
                {
                    var sigs = string.Join("; ", candidates.Select(m =>
                        $"({string.Join(", ", m.GetParameters().Select(pi => $"{pi.ParameterType.Name} {pi.Name}"))})"));
                    return Json.Obj("success", false, "error", $"Ambiguous: {candidates.Length} overloads match. Provide arg_types. Candidates: {sigs}");
                }
                method = candidates[0];
            }
            else
            {
                // No args
                method = type.GetMethod(methodName, flags);
                if (method == null)
                    return Json.Obj("success", false, "error", $"Method not found: {typeName}.{methodName}");
            }

            try
            {
                object[] convertedArgs = null;
                if (args != null)
                {
                    var paramInfos = method.GetParameters();
                    convertedArgs = new object[paramInfos.Length];
                    for (int i = 0; i < paramInfos.Length; i++)
                        convertedArgs[i] = ConvertValue(i < args.Count ? args[i] : null, paramInfos[i].ParameterType);
                }
                var result = method.Invoke(null, convertedArgs);
                return Json.Obj("success", true, "result", result?.ToString());
            }
            catch (Exception ex)
            {
                return Json.Obj("success", false, "error", $"Method threw: {ex.InnerException?.Message ?? ex.Message}");
            }
        }

        // ── Selection ───────────────────────────────────────────
        public static string GetSelection(Dictionary<string, object> p)
        {
            var sb = new StringBuilder();
            sb.Append("{\"success\":true,\"activeGameObject\":");
            var ago = UnityEditor.Selection.activeGameObject;
            if (ago != null)
                sb.Append("{\"id\":").Append(ago.GetInstanceID())
                  .Append(",\"name\":\"").Append(EscJson(ago.name)).Append("\"}");
            else
                sb.Append("null");

            sb.Append(",\"count\":").Append(UnityEditor.Selection.gameObjects.Length);
            sb.Append(",\"gameObjects\":[");
            var gos = UnityEditor.Selection.gameObjects;
            for (int i = 0; i < gos.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(gos[i].GetInstanceID())
                  .Append(",\"name\":\"").Append(EscJson(gos[i].name)).Append("\"}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        public static string SetSelection(Dictionary<string, object> p)
        {
            string target = p.GetStr("target");
            if (string.IsNullOrEmpty(target)) return Json.Obj("success", false, "error", "Missing 'target'");

            var go = FindOne(target);
            if (go == null) return Json.Obj("success", false, "error", $"GameObject not found: {target}");

            UnityEditor.Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);
            return Json.Obj("success", true, "message", $"Selected {go.name}");
        }

        // ── Project Info ───────────────────────────────────────
        public static string GetProjectInfo(Dictionary<string, object> p)
        {
            string renderPipeline = "BuiltIn";
            var currentRP = UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline;
            if (currentRP != null)
            {
                string rpType = currentRP.GetType().Name;
                if (rpType.Contains("Universal")) renderPipeline = "URP";
                else if (rpType.Contains("HDRender")) renderPipeline = "HDRP";
                else renderPipeline = rpType;
            }

            return Json.Obj(
                "success", true,
                "projectName", Application.productName,
                "unityVersion", Application.unityVersion,
                "platform", EditorUserBuildSettings.activeBuildTarget.ToString(),
                "renderPipeline", renderPipeline,
                "dataPath", Application.dataPath,
                "companyName", Application.companyName,
                "productName", Application.productName,
                "isPlaying", EditorApplication.isPlaying,
                "isCompiling", EditorApplication.isCompiling
            );
        }

        public static string GetTags(Dictionary<string, object> p)
        {
            var tags = UnityEditorInternal.InternalEditorUtility.tags;
            var sb = new StringBuilder();
            sb.Append("{\"success\":true,\"tags\":[");
            for (int i = 0; i < tags.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(EscJson(tags[i])).Append('"');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        public static string GetLayers(Dictionary<string, object> p)
        {
            var sb = new StringBuilder();
            sb.Append("{\"success\":true,\"layers\":[");
            bool first = true;
            for (int i = 0; i < 32; i++)
            {
                string name = LayerMask.LayerToName(i);
                if (string.IsNullOrEmpty(name)) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"index\":").Append(i).Append(",\"name\":\"").Append(EscJson(name)).Append("\"}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        public static string AddTag(Dictionary<string, object> p)
        {
            string tag = p.GetStr("tag");
            if (string.IsNullOrEmpty(tag)) return Json.Obj("success", false, "error", "Missing 'tag'");

            // Check if tag already exists
            var tags = UnityEditorInternal.InternalEditorUtility.tags;
            foreach (var t in tags)
                if (t == tag) return Json.Obj("success", true, "message", $"Tag '{tag}' already exists");

            UnityEditorInternal.InternalEditorUtility.AddTag(tag);
            return Json.Obj("success", true, "message", $"Tag '{tag}' added");
        }

        public static string AddLayer(Dictionary<string, object> p)
        {
            string layerName = p.GetStr("layer");
            if (string.IsNullOrEmpty(layerName)) return Json.Obj("success", false, "error", "Missing 'layer'");

            // Find first empty user layer (8-31)
            SerializedObject tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            SerializedProperty layers = tagManager.FindProperty("layers");

            for (int i = 8; i < 32; i++)
            {
                SerializedProperty layer = layers.GetArrayElementAtIndex(i);
                if (string.IsNullOrEmpty(layer.stringValue))
                {
                    layer.stringValue = layerName;
                    tagManager.ApplyModifiedProperties();
                    return Json.Obj("success", true, "message", $"Layer '{layerName}' added at index {i}");
                }
                if (layer.stringValue == layerName)
                    return Json.Obj("success", true, "message", $"Layer '{layerName}' already exists at index {i}");
            }
            return Json.Obj("success", false, "error", "No empty layer slots available");
        }

        // ── Prefabs ────────────────────────────────────────────
        public static string CreatePrefab(Dictionary<string, object> p)
        {
            var go = FindTarget(p);
            if (go == null) return Json.Obj("success", false, "error", "GameObject not found");

            string path = p.GetStr("path", $"Assets/{go.name}.prefab");
            if (!path.EndsWith(".prefab")) path += ".prefab";

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(Path.Combine(Application.dataPath, "..", dir)))
            {
                Directory.CreateDirectory(Path.Combine(Application.dataPath, "..", dir));
                AssetDatabase.Refresh();
            }

            var prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
            if (prefab == null) return Json.Obj("success", false, "error", "Failed to create prefab");

            return Json.Obj("success", true, "path", path, "message", $"Prefab created from {go.name}");
        }

        public static string InstantiatePrefab(Dictionary<string, object> p)
        {
            string path = p.GetStr("path");
            if (string.IsNullOrEmpty(path)) return Json.Obj("success", false, "error", "Missing 'path'");

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) return Json.Obj("success", false, "error", $"Prefab not found: {path}");

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            Undo.RegisterCreatedObjectUndo(instance, "Instantiate Prefab");

            ApplyTransform(instance, p);
            EditorUtility.SetDirty(instance);
            MarkSceneDirty(instance);

            return Json.Obj("success", true, "id", instance.GetInstanceID(), "name", instance.name);
        }

        // ── Batch ──────────────────────────────────────────────
        public static string Batch(Dictionary<string, object> p)
        {
            var commands = p.GetList("commands");
            if (commands == null || commands.Count == 0)
                return Json.Obj("success", false, "error", "Missing or empty 'commands' array");

            var sb = new StringBuilder();
            sb.Append("{\"success\":true,\"results\":[");

            for (int i = 0; i < commands.Count; i++)
            {
                if (i > 0) sb.Append(',');
                if (commands[i] is Dictionary<string, object> cmd)
                {
                    string cmdType = cmd.GetStr("type");
                    var cmdParams = cmd.GetDict("params") ?? new Dictionary<string, object>();

                    // refresh cannot be deferred inside batch — use it as a top-level command
                    if (cmdType == "refresh")
                    {
                        sb.Append(Json.Obj("success", false, "error",
                            "refresh cannot be used inside batch (it defers response). Call it as a separate command."));
                        continue;
                    }

                    // Build a fake body and execute through the router
                    var innerBody = new Dictionary<string, object> { ["type"] = cmdType, ["params"] = cmdParams };
                    try
                    {
                        string innerJson = MiniJsonWriter.Serialize(innerBody);
                        string result = CommandRouter.Execute(innerJson);
                        sb.Append(result);
                    }
                    catch (Exception ex)
                    {
                        sb.Append(Json.Obj("success", false, "error", ex.Message));
                    }
                }
                else
                {
                    sb.Append(Json.Obj("success", false, "error", "Invalid command entry"));
                }
            }

            sb.Append("]}");
            return sb.ToString();
        }

        // ── Helpers ────────────────────────────────────────────
        static GameObject FindTarget(Dictionary<string, object> p)
        {
            string target = p.GetStr("target");
            if (string.IsNullOrEmpty(target))
            {
                // Try by id
                int id = p.GetInt("id", 0);
                if (id != 0) return EditorUtility.InstanceIDToObject(id) as GameObject;
                return null;
            }

            // Try as instance ID
            if (int.TryParse(target, out int instanceId))
            {
                var byId = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
                if (byId != null) return byId;
            }

            return FindOne(target);
        }

        static GameObject FindOne(string nameOrPath)
        {
            if (string.IsNullOrEmpty(nameOrPath)) return null;

            // Try by path first (contains '/')
            if (nameOrPath.Contains("/"))
            {
                var found = GameObject.Find(nameOrPath);
                if (found != null) return found;
            }

            // Search by name in scene
            var roots = SceneManager.GetActiveScene().GetRootGameObjects();
            foreach (var root in roots)
            {
                var result = FindInChildren(root, nameOrPath);
                if (result != null) return result;
            }
            return null;
        }

        static GameObject FindInChildren(GameObject parent, string name)
        {
            if (parent.name == name) return parent;
            for (int i = 0; i < parent.transform.childCount; i++)
            {
                var found = FindInChildren(parent.transform.GetChild(i).gameObject, name);
                if (found != null) return found;
            }
            return null;
        }

        static string GetPath(GameObject go)
        {
            string path = go.name;
            var t = go.transform.parent;
            while (t != null)
            {
                path = t.name + "/" + path;
                t = t.parent;
            }
            return path;
        }

        static void ApplyTransform(GameObject go, Dictionary<string, object> p)
        {
            var pos = p.GetFloatArray("position");
            if (pos != null && pos.Length >= 3)
                go.transform.position = new Vector3(pos[0], pos[1], pos[2]);

            var rot = p.GetFloatArray("rotation");
            if (rot != null && rot.Length >= 3)
                go.transform.eulerAngles = new Vector3(rot[0], rot[1], rot[2]);

            var scale = p.GetFloatArray("scale");
            if (scale != null && scale.Length >= 3)
                go.transform.localScale = new Vector3(scale[0], scale[1], scale[2]);
        }

        static Type ResolveType(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            // Try direct
            var type = Type.GetType(name);
            if (type != null) return type;

            // Try UnityEngine
            type = Type.GetType($"UnityEngine.{name}, UnityEngine.CoreModule");
            if (type != null) return type;

            // Try UnityEngine broader
            type = Type.GetType($"UnityEngine.{name}, UnityEngine");
            if (type != null) return type;

            // Search all assemblies
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    type = asm.GetTypes().FirstOrDefault(t =>
                        t.Name == name || t.FullName == name);
                    if (type != null) return type;
                }
                catch { }
            }
            return null;
        }

        static string ReflectionSetProperty(Component comp, string propName, object value)
        {
            var type = comp.GetType();
            var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;

            // Try property
            var prop = type.GetProperty(propName, flags);
            if (prop != null && prop.CanWrite)
            {
                try
                {
                    object converted = ConvertValue(value, prop.PropertyType);
                    prop.SetValue(comp, converted);
                    return null;
                }
                catch (Exception ex) { return $"Failed to set property: {ex.Message}"; }
            }

            // Try field
            var field = type.GetField(propName, flags);
            if (field != null)
            {
                try
                {
                    object converted = ConvertValue(value, field.FieldType);
                    field.SetValue(comp, converted);
                    return null;
                }
                catch (Exception ex) { return $"Failed to set field: {ex.Message}"; }
            }

            // Try SerializeField (private)
            var current = type;
            while (current != null && current != typeof(object))
            {
                foreach (var f in current.GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (f.GetCustomAttribute<SerializeField>() != null &&
                        string.Equals(f.Name, propName, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            object converted = ConvertValue(value, f.FieldType);
                            f.SetValue(comp, converted);
                            return null;
                        }
                        catch (Exception ex) { return $"Failed to set serialized field: {ex.Message}"; }
                    }
                }
                current = current.BaseType;
            }

            return $"Property/field '{propName}' not found on {type.Name}";
        }

        static (object value, string error) ReflectionGetProperty(Component comp, string propName)
        {
            var type = comp.GetType();
            var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;

            var prop = type.GetProperty(propName, flags);
            if (prop != null && prop.CanRead)
            {
                var val = prop.GetValue(comp);
                return (SerializeValue(val), null);
            }

            var field = type.GetField(propName, flags);
            if (field != null)
            {
                var val = field.GetValue(comp);
                return (SerializeValue(val), null);
            }

            return (null, $"Property/field '{propName}' not found on {type.Name}");
        }

        static object ConvertValue(object value, Type targetType)
        {
            if (value == null) return null;

            // Handle $ref envelope for UnityEngine.Object references
            // {"$ref":{"kind":"asset_path|guid|instance_id","value":"..."}}
            if (value is Dictionary<string, object> refDict && refDict.ContainsKey("$ref")
                && typeof(UnityEngine.Object).IsAssignableFrom(targetType))
            {
                var inner = refDict["$ref"] as Dictionary<string, object>;
                if (inner == null) return null;
                string kind = inner.GetStr("kind");
                string refVal = inner.GetStr("value");
                if (string.IsNullOrEmpty(kind) || string.IsNullOrEmpty(refVal)) return null;

                UnityEngine.Object resolved = kind switch
                {
                    "asset_path" => AssetDatabase.LoadAssetAtPath(refVal, targetType),
                    "guid" => AssetDatabase.LoadAssetAtPath(AssetDatabase.GUIDToAssetPath(refVal), targetType),
                    "instance_id" => int.TryParse(refVal, out int id) ? EditorUtility.InstanceIDToObject(id) : null,
                    _ => null
                };

                if (resolved == null)
                    throw new Exception($"Could not resolve $ref: kind={kind}, value={refVal}");
                if (!targetType.IsAssignableFrom(resolved.GetType()))
                    throw new Exception($"$ref resolved to {resolved.GetType().Name}, expected {targetType.Name}");

                return resolved;
            }

            // Handle List<object> (from JSON arrays) → Vector3, Color, etc.
            if (value is List<object> list)
            {
                var floats = list.Select(x => Convert.ToSingle(x)).ToArray();

                if (targetType == typeof(Vector3) && floats.Length >= 3)
                    return new Vector3(floats[0], floats[1], floats[2]);
                if (targetType == typeof(Vector2) && floats.Length >= 2)
                    return new Vector2(floats[0], floats[1]);
                if (targetType == typeof(Vector4) && floats.Length >= 4)
                    return new Vector4(floats[0], floats[1], floats[2], floats[3]);
                if (targetType == typeof(Color) && floats.Length >= 3)
                {
                    float a = floats.Length >= 4 ? floats[3] : 1f;
                    // Auto-detect 0-255 vs 0-1 range
                    if (floats.Any(f => f > 1f))
                        return new Color(floats[0] / 255f, floats[1] / 255f, floats[2] / 255f, a / 255f);
                    return new Color(floats[0], floats[1], floats[2], a);
                }
                if (targetType == typeof(Quaternion) && floats.Length >= 4)
                    return new Quaternion(floats[0], floats[1], floats[2], floats[3]);
            }

            // Simple type conversions
            if (targetType == typeof(float)) return Convert.ToSingle(value);
            if (targetType == typeof(int)) return Convert.ToInt32(value);
            if (targetType == typeof(bool))
            {
                if (value is bool b) return b;
                if (value is string s) return s.ToLower() == "true" || s == "1";
                return Convert.ToBoolean(value);
            }
            if (targetType == typeof(string)) return value.ToString();
            if (targetType == typeof(double)) return Convert.ToDouble(value);
            if (targetType == typeof(long)) return Convert.ToInt64(value);

            if (targetType.IsEnum)
            {
                if (value is string enumStr)
                    return Enum.Parse(targetType, enumStr, true);
                return Enum.ToObject(targetType, Convert.ToInt32(value));
            }

            return Convert.ChangeType(value, targetType);
        }

        static object SerializeValue(object val)
        {
            if (val == null) return null;

            // UnityEngine.Object references → $ref envelope
            if (val is UnityEngine.Object uObj)
            {
                if (uObj == null) return null; // destroyed object
                string assetPath = AssetDatabase.GetAssetPath(uObj);
                var sb2 = new StringBuilder();
                sb2.Append("{\"$ref\":{\"kind\":");
                if (!string.IsNullOrEmpty(assetPath))
                {
                    sb2.Append("\"asset_path\",\"value\":\"").Append(Json.Esc(assetPath)).Append('"');
                }
                else
                {
                    sb2.Append("\"instance_id\",\"value\":\"").Append(uObj.GetInstanceID()).Append('"');
                }
                sb2.Append(",\"type\":\"").Append(uObj.GetType().Name)
                   .Append("\",\"name\":\"").Append(Json.Esc(uObj.name))
                   .Append("\"}}");
                return new RawJson(sb2.ToString());
            }

            if (val is Vector3 v3) return $"[{v3.x},{v3.y},{v3.z}]";
            if (val is Vector2 v2) return $"[{v2.x},{v2.y}]";
            if (val is Quaternion q) return $"[{q.x},{q.y},{q.z},{q.w}]";
            if (val is Color c) return $"[{c.r},{c.g},{c.b},{c.a}]";
            if (val is bool b) return b;
            if (val is int || val is float || val is double || val is long) return val;
            return val.ToString();
        }

        static void SerializeProperties(Component comp, StringBuilder sb)
        {
            var type = comp.GetType();
            var flags = BindingFlags.Public | BindingFlags.Instance;
            bool first = true;

            foreach (var prop in type.GetProperties(flags))
            {
                if (!prop.CanRead) continue;
                // Skip noisy/dangerous properties
                if (prop.Name == "mesh" || prop.Name == "material" || prop.Name == "materials"
                    || prop.Name == "sharedMesh" || prop.Name == "gameObject" || prop.Name == "transform")
                    continue;

                try
                {
                    var val = prop.GetValue(comp);
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('"').Append(EscJson(prop.Name)).Append("\":");
                    AppendJsonValue(sb, SerializeValue(val));
                }
                catch { } // Skip properties that throw
            }
        }

        static void AppendJsonValue(StringBuilder sb, object val)
        {
            if (val == null) { sb.Append("null"); return; }
            if (val is RawJson raw) { sb.Append(raw.Value); return; }
            if (val is bool b) { sb.Append(b ? "true" : "false"); return; }
            if (val is int or float or double or long)
            {
                sb.Append(Convert.ToString(val, System.Globalization.CultureInfo.InvariantCulture));
                return;
            }
            if (val is string s)
            {
                if (s.StartsWith("[") || s.StartsWith("{"))
                    sb.Append(s); // Already JSON (vectors, etc.)
                else
                    sb.Append('"').Append(EscJson(s)).Append('"');
                return;
            }
            sb.Append('"').Append(EscJson(val.ToString())).Append('"');
        }

        static void MarkSceneDirty(GameObject go)
        {
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage != null)
                EditorSceneManager.MarkSceneDirty(prefabStage.scene);
            else
                EditorSceneManager.MarkSceneDirty(go.scene);
        }

        static string EscJson(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  Minimal JSON helpers (no dependencies)
    // ─────────────────────────────────────────────────────────────
    /// <summary>Wraps a pre-formatted JSON string so Json.Obj passes it through raw.</summary>
    readonly struct RawJson
    {
        public readonly string Value;
        public RawJson(string v) { Value = v; }
        public override string ToString() => Value;
    }

    static class Json
    {
        public static string Obj(params object[] kvPairs)
        {
            var sb = new StringBuilder("{");
            for (int i = 0; i < kvPairs.Length; i += 2)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(kvPairs[i]).Append("\":");
                var val = kvPairs[i + 1];
                if (val == null) sb.Append("null");
                else if (val is RawJson raw) sb.Append(raw.Value);
                else if (val is bool b) sb.Append(b ? "true" : "false");
                else if (val is int iv) sb.Append(iv);
                else if (val is float fv) sb.Append(fv.ToString(System.Globalization.CultureInfo.InvariantCulture));
                else if (val is double dv) sb.Append(dv.ToString(System.Globalization.CultureInfo.InvariantCulture));
                else if (val is long lv) sb.Append(lv);
                else sb.Append('"').Append(Esc(val.ToString())).Append('"');
            }
            sb.Append('}');
            return sb.ToString();
        }

        public static string Esc(string s) => s?.Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t") ?? "";
    }

    // Very small JSON parser (deserialize only) — avoids Newtonsoft dependency
    static class MiniJson
    {
        public static object Deserialize(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            int idx = 0;
            return ParseValue(json, ref idx);
        }

        static object ParseValue(string json, ref int idx)
        {
            SkipWhitespace(json, ref idx);
            if (idx >= json.Length) return null;

            char c = json[idx];
            if (c == '{') return ParseObject(json, ref idx);
            if (c == '[') return ParseArray(json, ref idx);
            if (c == '"') return ParseString(json, ref idx);
            if (c == 't' || c == 'f') return ParseBool(json, ref idx);
            if (c == 'n') { idx += 4; return null; }
            return ParseNumber(json, ref idx);
        }

        static Dictionary<string, object> ParseObject(string json, ref int idx)
        {
            var dict = new Dictionary<string, object>();
            idx++; // skip {
            SkipWhitespace(json, ref idx);
            if (idx < json.Length && json[idx] == '}') { idx++; return dict; }

            while (idx < json.Length)
            {
                SkipWhitespace(json, ref idx);
                string key = ParseString(json, ref idx);
                SkipWhitespace(json, ref idx);
                idx++; // skip :
                object val = ParseValue(json, ref idx);
                dict[key] = val;
                SkipWhitespace(json, ref idx);
                if (idx < json.Length && json[idx] == ',') { idx++; continue; }
                if (idx < json.Length && json[idx] == '}') { idx++; break; }
            }
            return dict;
        }

        static List<object> ParseArray(string json, ref int idx)
        {
            var list = new List<object>();
            idx++; // skip [
            SkipWhitespace(json, ref idx);
            if (idx < json.Length && json[idx] == ']') { idx++; return list; }

            while (idx < json.Length)
            {
                list.Add(ParseValue(json, ref idx));
                SkipWhitespace(json, ref idx);
                if (idx < json.Length && json[idx] == ',') { idx++; continue; }
                if (idx < json.Length && json[idx] == ']') { idx++; break; }
            }
            return list;
        }

        static string ParseString(string json, ref int idx)
        {
            idx++; // skip opening "
            var sb = new StringBuilder();
            while (idx < json.Length)
            {
                char c = json[idx++];
                if (c == '"') break;
                if (c == '\\' && idx < json.Length)
                {
                    char next = json[idx++];
                    switch (next)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (idx + 4 <= json.Length)
                            {
                                string hex = json.Substring(idx, 4);
                                sb.Append((char)Convert.ToInt32(hex, 16));
                                idx += 4;
                            }
                            break;
                        default: sb.Append(next); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        static object ParseNumber(string json, ref int idx)
        {
            int start = idx;
            bool isFloat = false;
            if (idx < json.Length && json[idx] == '-') idx++;
            while (idx < json.Length && char.IsDigit(json[idx])) idx++;
            if (idx < json.Length && json[idx] == '.') { isFloat = true; idx++; }
            while (idx < json.Length && char.IsDigit(json[idx])) idx++;
            if (idx < json.Length && (json[idx] == 'e' || json[idx] == 'E'))
            {
                isFloat = true; idx++;
                if (idx < json.Length && (json[idx] == '+' || json[idx] == '-')) idx++;
                while (idx < json.Length && char.IsDigit(json[idx])) idx++;
            }
            string num = json.Substring(start, idx - start);
            if (isFloat) return double.Parse(num, System.Globalization.CultureInfo.InvariantCulture);
            if (long.TryParse(num, out long lv))
                return lv <= int.MaxValue && lv >= int.MinValue ? (object)(int)lv : lv;
            return double.Parse(num, System.Globalization.CultureInfo.InvariantCulture);
        }

        static bool ParseBool(string json, ref int idx)
        {
            if (json[idx] == 't') { idx += 4; return true; }
            idx += 5; return false;
        }

        static void SkipWhitespace(string json, ref int idx)
        {
            while (idx < json.Length && char.IsWhiteSpace(json[idx])) idx++;
        }
    }

    // Minimal JSON writer for batch re-serialization
    static class MiniJsonWriter
    {
        public static string Serialize(object obj)
        {
            var sb = new StringBuilder();
            Write(sb, obj);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object obj)
        {
            if (obj == null) { sb.Append("null"); return; }
            if (obj is string s) { sb.Append('"').Append(s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r")).Append('"'); return; }
            if (obj is bool b) { sb.Append(b ? "true" : "false"); return; }
            if (obj is int or long or float or double) { sb.Append(Convert.ToString(obj, System.Globalization.CultureInfo.InvariantCulture)); return; }
            if (obj is Dictionary<string, object> dict)
            {
                sb.Append('{');
                bool first = true;
                foreach (var kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('"').Append(kv.Key).Append("\":");
                    Write(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }
            if (obj is List<object> list)
            {
                sb.Append('[');
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Write(sb, list[i]);
                }
                sb.Append(']');
                return;
            }
            sb.Append('"').Append(obj.ToString()).Append('"');
        }
    }

    // Dictionary helper extensions
    static class DictExt
    {
        public static string GetStr(this Dictionary<string, object> d, string key, string def = null)
        {
            if (d.TryGetValue(key, out var v) && v != null) return v.ToString();
            return def;
        }
        public static int GetInt(this Dictionary<string, object> d, string key, int def = 0)
        {
            if (d.TryGetValue(key, out var v) && v != null)
            {
                if (v is int i) return i;
                if (v is long l) return (int)l;
                if (v is double dv) return (int)dv;
                if (int.TryParse(v.ToString(), out int parsed)) return parsed;
            }
            return def;
        }
        public static double GetDouble(this Dictionary<string, object> d, string key, double def = 0)
        {
            if (d.TryGetValue(key, out var v) && v != null)
            {
                if (v is double dv) return dv;
                if (v is float f) return f;
                if (v is int i) return i;
                if (v is long l) return l;
                if (double.TryParse(v.ToString(), out double parsed)) return parsed;
            }
            return def;
        }
        public static bool GetBool(this Dictionary<string, object> d, string key, bool def = false)
        {
            if (d.TryGetValue(key, out var v) && v != null)
            {
                if (v is bool b) return b;
                string s = v.ToString().ToLower();
                return s == "true" || s == "1";
            }
            return def;
        }
        public static float[] GetFloatArray(this Dictionary<string, object> d, string key)
        {
            if (!d.TryGetValue(key, out var v) || v == null) return null;
            if (v is List<object> list)
                return list.Select(x => Convert.ToSingle(x)).ToArray();
            return null;
        }
        public static List<object> GetList(this Dictionary<string, object> d, string key)
        {
            if (d.TryGetValue(key, out var v) && v is List<object> list) return list;
            return null;
        }
        public static Dictionary<string, object> GetDict(this Dictionary<string, object> d, string key)
        {
            if (d.TryGetValue(key, out var v) && v is Dictionary<string, object> dict) return dict;
            return null;
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  Self-describing API schema
    // ─────────────────────────────────────────────────────────────
    static class ApiSchema
    {
        public static string Get()
        {
            // Single static schema string. Designed to be the first thing an AI model reads.
            // Concise but complete — every command, param, convention, and workflow pattern.
            return @"{
  ""v"": ""1.0"",
  ""usage"": ""POST /command with body {type, params}"",
  ""conventions"": {
    ""target"": ""Name ('Player'), hierarchy path ('Env/Props/Barrel'), or instance ID (int, returned by create/find commands)"",
    ""transforms"": ""[x,y,z] arrays. position=world, rotation=euler degrees, scale=local"",
    ""type_conversion"": ""set_property auto-converts: [x,y,z]->Vector3, [x,y]->Vector2, [r,g,b,a]->Color (0-255 auto-detected), string->enum, number->int/float"",
    ""object_refs"": ""For UnityEngine.Object fields (materials, sprites, prefabs, audio, etc.) use {$ref:{kind,value}} as value in set_property. kind: asset_path | guid | instance_id. get_property returns same $ref format for object values."",
    ""undo"": ""All mutations support Ctrl+Z"",
    ""errors"": ""All responses: {success:bool}. Failures add {error:string}""
  },
  ""endpoints"": {
    ""GET /health"": ""Status, Unity version, project name"",
    ""GET /api"": ""This schema"",
    ""GET /events?since=<cursor>&limit=<n>"": ""Poll events (log, compile_start, compile_end, play_mode, scene_change). Returns {cursor, events[{id,time,type,severity?,message}]}. Start with since=0."",
    ""POST /command"": ""Execute a command""
  },
  ""commands"": {
    ""scene"": {
      ""get_hierarchy"": {""desc"": ""Scene objects as flat list"", ""params"": {""view?"": ""summary|standard|full (default summary)"", ""root?"": ""target — subtree only"", ""depth?"": ""int (default 10)"", ""filter?"": ""{name_contains?, has_component?}"", ""limit?"": ""int (default 1000)""}, ""returns"": ""objects[]: {id, n(name), a(active), p(parent_id, 0=root), d(depth), cc(child_count)}. standard adds: tag, layer, pos. full adds: rot, scl, comp[]""},
      ""get_scene_info"": {""desc"": ""Active scene name, path, dirty state, root count""},
      ""load_scene"": {""desc"": ""Open scene"", ""params"": {""path"": ""Assets/Scenes/X.unity""}},
      ""save_scene"": {""desc"": ""Save all open scenes""}
    },
    ""gameobject"": {
      ""create_gameobject"": {""desc"": ""Create empty or primitive"", ""params"": {""name"": ""string"", ""primitive_type?"": ""Cube|Sphere|Capsule|Cylinder|Plane|Quad"", ""position?"": ""[x,y,z]"", ""rotation?"": ""[x,y,z]"", ""scale?"": ""[x,y,z]"", ""parent?"": ""target"", ""components?"": ""['Rigidbody',...]""}, ""returns"": ""{id, name}""},
      ""modify_gameobject"": {""desc"": ""Move, rotate, scale, rename, reparent, toggle"", ""params"": {""target"": ""target"", ""name?"": ""string"", ""position?"": ""[x,y,z]"", ""rotation?"": ""[x,y,z]"", ""scale?"": ""[x,y,z]"", ""parent?"": ""target (''=unparent)"", ""tag?"": ""string"", ""layer?"": ""string"", ""active?"": ""bool""}},
      ""delete_gameobject"": {""desc"": ""Delete (undoable)"", ""params"": {""target"": ""target""}},
      ""duplicate_gameobject"": {""desc"": ""Clone"", ""params"": {""target"": ""target"", ""name?"": ""string"", ""position?"": ""[x,y,z]""}},
      ""find_gameobjects"": {""desc"": ""Search scene"", ""params"": {""search"": ""string"", ""method?"": ""by_name|by_tag|by_layer|by_component"", ""include_inactive?"": ""bool""}, ""returns"": ""results[]: {id, name, path}""}
    },
    ""component"": {
      ""add_component"": {""desc"": ""Add component"", ""params"": {""target"": ""target"", ""component_type"": ""e.g. Rigidbody, BoxCollider, Light, AudioSource""}},
      ""remove_component"": {""desc"": ""Remove component"", ""params"": {""target"": ""target"", ""component_type"": ""string""}},
      ""get_components"": {""desc"": ""List components"", ""params"": {""target"": ""target"", ""detailed?"": ""bool — include all property values""}},
      ""set_property"": {""desc"": ""Set any property/field via reflection"", ""params"": {""target"": ""target"", ""component_type"": ""string"", ""property"": ""string"", ""value"": ""any — see type_conversion and object_refs""}},
      ""get_property"": {""desc"": ""Read any property/field"", ""params"": {""target"": ""target"", ""component_type"": ""string"", ""property"": ""string""}}
    },
    ""editor"": {
      ""editor_state"": {""desc"": ""Play mode, compiling, platform, scene""},
      ""play"": {""desc"": ""Enter play mode""},
      ""stop"": {""desc"": ""Exit play mode""},
      ""pause"": {""desc"": ""Toggle pause""},
      ""refresh"": {""desc"": ""Refresh AssetDatabase. Blocks until compilation/import finishes. domain_reload:true if scripts triggered reload (reconnect via /health).""},
      ""read_console"": {""desc"": ""Recent log entries"", ""params"": {""count?"": ""int (default 20)""}},
      ""execute_menu_item"": {""desc"": ""Run any Unity menu command"", ""params"": {""menu_path"": ""e.g. File/Save Project""}}
    },
    ""asset"": {
      ""create_asset"": {""desc"": ""Create material or folder"", ""params"": {""path"": ""Assets/Materials/X.mat"", ""asset_type"": ""material|folder""}},
      ""find_assets"": {""desc"": ""Search AssetDatabase"", ""params"": {""filter"": ""e.g. t:Material, t:Prefab, player"", ""search_in?"": ""string (default Assets)"", ""max_results?"": ""int (default 50)""}}
    },
    ""screenshot"": {
      ""screenshot"": {""desc"": ""Capture scene view to PNG file"", ""params"": {""filename?"": ""string (default screenshot.png)"", ""path?"": ""output dir (default Assets)"", ""width?"": ""int (default 1920)"", ""height?"": ""int (default 1080)""}, ""returns"": ""{path}""},
      ""capture"": {""desc"": ""Screenshot with custom camera. Saves to _debug/. Modes: topdown, isometric, front, or manual position/rotation."", ""params"": {""mode?"": ""topdown|isometric|front (auto-positions camera)"", ""center?"": ""[x,z] world center for mode presets"", ""size?"": ""float world units to fit (default 80)"", ""position?"": ""[x,y,z] manual camera position"", ""rotation?"": ""[x,y,z] euler degrees"", ""ortho?"": ""bool (default false)"", ""ortho_size?"": ""float"", ""fov?"": ""float (default 60)"", ""width?"": ""int (default 1920)"", ""height?"": ""int (default 1080)"", ""filename?"": ""string (default capture.png)""}, ""returns"": ""{path}""},
      ""capture_sequence"": {""desc"": ""Multi-step screenshot sequence. Pause, wait, capture, resume in one atomic command. Deferred: holds HTTP connection open until done."", ""params"": {""source?"": ""game|scene|main|custom (default game). game=full Game View w/ IMGUI+UI (play mode only). scene=Scene View cam. main=Camera.main render. custom=temp cam at position."", ""width?"": ""int (default 1920)"", ""height?"": ""int (default 1080)"", ""position?"": ""[x,y,z] for source:custom"", ""rotation?"": ""[x,y,z] for source:custom"", ""fov?"": ""float (default 60)"", ""steps"": ""[{action, ...}] — actions: pause, resume, wait {seconds}, step {frames?}, capture {filename?}""}, ""returns"": ""{captures:[{filename,path}], duration_ms}""}
    },
    ""selection"": {
      ""get_selection"": {""desc"": ""Currently selected GameObjects""},
      ""set_selection"": {""desc"": ""Select and ping a GameObject"", ""params"": {""target"": ""target""}}
    },
    ""project"": {
      ""get_project_info"": {""desc"": ""Render pipeline, platform, paths, version""},
      ""get_tags"": {""desc"": ""All project tags""},
      ""get_layers"": {""desc"": ""All layers (index + name)""},
      ""add_tag"": {""desc"": ""Add tag"", ""params"": {""tag"": ""string""}},
      ""add_layer"": {""desc"": ""Add layer to first empty slot"", ""params"": {""layer"": ""string""}}
    },
    ""prefab"": {
      ""create_prefab"": {""desc"": ""Save scene object as prefab asset"", ""params"": {""target"": ""target"", ""path?"": ""Assets/Prefabs/X.prefab""}},
      ""instantiate_prefab"": {""desc"": ""Spawn prefab into scene"", ""params"": {""path"": ""prefab asset path"", ""position?"": ""[x,y,z]"", ""rotation?"": ""[x,y,z]"", ""scale?"": ""[x,y,z]""}}
    },
    ""utility"": {
      ""batch"": {""desc"": ""Multiple commands in one request"", ""params"": {""commands"": ""[{type, params}, ...]""}},
      ""execute_method"": {""desc"": ""Call any static C# method (escape hatch)"", ""params"": {""type_name"": ""fully qualified type"", ""method_name"": ""string"", ""args?"": ""[...] values, auto-converted to parameter types"", ""arg_types?"": ""['System.Int32',...] for overload disambiguation""}}
    }
  },
  ""workflows"": {
    ""script"": ""Write .cs to Assets/Scripts/ with your file tools -> refresh -> poll /events or read_console for errors -> add_component to use it"",
    ""material"": ""find_assets {filter:'t:Material Red'} -> set_property on MeshRenderer.sharedMaterial with {$ref:{kind:'asset_path',value:'Assets/Materials/Red.mat'}}"",
    ""inspect"": ""get_hierarchy (summary) -> find targets -> get_components {detailed:true} on specific objects"",
    ""prefab"": ""create_gameobject + configure -> create_prefab -> instantiate_prefab for copies"",
    ""batch"": ""Wrap multiple commands in batch for single HTTP round-trip""
  }
}";
        }
    }
}
