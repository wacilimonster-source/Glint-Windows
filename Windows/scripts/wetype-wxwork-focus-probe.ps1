param(
    [ValidateRange(1, 600)][int]$Seconds = 90,
    [string]$WxWorkExe = "${env:ProgramFiles(x86)}\WXWork\WXWork.exe"
)

$ErrorActionPreference = 'Stop'
$WxWorkExe = (Resolve-Path -LiteralPath $WxWorkExe).ProviderPath
if (-not ('WxWorkFocusProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

public static class WxWorkFocusProbe
{
    public sealed class Window
    {
        public string Hwnd;
        public uint Pid;
        public uint Tid;
        public string Machine;
        public int? Bits;

        internal Window(IntPtr hwnd)
        {
            Hwnd = Hex(hwnd);
            Tid = GetWindowThreadProcessId(hwnd, out Pid);
            if (Pid == 0) return;
            using (var process = OpenProcess(0x1000, false, Pid))
            {
                ushort machine, native;
                if (process.IsInvalid || !IsWow64Process2(process, out machine, out native)) return;
                ushort effective = machine == 0 ? native : machine;
                Machine = "0x" + effective.ToString("X4");
                if (effective == 0x014c) Bits = 32;
                if (effective == 0x8664 || effective == 0xaa64) Bits = 64;
            }
        }
    }

    public sealed class Sample
    {
        public Window Foreground;
        public Window Focus;
        public string FocusRoot;
        public bool RootEqualsForeground;
        public bool CandidateVisible;
        public uint[] CandidatePids;
    }

    public static Sample Capture()
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return null;
        var before = new Window(foreground);
        if (before.Tid == 0) return null;
        IntPtr focus = FocusFor(before.Tid);
        var focused = new Window(focus);
        IntPtr root = GetAncestor(focus, 2);

        var candidates = new SortedSet<uint>();
        var buffer = new StringBuilder(128);
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            string cls = ClassName(hwnd, buffer);
            if (cls == "WeType.NativeCandidate.Direct2D.v2") AddOwner(hwnd, candidates);
            if (cls == "WeType.NativeCandidate.Decoration.v2")
                EnumChildWindows(hwnd, (child, unused) =>
                {
                    if (IsWindowVisible(child) && ClassName(child, buffer) == "WeType.NativeCandidate.Direct2D.v2")
                        AddOwner(child, candidates);
                    return true;
                }, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);

        if (GetForegroundWindow() != foreground || FocusFor(before.Tid) != focus) return null;
        var pids = new uint[candidates.Count];
        candidates.CopyTo(pids);
        return new Sample
        {
            Foreground = before, Focus = focused, FocusRoot = Hex(root),
            RootEqualsForeground = root == foreground,
            CandidateVisible = pids.Length != 0, CandidatePids = pids
        };
    }

    private static string Hex(IntPtr hwnd) { return "0x" + hwnd.ToInt64().ToString("X"); }
    private static string ClassName(IntPtr hwnd, StringBuilder buffer)
    {
        return GetClassName(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : "";
    }

    private static void AddOwner(IntPtr hwnd, SortedSet<uint> owners)
    {
        uint pid;
        if (GetWindowThreadProcessId(hwnd, out pid) != 0) owners.Add(pid);
    }

    private static IntPtr FocusFor(uint thread)
    {
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf(typeof(GuiThreadInfo)) };
        return GetGUIThreadInfo(thread, ref info) ? info.Focus : IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }

    private delegate bool EnumWindow(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hwnd, EnumWindow callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int size);
    [DllImport("kernel32.dll")] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool IsWow64Process2(SafeProcessHandle process, out ushort machine, out ushort native);
}
'@
}

function Find-WxWorkRoot([uint32]$ProcessId) {
    while ($ProcessId -ne 0 -and $processes.ContainsKey($ProcessId)) {
        if ($roots.Contains($ProcessId)) { return $ProcessId }
        $child = $processes[$ProcessId]
        $ProcessId = $child.ParentProcessId
        # A live parent must predate the child. A newer process reused the parent PID.
        if (-not $processes.ContainsKey($ProcessId) -or
            $processes[$ProcessId].CreationDate -ge $child.CreationDate) { break }
    }
    return 0
}

$clock = [Diagnostics.Stopwatch]::StartNew()
$samples = 0
$visibleSamples = 0
$previous = ''
$refreshAt = 0
do {
    if ($clock.ElapsedMilliseconds -ge $refreshAt) {
        $processes = @{}
        $roots = [Collections.Generic.HashSet[uint32]]::new()
        foreach ($root in (Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($WxWorkExe)) -ErrorAction SilentlyContinue)) {
            try {
                if ($root.Path -eq $WxWorkExe) { [void]$roots.Add($root.Id) }
            }
            finally { $root.Dispose() }
        }
        if ($roots.Count -gt 0) {
            foreach ($process in (Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, CreationDate)) {
                $processes[$process.ProcessId] = $process
            }
        }
        $refreshAt = $clock.ElapsedMilliseconds + 1000
    }
    $sample = if ($roots.Count -gt 0) { [WxWorkFocusProbe]::Capture() }
    if ($null -ne $sample -and (Find-WxWorkRoot $sample.Foreground.Pid) -ne 0) {
        $samples++
        if ($sample.CandidateVisible) { $visibleSamples++ }
        $focusRootPid = Find-WxWorkRoot $sample.Focus.Pid
        $line = [ordered]@{
            foreground = $sample.Foreground
            focus = $sample.Focus
            focusInWxWorkTree = $focusRootPid -ne 0
            focusWxWorkRootPid = $focusRootPid
            focusRoot = $sample.FocusRoot
            rootEqualsForeground = $sample.RootEqualsForeground
            candidateVisible = $sample.CandidateVisible
            candidatePids = $sample.CandidatePids
        } | ConvertTo-Json -Compress
        if ($line -ne $previous) { $line }
        $previous = $line
    } else {
        $previous = ''
    }
    Start-Sleep -Milliseconds 150
} while ($clock.Elapsed.TotalSeconds -lt $Seconds)

[ordered]@{ samples = $samples; visibleSamples = $visibleSamples } | ConvertTo-Json -Compress
