using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Input;
using Glint.Windows.Services;

namespace Glint.Windows;

internal static class SelfCheck
{
    public static async Task<int> RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        MainWindow? window = null;
        AppController? controller = null;
        try
        {
            string preferences = Path.Combine(directory, "test-settings.json");
            string legacyPreferences = Path.Combine(directory, "legacy-settings.json");
            File.WriteAllText(legacyPreferences, "{\"TextColor\":\"#F2F2F7\"}");
            var legacy = AppSettings.Load(legacyPreferences);
            Require(legacy.TextColor == "Auto", "legacy default text color follows theme");
            legacy.TextColor = "#F2F2F7";
            Require(AppSettings.Load(legacyPreferences).TextColor == "#F2F2F7", "later explicit text color is retained");
            var settings = AppSettings.Load(preferences);
            Require(new AppSettings().MouseInsert, "mouse insertion defaults to enabled");
            settings.MouseInsert = false;
            Require(!AppSettings.Load(preferences).MouseInsert, "mouse insertion preference persists");
            settings.MouseInsert = true;
            settings.MaxTranslations = 200; Require(settings.MaxTranslations == 9, "translation count clamp");
            settings.DwellMs = 0; Require(settings.DwellMs == 50, "dwell clamp");
            settings.DwellMs = 80; Require(settings.DwellMs == 75, "dwell snaps to 75");
            settings.DwellMs = 200; Require(settings.DwellMs == 100, "dwell snaps to 100");
            settings.HudFontSize = double.NaN; Require(settings.HudFontSize == 14, "finite font size");
            settings.HudOpacity = 2; Require(settings.HudOpacity == 1, "opacity clamp");
            settings.Theme = "invalid"; Require(settings.Theme == "System", "theme validation");
            settings.SolidColor = "broken"; Require(settings.SolidColor == "#1C1C1F", "color validation");
            settings.MaxTranslations = 3; settings.DwellMs = 100; settings.HudOpacity = .8;
            settings.InsertModifiers = "Ctrl+Shift"; Require(settings.InsertModifiers == "Ctrl+Shift", "insert shortcut accepts modifiers");
            settings.InsertModifiers = ""; Require(settings.InsertModifiers == "", "insert shortcut can be cleared");
            settings.InsertModifiers = "nope"; Require(settings.InsertModifiers == "Ctrl+Alt", "invalid insert shortcut restores default");
            settings.SpeechShortcut = "Alt+F6"; Require(settings.SpeechShortcut == "Alt+F6", "speech shortcut accepts a key");
            settings.SpeechShortcut = ""; Require(settings.SpeechShortcut == "", "speech shortcut can be cleared");
            settings.TargetLanguage = "ja"; Require(settings.TargetLanguage == "ja" && settings.TargetLanguageName == "日语", "japanese target");
            settings.TargetLanguage = "nope"; Require(settings.TargetLanguage == "en", "invalid target restores english");
            settings.TargetLanguage = "ko";
            settings.HideTaskbarIcon = true;
            settings.Save();
            var restored = AppSettings.Load(preferences);
            Require(restored.MaxTranslations == 3 && restored.DwellMs == 100 && restored.SpeechShortcut == "" && restored.InsertModifiers == "Ctrl+Alt" && restored.TargetLanguage == "ko", "preferences round trip");
            Require(restored.HideTaskbarIcon && !new AppSettings().HideTaskbarIcon, "taskbar preference persists and defaults to visible");
            Require(!File.ReadAllText(preferences).Contains("你好"), "no candidate persistence");
            checks.Add(new { check = "preferences", passed = true });
            Require(Platform.ImeAutomationEvents.ChooseCandidatePhrase("成", null, null, null, ["成", "功"]) == "成功", "split candidate joins to the highlighted phrase");
            Require(Platform.ImeAutomationEvents.ChooseCandidatePhrase("成", null, null, "成功", ["成"]) == "成功", "item text restores a phrase when the name is only the first character");
            Require(Platform.ImeAutomationEvents.ChooseCandidatePhrase("成功", null, null, "成功呈貢程工成工橙弓", ["成", "功"]) == "成功", "item text does not swallow the whole candidate bar");
            Require(Platform.ImeAutomationEvents.ChooseCandidatePhrase("1成功", null, null, null, []) == "成功", "index prefix is removed from the highlighted phrase");
            Require(Platform.ImeAutomationEvents.ChooseCandidatePhrase("从", null, null, null, ["从"]) == "从", "single highlighted character stays intact");
            Require(Platform.ImeAutomationEvents.ShouldReadOpenMenu(20003, true), "opening a candidate menu starts highlight discovery even before it is visible");
            Require(!Platform.ImeAutomationEvents.ShouldReadOpenMenu(20003, false), "ordinary menus do not start candidate discovery");
            Require(!Platform.ImeAutomationEvents.ShouldReadOpenMenu(20012, true), "selection events do not take the menu-open path");
            Require(Platform.NativeCandidateReader.AcceptsImeHostFocus(true, true), "ime host focus can keep the first candidate");
            Require(!Platform.NativeCandidateReader.AcceptsImeHostFocus(true, false), "a normal failed focus check stays failed");
            Require(!Platform.NativeCandidateReader.AcceptsImeHostFocus(false, true), "non-ime live text does not use the ime focus fallback");
            Platform.HudPlacement.Check();
            Platform.DoubaoCandidateBridge.Check();
            Platform.WeTypeSnapshot.Check();
            Platform.WeTypeCandidateBridge.CheckModule();
            checks.Add(new { check = "WeType bridge modules and highlighted candidate snapshot", passed = true });
            using (var fixture = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { ArgumentList = { "--ime-fixture" }, UseShellExecute = false, CreateNoWindow = true }))
            {
                var eventCheck = Task.Run(() => Platform.NativeCandidateReader.CheckFixtureEvent(fixture!.Id));
                bool candidateRead = false;
                string? failure = null;
                for (int attempt = 0; attempt < 30 && !candidateRead; attempt++)
                {
                    await Task.Delay(200);
                    try { fixture!.Refresh(); nint handle = fixture.MainWindowHandle; candidateRead = handle != 0 && await Task.Run(() => Platform.NativeCandidateReader.CheckFixture(handle)); }
                    catch (Exception error) { failure = error.ToString(); }
                }
                string eventFailure = await eventCheck;
                if (fixture is not null && !fixture.HasExited) { fixture.Kill(); await fixture.WaitForExitAsync(); }
                Require(candidateRead, "native UIA3 cross-process candidate protocol: " + failure);
                Require(eventFailure.Length == 0, "UIA selection event from fixture: " + eventFailure);
                checks.Add(new { check = "native UIA3 cross-process candidate protocol", passed = true });
                checks.Add(new { check = "UIA selection event from independent fixture", passed = true });
            }

