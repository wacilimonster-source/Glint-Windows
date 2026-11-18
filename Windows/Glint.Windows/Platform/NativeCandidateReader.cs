using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using U = Glint.Windows.Interop.UIA;

namespace Glint.Windows.Platform;

/// <summary>Native UIA3 supports modern XAML IMEs and Chromium's cross-process accessibility tree.</summary>
internal static class NativeCandidateReader
{
    [ThreadStatic] internal static string? DiagnosticStage;
    [ThreadStatic] private static long lastEvent;
    [ThreadStatic] private static List<(U.IUIAutomationElement Root, nint Foreground)>? eventContainers;
    [ThreadStatic] private static long nextSlowScanTicks;
    [ThreadStatic] private static nint acceptedForeground;
    [ThreadStatic] private static long acceptedTick;
    [ThreadStatic] private static string? acceptedIdentity;
    [ThreadStatic] private static nint acceptedFocus;
    private static readonly ThreadLocal<U.IUIAutomation> Clients = new(() =>
    {
        var client = (U.IUIAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("E22AD333-B25F-460C-83D0-0581107395C9"), true)!)!;
        if (client is U.IUIAutomation2 settings) { settings.ConnectionTimeout = 120; settings.TransactionTimeout = 120; }
        return client;
    });
    private static U.IUIAutomation Client => Clients.Value!;
    private static object Property(U.IUIAutomationElement node, int id) => node.GetCurrentPropertyValue(id);

    internal static void CaptureDiagnostics(string path, int seconds, CancellationToken cancellation = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var weTypeEvents = new WeTypeEventDiagnostics();
        var samples = new List<object>();
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed.TotalSeconds < seconds && !cancellation.IsCancellationRequested)
        {
            var windows = new List<object>();
            var visibleWindows = new List<object>();
            string stage;
            try { _ = ReadCurrent(); stage = DiagnosticStage ?? "unknown"; }
            catch (Exception e) { stage = (DiagnosticStage ?? "unknown") + ":" + e.GetType().Name + ":" + e.HResult; }
            var diagnosticHandles = new HashSet<nint>();
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                bool visible = NativeMethods.IsWindowVisible(hwnd);
                string cls = NativeMethods.WindowClass(hwnd);
                if (!visible && !IsImeClass(cls)) return true;
                NativeMethods.GetWindowThreadProcessId(hwnd, out uint windowPid);
                string name = ProcessName(windowPid);
                if (visible && visibleWindows.Count < 180)
                    visibleWindows.Add(new { pid = (int)windowPid, process = name, cls });
                bool knownHost = IsKnownImeProcess(name);
                if (visible && (knownHost || IsImeClass(cls))) diagnosticHandles.Add(hwnd);
                else if (!visible && knownHost) diagnosticHandles.Add(hwnd);
                return true;
            }, 0);
            nint foregroundWindow = NativeMethods.GetForegroundWindow();
            if (foregroundWindow != 0)
                NativeMethods.EnumChildWindows(foregroundWindow, (hwnd, _) =>
                {
                    if (NativeMethods.IsWindowVisible(hwnd) && IsImeClass(NativeMethods.WindowClass(hwnd))) diagnosticHandles.Add(hwnd);
                    return true;
                }, 0);
            foreach (nint hwnd in diagnosticHandles)
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                string name = ProcessName(pid);
                if (name.Length == 0) continue;
                string nativeClass = NativeMethods.WindowClass(hwnd);
                bool visible = NativeMethods.IsWindowVisible(hwnd);
                if (!visible)
                {
                    windows.Add(new { process = name, cls = nativeClass, visible = false });
                    continue;
                }
                var nodes = new List<object>();
                var rawChildren = new List<object>();
                try
                {
                    var root = Client.ElementFromHandle(hwnd);
                    var queue = new Queue<(U.IUIAutomationElement Node, int Depth)>();
                    queue.Enqueue((root, 0));
                    while (queue.Count > 0 && nodes.Count < 200)
                    {
                        var (node, depth) = queue.Dequeue();
                        nodes.Add(NodeMetadata(node, depth));
                        if (depth >= 8) continue;
                        var children = node.FindAll((U.TreeScope)2, Client.CreateTrueCondition());
                        for (int i = 0; i < children.Length && i < 60; i++) queue.Enqueue((children.GetElement(i), depth + 1));
                    }
                    if (nodes.Count <= 1)
                    {
                        var raw = Client.RawViewWalker.GetFirstChildElement(root);
                        for (int i = 0; raw is not null && i < 8; i++)
                        {
                            rawChildren.Add(NodeMetadata(raw, 1));
                            raw = Client.RawViewWalker.GetNextSiblingElement(raw);
                        }
                    }
                }
                catch (Exception e) { nodes.Add(new { error = e.GetType().Name, code = e.HResult }); }
                windows.Add(new { process = name, cls = nativeClass, visible = true, msaa = LegacyCandidateReader.Metadata(hwnd), nodes, rawChildren });
            }
            samples.Add(new
            {
                elapsed = (int)timer.ElapsedMilliseconds,
                stage,
                weTypeEvents = new { status = weTypeEvents.Status, recent = weTypeEvents.Snapshot() },
                events = new { received = ImeAutomationEvents.Received, kept = ImeAutomationEvents.Count, status = ImeAutomationEvents.Status, open = ImeAutomationEvents.OpenDiagnostic, live = ImeAutomationEvents.Live is not null, lastDrop = ImeAutomationEvents.LastDrop, recent = ImeAutomationEvents.RecentMetadata() },
                visibleWindows,
                windows
            });
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(samples, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            if (cancellation.WaitHandle.WaitOne(500)) break;
        }
    }

    internal static bool CheckFixture(nint hwnd)
    {
        var root = Client.ElementFromHandle(hwnd);
        var container = root.FindFirst((U.TreeScope)4, Client.CreatePropertyCondition(30011, "IME_Candidate_Window"));
        if (container is null) return false;
        var selected = container.FindFirst((U.TreeScope)4, Client.CreatePropertyCondition(30079, true));
        return selected is not null && Property(selected, 30005) as string == "你好";
    }

    /// <summary>只接受独立进程真实发出的选中事件。成功时返回空字符串。</summary>
    internal static string CheckFixtureEvent(int processId)
    {
        ImeAutomationEvents.Start();
        var timer = Stopwatch.StartNew();
        while (ImeAutomationEvents.Status == "not started" && timer.ElapsedMilliseconds < 2000) Thread.Sleep(20);
        if (ImeAutomationEvents.Status != "listening") return "listener " + ImeAutomationEvents.Status;
        long seen = 0;
        string failure = "no selection event from fixture";
        while (timer.Elapsed.TotalSeconds < 12)
        {
            foreach (var source in ImeAutomationEvents.Snapshot())
            {
                if (source.Sequence <= seen) continue;
                seen = source.Sequence;
                if (source.EventId is not (20010 or 20012)) continue;
                var element = source.ElementOnThisThread();
                if (element is null) { failure = "event element unmarshal failed"; continue; }
                try
                {
                    var container = ResolveCandidateContainer(element);
                    if (container is null) { failure = "event sender has no IME candidate container"; continue; }
                    nint handle = NativeHandle(container);
                    NativeMethods.GetWindowThreadProcessId(handle, out uint windowPid);
                    if (windowPid != (uint)processId && source.ProcessId != processId)
                    {
                        failure = "window pid " + windowPid + " element pid " + source.ProcessId + " expected " + processId;
                        continue;
                    }
                    if (IsFixtureSelection(element, container)) return "";
                    failure = "event did not identify the selected fixture candidate";
                }
                catch (COMException) { failure = "event element disconnected"; }
            }
            Thread.Sleep(40);
        }
        string recent = string.Join(",", ImeAutomationEvents.Snapshot().TakeLast(6).Select(source => source.EventId + ":" + source.ProcessId));
        return failure + " expected=" + processId + " received=" + ImeAutomationEvents.Received + " kept=" + ImeAutomationEvents.Count + " recent=" + recent + " drop=" + ImeAutomationEvents.LastDrop;
    }

    private static bool TryAcceptLiveFocus(out nint foreground, out string identity, out nint focusWindow)
    {
        foreground = NativeMethods.GetForegroundWindow();
        identity = "";
        focusWindow = 0;
        if (foreground == 0) return false;
        if (foreground == acceptedForeground && acceptedIdentity is not null && Stopwatch.GetElapsedTime(acceptedTick).TotalMilliseconds < 200)
        {
            identity = acceptedIdentity;
            focusWindow = acceptedFocus;
            return true;
        }
        uint thread = NativeMethods.GetWindowThreadProcessId(foreground, out uint pid);
        if (pid == (uint)Environment.ProcessId || !NativeMethods.IsOrdinaryProcess(pid)) return false;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            if (process.ProcessName is "WindowsTerminal" or "OpenConsole" or "conhost" or "cmd" or "powershell" or "pwsh" or "mintty") return false;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
        var focus = Client.GetFocusedElement();
        if (focus is null || Property(focus, 30019) is not false || Property(focus, 30010) is not true || Property(focus, 30022) is not false) return false;
        var gui = new NativeMethods.GUITHREADINFO { size = (uint)Marshal.SizeOf<NativeMethods.GUITHREADINFO>() };
        if (!NativeMethods.GetGUIThreadInfo(thread, ref gui) || gui.focus == 0) return false;
        if (NativeMethods.GetForegroundWindow() != foreground) return false;
        acceptedForeground = foreground;
        acceptedTick = Stopwatch.GetTimestamp();
        acceptedIdentity = identity = string.Join('.', (int[])focus.GetRuntimeId());
        acceptedFocus = focusWindow = gui.focus;
        RecentInputLine = SelectionLine(focus);
        return true;
    }

    internal static bool AcceptsImeHostFocus(bool fromImeHost, bool focusIsImeHost) => fromImeHost && focusIsImeHost;

    private static bool FocusedElementIsImeHost()
    {
        try
        {
            var focus = Client.GetFocusedElement();
            if (focus is null) return false;
            int processId = Convert.ToInt32(Property(focus, 30002));
            return processId > 0 && IsKnownImeProcess(ProcessName((uint)processId));
        }
        catch (Exception error) when (error is COMException or InvalidCastException) { return false; }
    }

    private static bool TryAcceptImeForeground(out nint foreground, out string identity, out nint focusWindow)
    {
        foreground = NativeMethods.GetForegroundWindow();
        identity = "";
        focusWindow = 0;
        if (foreground == 0) return false;
        uint thread = NativeMethods.GetWindowThreadProcessId(foreground, out uint pid);
        if (pid == (uint)Environment.ProcessId || !NativeMethods.IsOrdinaryProcess(pid)) return false;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            if (process.ProcessName is "WindowsTerminal" or "OpenConsole" or "conhost" or "cmd" or "powershell" or "pwsh" or "mintty") return false;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
        if (NativeMethods.WindowClass(foreground) is "ConsoleWindowClass" or "CASCADIA_HOSTING_WINDOW_CLASS") return false;
        var gui = new NativeMethods.GUITHREADINFO { size = (uint)Marshal.SizeOf<NativeMethods.GUITHREADINFO>() };
        if (!NativeMethods.GetGUIThreadInfo(thread, ref gui) || gui.focus == 0) return false;
        if ((NativeMethods.GetWindowLongPtr(gui.focus, NativeMethods.GwlStyle) & NativeMethods.EsPassword) != 0) return false;
        if (NativeMethods.GetForegroundWindow() != foreground) return false;
        acceptedForeground = foreground;
        acceptedTick = Stopwatch.GetTimestamp();
        acceptedIdentity = identity = "ime." + gui.focus.ToInt64();
        acceptedFocus = focusWindow = gui.focus;
        RecentInputLine = NativeMethods.TryGetForegroundCaret(out var caret) ? caret : null;
        return true;
    }

    internal static bool TryGetSafeFocus(out nint foreground, out string identity, out nint focusWindow) =>
        TryGetSafeFocus(allowImeSelection: false, out foreground, out identity, out focusWindow);

    [field: ThreadStatic] internal static Rect? RecentInputLine { get; private set; }

    internal static bool FocusedElementIsPassword()
    {
        try { return Client.GetFocusedElement() is { } focus && Property(focus, 30019) is true; }
        catch (Exception error) when (error is COMException or InvalidCastException) { return false; }
    }

    internal static bool FocusedElementAllowsWeType32(uint processId)
    {
        try
        {
            return Client.GetFocusedElement() is { } focus &&
                Property(focus, 30002) is int pid && (uint)pid == processId &&
                Property(focus, 30019) is false;
        }
        catch (Exception error) when (error is COMException or InvalidCastException) { return false; }
    }

    internal static bool TryGetSafeFocus(bool allowImeSelection, out nint foreground, out string identity, out nint focusWindow)
    {
        foreground = NativeMethods.GetForegroundWindow(); identity = ""; focusWindow = 0;
        DiagnosticStage = "foreground";
        if (foreground == 0) return false;
        uint thread = NativeMethods.GetWindowThreadProcessId(foreground, out uint pid);
        DiagnosticStage = "foreground integrity";
        if (pid == Environment.ProcessId || !NativeMethods.IsOrdinaryProcess(pid)) return false;
        using (var process = Process.GetProcessById((int)pid))
            if (new[] { "WindowsTerminal", "OpenConsole", "conhost", "cmd", "powershell", "pwsh", "mintty" }.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase)) return false;
        var focus = Client.GetFocusedElement();
        DiagnosticStage = "focus properties";
        if (focus is null || Property(focus, 30019) is not false || Property(focus, 30010) is not true || Property(focus, 30022) is not false) return false;
        int focusPid = Convert.ToInt32(Property(focus, 30002));
        DiagnosticStage = "focus integrity";
        if (!NativeMethods.IsOrdinaryProcess((uint)focusPid)) return false;
        // Chromium's accessible focus may live in a renderer process. Prove HWND ancestry
        // instead of assuming its PID equals that of the top-level browser window.
        bool belongs = false;
        var ancestor = focus;
        for (int depth = 0; ancestor is not null && depth < 40; depth++)
        {
            nint hwnd = new(Convert.ToInt64(Property(ancestor, 30020)));
            if (hwnd != 0 && (hwnd == foreground || NativeMethods.GetAncestor(hwnd, 2) == foreground)) { belongs = true; break; }
            ancestor = Client.RawViewWalker.GetParentElement(ancestor);
        }
        if (!belongs) return false;
        DiagnosticStage = "selection";
        if (!allowImeSelection && focus.GetCurrentPattern(10014) is U.IUIAutomationTextPattern text)
        {
            U.IUIAutomationTextRange? composition = null;
            if (focus.GetCurrentPattern(10032) is U.IUIAutomationTextEditPattern edit) composition = edit.GetActiveComposition();
            var ranges = text.GetSelection();
            for (int i = 0; i < ranges.Length; i++)
            {
                var range = ranges.GetElement(i);
                if (range.CompareEndpoints((U.TextPatternRangeEndpoint)0, range, (U.TextPatternRangeEndpoint)1) == 0) continue;
                if (composition is null || range.CompareEndpoints((U.TextPatternRangeEndpoint)0, composition, (U.TextPatternRangeEndpoint)0) < 0 ||
                    range.CompareEndpoints((U.TextPatternRangeEndpoint)1, composition, (U.TextPatternRangeEndpoint)1) > 0) return false;
            }
        }
        var gui = new NativeMethods.GUITHREADINFO { size = (uint)Marshal.SizeOf<NativeMethods.GUITHREADINFO>() };
        DiagnosticStage = "native focus";
        if (!NativeMethods.GetGUIThreadInfo(thread, ref gui) || gui.focus == 0) return false;
        focusWindow = gui.focus;
        identity = string.Join('.', (int[])focus.GetRuntimeId());
        RecentInputLine = SelectionLine(focus);
        DiagnosticStage = "focus accepted";
        return NativeMethods.GetForegroundWindow() == foreground;
    }

    private static Rect? SelectionLine(U.IUIAutomationElement focus)
    {
        try
        {
            if (focus.GetCurrentPattern(10014) is not U.IUIAutomationTextPattern text) return null;
            var ranges = text.GetSelection();
            if (ranges.Length == 0) return null;
            double[] values = ranges.GetElement(0).GetBoundingRectangles();
            if (values.Length < 4) return null;
            double x = values[0], y = values[1], width = values[2], height = values[3];
            if (!double.IsFinite(x) || !double.IsFinite(y) || width is < 0 or > 800 || height is < 8 or > 160) return null;
            return new Rect(x, y, width, height);
        }
        catch (Exception error) when (error is COMException or InvalidCastException) { return null; }
    }

    internal static CandidateSnapshot? ReadCurrent()
    {
        ImeAutomationEvents.Start();
        if (DoubaoCandidateBridge.FindWindow() is { } doubaoBounds)
        {
            if (WeTypeCandidateBridge.TryGetFocus(out nint doubaoForeground, out string doubaoIdentity, out nint doubaoFocus))
            {
                var doubao = DoubaoCandidateBridge.TryRead(doubaoBounds, doubaoForeground, doubaoIdentity, doubaoFocus);
                if (doubao is not null)
                {
                    DiagnosticStage = "candidate read";
                    return doubao;
                }
            }
            DiagnosticStage = "doubao candidate";
            return null;
        }
        if (WeTypeCandidateBridge.FindWindow() is { } weTypeBounds)
        {
            if (WeTypeCandidateBridge.TryGetFocus(out nint weTypeForeground, out string weTypeIdentity, out nint weTypeFocus))
            {
                WeTypeCandidateBridge.Allow(true);
                var weType = WeTypeCandidateBridge.TryRead(weTypeBounds, weTypeForeground, weTypeIdentity, weTypeFocus);
                if (weType is not null)
                {
                    DiagnosticStage = "candidate read";
                    return weType;
                }
            }
            else WeTypeCandidateBridge.Allow(false);
            DiagnosticStage = "we type candidate";
            return null;
        }
        WeTypeCandidateBridge.Allow(false);
        if (ImeAutomationEvents.Live is { } ready)
        {
            bool accepted = TryAcceptLiveFocus(out nint liveForeground, out string liveIdentity, out nint liveFocus);
            // 第一次弹出时，UIA 焦点经常停在仍被标成离屏的输入法候选项上，正文焦点检查会把这次候选丢掉。
            if (!accepted && AcceptsImeHostFocus(ready.FromImeHost, FocusedElementIsImeHost()))
                accepted = TryAcceptImeForeground(out liveForeground, out liveIdentity, out liveFocus);
            if (accepted)
            {
                DiagnosticStage = "candidate read";
                return new CandidateSnapshot(ready.Text, ready.Bounds, liveForeground, 0, "Windows 候选 / UIA 事件") { FocusIdentity = liveIdentity, FocusWindow = liveFocus, InputLine = RecentInputLine };
            }
        }
        if (Stopwatch.GetTimestamp() < nextSlowScanTicks) return null;
        nextSlowScanTicks = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 5;
        if (!TryGetSafeFocus(out nint foreground, out string identity, out nint focusWindow)) return null;
        if (ImeAutomationEvents.Live is { } live && (live.FromImeHost || live.Foreground == foreground))
        {
            DiagnosticStage = "candidate read";
            return new CandidateSnapshot(live.Text, live.Bounds, foreground, 0, "Windows 候选 / UIA 事件") { FocusIdentity = identity, FocusWindow = focusWindow, InputLine = RecentInputLine };
        }
        eventContainers ??= new();
        eventContainers.RemoveAll(x => x.Foreground != foreground);
        foreach (var source in ImeAutomationEvents.Snapshot())
        {
            if (source.Sequence <= lastEvent) continue;
            lastEvent = Math.Max(lastEvent, source.Sequence);
            if (source.Foreground != foreground) continue;
            try
            {
                var node = source.ElementOnThisThread();
                if (node is null) continue;
                var container = ResolveCandidateContainer(node);
                if (source.EventId == 20007)
                {
                    if (container is null) eventContainers.Clear();
                    else eventContainers.RemoveAll(x => SameElement(x.Root, container));
                    continue;
                }
                if (container is null || eventContainers.Any(x => SameElement(x.Root, container))) continue;
                eventContainers.Add((container, foreground));
            }
            catch (COMException) { }
        }
        while (eventContainers.Count > 4) eventContainers.RemoveAt(0);
        for (int i = eventContainers.Count - 1; i >= 0; i--)
        {
            try
            {
                var result = ReadContainer(eventContainers[i].Root, foreground, identity, focusWindow, "Windows 候选 / UIA 事件");
                if (result is not null) return result;
                if (Property(eventContainers[i].Root, 30022) is true) eventContainers.RemoveAt(i);
            }
            catch (COMException) { eventContainers.RemoveAt(i); }
        }
        var handles = new List<nint>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == Environment.ProcessId) return true;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                if (IsKnownImeProcess(process.ProcessName) || IsImeClass(NativeMethods.WindowClass(hwnd))) handles.Add(hwnd);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return true;
        }, 0);
        NativeMethods.EnumChildWindows(foreground, (hwnd, _) =>
        {
            if (NativeMethods.IsWindowVisible(hwnd) && IsImeClass(NativeMethods.WindowClass(hwnd))) handles.Add(hwnd);
            return true;
        }, 0);
        // A host may publish its IME candidate subtree inside the foreground accessibility tree.
        handles.Insert(0, foreground);
        var condition = Client.CreatePropertyCondition(30011, "IME_Candidate_Window");
        DiagnosticStage = "candidate container; host=" + NativeMethods.WindowClass(foreground);
        foreach (nint hwnd in handles.Distinct().Take(24))
        {
            var legacy = LegacyCandidateReader.Read(hwnd, foreground, identity, focusWindow);
            if (legacy is not null) return legacy;
            try
            {
                var root = Client.ElementFromHandle(hwnd);
                var container = Property(root, 30011) as string == "IME_Candidate_Window" ? root : root.FindFirst((U.TreeScope)4, condition);
                if (container is null) continue;
                var result = ReadContainer(container, foreground, identity, focusWindow, "Windows 标准候选 / UIA3");
                if (result is not null) return result;
            }
            catch (Exception e) when (e is COMException or InvalidCastException or ArgumentException) { DiagnosticStage = "native candidate error: " + e.GetType().Name + " " + e.HResult; }
        }
        return null;
    }

    private static U.IUIAutomationElement? ResolveCandidateContainer(U.IUIAutomationElement node)
    {
        foreach (var walker in new[] { Client.ControlViewWalker, Client.RawViewWalker })
        {
            var current = node;
            for (int depth = 0; current is not null && depth < 16; depth++)
            {
                if (Property(current, 30011) as string == "IME_Candidate_Window") return current;
                current = walker.GetParentElement(current);
            }
        }
        var pattern = As<U.IUIAutomationSelectionItemPattern>(Pattern(node, 10010));
        var container = pattern is null ? null : As<U.IUIAutomationElement>(pattern.CurrentSelectionContainer);
        container ??= As<U.IUIAutomationElement>(PropertyOrNull(node, 30161));
        if (container is not null && Property(container, 30011) as string == "IME_Candidate_Window") return container;
        // 事件源有时是列表内部的另一套节点，父链看不到候选窗口。回到事件节点所属窗口再找标准容器。
        nint handle = NativeHandle(node);
        var ancestor = node;
        for (int depth = 0; handle == 0 && ancestor is not null && depth < 8; depth++)
        {
            ancestor = Client.ControlViewWalker.GetParentElement(ancestor);
            if (ancestor is not null) handle = NativeHandle(ancestor);
        }
        if (handle != 0)
        {
            nint rootWindow = NativeMethods.GetAncestor(handle, 2);
            var found = FindCandidateWindow(rootWindow != 0 ? rootWindow : handle);
            if (found is not null) return found;
        }
        int processId = Convert.ToInt32(PropertyOrNull(node, 30002) ?? 0);
        return processId <= 0 ? null : FindCandidateWindowInProcess(processId);
    }

    private static U.IUIAutomationElement? FindCandidateWindow(nint hwnd)
    {
        try
        {
            var root = Client.ElementFromHandle(hwnd);
            if (Property(root, 30011) as string == "IME_Candidate_Window") return root;
            return root.FindFirst((U.TreeScope)4, Client.CreatePropertyCondition(30011, "IME_Candidate_Window"));
        }
        catch (Exception error) when (error is COMException or InvalidCastException) { return null; }
    }

    private static U.IUIAutomationElement? FindCandidateWindowInProcess(int processId)
    {
        U.IUIAutomationElement? found = null;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (found is not null || !NativeMethods.IsWindowVisible(hwnd)) return true;
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if ((int)pid != processId) return true;
            found = FindCandidateWindow(hwnd);
            return found is null;
        }, 0);
        return found;
    }

    private static nint NativeHandle(U.IUIAutomationElement node)
    {
        try { return new nint(Convert.ToInt64(PropertyOrNull(node, 30020) ?? 0L)); }
        catch (Exception error) when (error is COMException or InvalidCastException or OverflowException) { return 0; }
    }

    private static object? Pattern(U.IUIAutomationElement node, int id)
    {
        try { return node.GetCurrentPattern(id); }
        catch (COMException) { return null; }
    }

    private static object? PropertyOrNull(U.IUIAutomationElement node, int id)
    {
        try { return node.GetCurrentPropertyValue(id); }
        catch (COMException) { return null; }
    }

    private static T? As<T>(object? value) where T : class
    {
        if (value is null) return null;
        if (value is T typed) return typed;
        nint unknown = 0;
        try
        {
            unknown = Marshal.GetIUnknownForObject(value);
            return Marshal.GetTypedObjectForIUnknown(unknown, typeof(T)) as T;
        }
        catch (Exception error) when (error is COMException or InvalidCastException or ArgumentException) { return null; }
        finally { if (unknown != 0) Marshal.Release(unknown); }
    }

    private static bool IsFixtureSelection(U.IUIAutomationElement sender, U.IUIAutomationElement container)
    {
        if (Property(sender, 30079) is true && Property(sender, 30005) as string == "你好") return true;
        var selected = container.FindFirst((U.TreeScope)4, Client.CreatePropertyCondition(30079, true));
        return selected is not null && Property(selected, 30005) as string == "你好";
    }

    private static bool SameElement(U.IUIAutomationElement left, U.IUIAutomationElement right)
    {
        try { return Client.CompareElements(left, right) != 0; }
        catch (COMException)
        {
            int[] first = RuntimeId(left), second = RuntimeId(right);
            return first.Length > 0 && second.Length > 0 && Client.CompareRuntimeIds(first, second) != 0;
        }
    }

    private static int[] RuntimeId(U.IUIAutomationElement node)
    {
        try { return node.GetRuntimeId() ?? []; }
        catch (COMException) { return []; }
    }

    private static object NodeMetadata(U.IUIAutomationElement node, int depth)
    {
        string id = Sanitize(Property(node, 30011) as string);
        string cls = Sanitize(Property(node, 30012) as string);
        string framework = Sanitize(Property(node, 30024) as string);
        return new { depth, id, type = Property(node, 30003), cls, framework, offscreen = Property(node, 30022) is true, selected = Property(node, 30079) is true };
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Any(character => character > 127)) return "(non-ascii)";
        return value.Length <= 64 ? value : value[..64];
    }

    private static string ProcessName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return ""; }
    }

    private static bool IsKnownImeProcess(string name) =>
        name is "TextInputHost" or "InputApp" or "ChsIME" or "ChsIMEHost" or "ChtIME" or "wetype_renderer";

    private static bool IsImeClass(string className) =>
        className.Contains("IME", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("Candidate", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("WeType", StringComparison.Ordinal);

    private static CandidateSnapshot? ReadContainer(U.IUIAutomationElement container, nint foreground, string identity, nint focusWindow, string source)
    {
        if (Property(container, 30022) is true) return null;
        DiagnosticStage = "selected candidate";
        var selected = container.FindFirst((U.TreeScope)4, Client.CreatePropertyCondition(30079, true));
        if (selected is null || Property(selected, 30022) is true) return null;
        string value = (Property(selected, 30005) as string ?? "").Trim();
        if (value.Length is 0 or > 120 || value.Any(char.IsControl) || !value.Any(c => c is >= '\u3400' and <= '\u9fff')) return null;
        var rectangle = (double[])Property(container, 30001);
        if (rectangle.Length != 4 || !rectangle.All(double.IsFinite) || rectangle[2] is <= 0 or > 5000 || rectangle[3] is <= 0 or > 1200) return null;
        var bounds = new Rect(rectangle[0], rectangle[1], rectangle[2], rectangle[3]);
        try
        {
            var parent = Client.ControlViewWalker.GetParentElement(selected);
            for (int depth = 0; parent is not null && depth < 6; depth++)
            {
                if (Property(parent, 30003) is int type && type == 50008 && Property(parent, 30001) is double[] list &&
                    list.Length == 4 && list.All(double.IsFinite) && list[2] > 0 && list[3] > 0)
                {
                    bounds = HudPlacement.AlignToList(bounds, new Rect(list[0], list[1], list[2], list[3]));
                    break;
                }
                if (SameElement(parent, container)) break;
                parent = Client.ControlViewWalker.GetParentElement(parent);
            }
        }
        catch (COMException) { }
        if (NativeMethods.GetForegroundWindow() != foreground) return null;
        DiagnosticStage = "candidate read";
        return new CandidateSnapshot(value, bounds, foreground, 0, source) { FocusIdentity = identity, FocusWindow = focusWindow, InputLine = RecentInputLine };
    }
}
