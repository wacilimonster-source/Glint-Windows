using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Glint.Windows.Services;
using Forms = System.Windows.Forms;

namespace Glint.Windows;

public partial class App : Application
{
    private Mutex? _instance;
    private EventWaitHandle? _activation;
    private EventWaitHandle? _exitSignal;
    private AppController? _controller;
    private Forms.NotifyIcon? _tray;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length > 1 && e.Args[0] == "--diagnose-ime")
        {
            int duration = e.Args.Length > 2 && int.TryParse(e.Args[2], out int requested) ? Math.Clamp(requested, 5, 180) : 40;
            await Task.Run(() => Platform.NativeCandidateReader.CaptureDiagnostics(e.Args[1], duration));
            Shutdown(); return;
        }
        if (e.Args.Contains("--ime-fixture"))
        {
            // WinForms 列表走系统 UIA 提供方，能向其他进程发出真正的选中事件。WPF 的 AutomationPeer 事件到不了这个监听。
            var list = new Forms.ListBox { Name = "IME_Candidate_Window", Width = 260, Height = 90 };
            list.Items.AddRange(new object[] { "测试", "你好" });
            list.SelectedIndex = 0;
            var form = new Forms.Form
            {
                Text = "Glint 候选协议自测",
                Width = 320,
                Height = 220,
                StartPosition = Forms.FormStartPosition.CenterScreen
            };
            form.Controls.Add(list);
            form.Shown += async (_, _) =>
            {
                await Task.Delay(800);
                list.SelectedIndex = 1;
            };
            form.Show();
            await Task.Delay(12000);
            form.Close();
            Shutdown();
            return;
        }
        if (e.Args.Contains("--quit"))
        {
            if (EventWaitHandle.TryOpenExisting(@"Local\Glint.Windows.Exit", out var signal))
            { using (signal) signal.Set(); }
            Shutdown(); return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--self-test")
        {
            int exit = await SelfCheck.RunAsync(e.Args.Length > 1 ? e.Args[1] : Path.Combine(AppContext.BaseDirectory, "self-test"));
            Shutdown(exit);
            return;
        }
        _instance = new Mutex(true, @"Local\Glint.Windows.SingleInstance", out bool firstInstance);
        _activation = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Glint.Windows.Activate");
        if (!firstInstance) { _activation.Set(); Shutdown(); return; }
        _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Glint.Windows.Exit");
        _controller = new AppController();
        var main = new MainWindow(_controller);
        MainWindow = main;
        main.Closing += MainClosing;
        main.Show();
        if (e.Args.Contains("--input-methods")) main.NavigateTo("input");
        _controller.Start(main);
        if (e.Args.Contains("--background")) main.Hide();
        CreateTray();
        int readyIndex = Array.IndexOf(e.Args, "--ready-file");
        if (readyIndex >= 0 && readyIndex + 1 < e.Args.Length)
        {
            string path = Path.GetFullPath(e.Args[readyIndex + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { status = _controller.IsMonitoring ? "ready" : "unavailable", processId = Environment.ProcessId, version = typeof(App).Assembly.GetName().Version?.ToString(3) }));
        }
        var activationThread = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    int index = WaitHandle.WaitAny([_activation, _exitSignal]);
                    if (_exiting) return;
                    if (index == 1) { Dispatcher.InvokeAsync(() => Shutdown()); return; }
                    Dispatcher.InvokeAsync(ShowMainWindow);
                }
            }
            catch (ObjectDisposedException) { }
        }) { IsBackground = true, Name = "Glint instance activation" };
        activationThread.Start();
    }

    private void CreateTray()
    {
        if (_controller is null) return;
        using var stream = GetResourceStream(new Uri("pack://application:,,,/Assets/glint-logo.png")).Stream;
        using var image = new Bitmap(stream);
        nint rawIcon = image.GetHicon();
        using var originalIcon = Icon.FromHandle(rawIcon);
        var icon = (Icon)originalIcon.Clone();
        DestroyIcon(rawIcon);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开 Glint", null, (_, _) => ShowMainWindow());
        var pause = new Forms.ToolStripMenuItem("暂停翻译");
        pause.Click += (_, _) => _controller.IsPaused = !_controller.IsPaused;
        menu.Items.Add(pause);
        menu.Items.Add("预览翻译栏", null, (_, _) => _controller.PreviewHud());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出 Glint", null, (_, _) => Shutdown());
        menu.Opening += (_, _) => { pause.Text = _controller.IsPaused ? "继续翻译" : "暂停翻译"; pause.Checked = _controller.IsPaused; };
        _tray = new Forms.NotifyIcon { Icon = icon, Text = "Glint · 输入中文，遇见英文", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => ShowMainWindow();
    }
    private void ShowMainWindow()
    {
        if (MainWindow is not { } main) return;
        main.Show();
        if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
        main.Activate();
    }
    private void MainClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        e.Cancel = true;
        MainWindow.Hide();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        Platform.ImeAutomationEvents.Shutdown();
        _activation?.Set();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Icon?.Dispose();
            _tray.ContextMenuStrip?.Dispose();
            _tray.Dispose();
        }
        _controller?.Dispose();
        Platform.WeTypeCandidateBridge.Shutdown();
        _activation?.Dispose();
        _exitSignal?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint handle);
}