            controller = new AppController(true, restored);
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134) && !SystemParameters.HighContrast)
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key?.GetValue("EnableTransparency") is not int enabled || enabled != 0)
                {
                    restored.BackgroundStyle = "Frosted";
                    restored.TextColor = "Auto";
                    var hud = new HudWindow(restored);
                    int selectedIndex = -1;
                    hud.TranslationSelected += index => selectedIndex = index;
                    hud.Present([new TranslationResult("hello"), new TranslationResult("world")], new Rect(200, 200, 100, 30), allowSelection: true);
                    var selectionRow = (StackPanel)((Border)hud.Content).Child;
                    var selectionItem = (StackPanel)selectionRow.Children[1];
                    selectionItem.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                    Require(selectedIndex == 1 && hud.IsHitTestVisible, "mouse selects the intended translation");
                    hud.MarkStale();
                    Require(!hud.IsHitTestVisible, "stale translation is not clickable");
                    restored.MouseInsert = false;
                    hud.Present([new TranslationResult("hello")], new Rect(200, 200, 100, 30), allowSelection: true);
                    Require(!hud.IsHitTestVisible, "disabled mouse insertion passes through");
                    restored.MouseInsert = true;
                    var acceptedThemes = new List<string>();
                    foreach (string theme in new[] { "Light", "Dark" })
                    {
                        restored.Theme = theme;
                        hud.Present([new TranslationResult("hello")], new Rect(200, 200, 100, 30), new Rect(200, 170, 100, 30));
                        if (hud.HasCompositionBackdrop) acceptedThemes.Add(theme);
                        var surface = (System.Windows.Controls.Border)hud.Content;
                        var row = (System.Windows.Controls.StackPanel)surface.Child;
                        var entry = (System.Windows.Controls.StackPanel)row.Children[0];
                        var text = (System.Windows.Controls.TextBlock)entry.Children[1];
                        Require(((SolidColorBrush)text.Foreground).Color == ((SolidColorBrush)Application.Current.FindResource("TextBrush")).Color, $"{theme} HUD text follows theme");
                    }
                    restored.BackgroundStyle = "Solid";
                    hud.Present([new TranslationResult("hello")], new Rect(200, 200, 100, 30), new Rect(200, 170, 100, 30));
                    hud.MarkStale();
                    Require(hud.IsVisible && hud.Opacity == 1, "candidate changes keep the HUD fully visible");
                    hud.Present([new TranslationResult("world")], new Rect(200, 200, 100, 30), new Rect(200, 170, 100, 30));
                    Require(hud.IsVisible && hud.Opacity == 1, "new translation replaces the previous one without dimming");
                    Require(!hud.HasCompositionBackdrop, "solid HUD does not use composition backdrop");
                    Require(((SolidColorBrush)((System.Windows.Controls.Border)hud.Content).Background).Color == ((SolidColorBrush)Application.Current.FindResource("HudGlassBrush")).Color, "solid HUD uses fixed themed surface");
                    hud.Close();
                    checks.Add(new { check = "light/dark HUD rendering and text colors", passed = true, acceptedThemes, compositionError = hud.CompositionError });
                }
            }
            Require(controller.Translations.IsAvailable, "bundled dictionary readiness");
            var hello = await controller.Translations.TranslateAsync("你好");
            Require(hello.Any(x => x.Text.Contains("hello", StringComparison.OrdinalIgnoreCase)), "你好 → hello");
            var computer = await controller.Translations.TranslateAsync("电脑");
            Require(computer.Any(x => x.Text.Contains("computer", StringComparison.OrdinalIgnoreCase)), "电脑 → computer");
            Require((await controller.Translations.TranslateAsync("hello")).Count == 0, "non-Han rejection");
            JapaneseDisplay.Check();
            var japanese = await controller.Translations.TranslateAsync("你好", "ja");
            Require(japanese.All(x => JapaneseDisplay.IsUsable(x.Text)), "japanese does not fall back to english");
            Require((await controller.Translations.TranslateAsync("你好", "fr")).Count == 0, "unsupported direction rejection");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            bool cancelled = false;
            try { await controller.Translations.TranslateAsync("你好", ct: cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
            Require(cancelled, "cancelled request rejection");
            checks.Add(new { check = "offline dictionaries and cancellation", passed = true });
            await TranslationSmokeTests.RunAsync(controller.Translations);
            checks.Add(new { check = "upstream dictionary benchmark, parts of speech and optional local model", passed = true });
            checks.Add(new { check = "dictionary benchmark results", report = TranslationSmokeTests.LastReport });

            window = new MainWindow(controller);
            Require(window.FontFamily.Equals(Application.Current.FindResource("InterfaceFont")), "main window uses interface font");
            Application.Current.MainWindow = window;
            window.Show();
            Require(!window.ShowInTaskbar && window.IsVisible, "hidden taskbar icon keeps settings window visible");
            var windowHandle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            restored.HideTaskbarIcon = false;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(window.ShowInTaskbar && window.IsVisible, "taskbar icon can be restored without closing the window");
            restored.HideTaskbarIcon = true;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(!window.ShowInTaskbar && window.IsVisible, "taskbar icon can be hidden without closing the window");
            Require(new System.Windows.Interop.WindowInteropHelper(window).Handle == windowHandle, "taskbar visibility keeps the hotkey window handle");
            window.Hide();
            window.Show();
            Require(window.IsVisible && !window.ShowInTaskbar, "hidden taskbar window can be reopened");
            restored.HideTaskbarIcon = false;
            foreach (string theme in new[] { "Light", "Dark" })
            {
                restored.Theme = theme;
                window.Width = 1040; window.Height = 760;
                foreach (string page in new[] { "overview", "general", "language", "display", "input", "models", "privacy", "about" })
                {
                    window.NavigateTo(page);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    await Task.Delay(200);
                    SaveRender(window, Path.Combine(directory, $"{page}-{theme.ToLowerInvariant()}.png"));
                }
                window.NavigateTo("overview");
                window.Width = window.MinWidth; window.Height = window.MinHeight;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                SaveRender(window, Path.Combine(directory, $"minimum-{theme.ToLowerInvariant()}.png"));
            }
            checks.Add(new { check = "WPF light/dark and minimum window render", passed = true });
            if (SystemParameters.ClientAreaAnimation)
            {
                restored.ReduceMotion = false;
                window.NavigateTo("display");
                await Task.Delay(250);
                var scroll = (ScrollViewer)window.FindName("PageScroll");
                Require(scroll.ScrollableHeight > 0, "display page can scroll at minimum window size");
                scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent, Source = scroll });
                await Task.Delay(500);
                double offset = scroll.VerticalOffset;
                Require(offset > 0, "wheel reaches a positive offset");
                await Task.Delay(100);
                Require(Math.Abs(scroll.VerticalOffset - offset) < 0.5, "scroll offset stays stable after animation");
                double previousOffset = scroll.VerticalOffset;
                for (int i = 0; i < 8; i++)
                {
                    scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent, Source = scroll });
                    await Task.Delay(20);
                    Require(scroll.VerticalOffset + 0.5 >= previousOffset, "continuous wheel input does not move backwards");
                    previousOffset = scroll.VerticalOffset;
                }
                Require(scroll.VerticalOffset > offset + 30, "continuous wheel input moves while events arrive");
                double beforeReverse = scroll.VerticalOffset;
                scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = UIElement.PreviewMouseWheelEvent, Source = scroll });
                await Task.Delay(100);
                Require(scroll.VerticalOffset < beforeReverse, "reversing the wheel responds without waiting for the old target");
                window.NavigateTo("general");
                window.NavigateTo("display");
                window.NavigateTo("about");
                await Task.Delay(250);
                var content = (ContentControl)window.FindName("PageContent");
                Require(content.Opacity > 0.99 && Math.Abs(scroll.VerticalOffset) < 0.5, "rapid navigation settles at the top with visible content");
                checks.Add(new { check = "smooth wheel and rapid navigation in WPF window", passed = true });
            }
            File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(new { passed = true, checks }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(new { passed = false, checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
        finally
        {
            window?.Close();
            controller?.Dispose();
        }
    }

    internal static void SaveRender(Window window, string path)
    {
        window.UpdateLayout();
        var root = (FrameworkElement)window.Content;
        double width = root.ActualWidth, height = root.ActualHeight;
        Require(width > 0 && height > 0, "window has content size");
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException("Check failed: " + message); }
}
