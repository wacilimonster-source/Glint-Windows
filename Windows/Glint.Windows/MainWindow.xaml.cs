using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using Glint.Windows.Services;

namespace Glint.Windows;

public partial class MainWindow : Window
{
    private readonly AppController controller;
    private readonly Dictionary<string, FrameworkElement> pages = new();
    private SmoothPageScroll? smoothScroll;
    private int previousNavigationIndex = -1;
    private static readonly NavigationItem[] NavigationItems =
    [
        new("overview", "概览", "\uE80F"),
        new("general", "通用配置", "\uE713"),
        new("language", "语言与翻译", "\uE8C1"),
        new("display", "显示与触发", "\uE7F4"),
        new("input", "输入法支持", "\uE765"),
        new("models", "插件与模型", "\uE7B8"),
        new("privacy", "权限与隐私", "\uE72E"),
        new("about", "关于", "\uE946"),
    ];

    public MainWindow(AppController controller)
    {
        this.controller = controller;
        InitializeComponent();
        DataContext = controller;
        SetBinding(ShowInTaskbarProperty, new Binding("Settings.HideTaskbarIcon") { Converter = new InverseBooleanConverter() });
        smoothScroll = new SmoothPageScroll(PageScroll, ScrollFeedback, () => SystemParameters.ClientAreaAnimation && !controller.Settings.ReduceMotion);
        Navigation.ItemsSource = NavigationItems;
        Navigation.SelectedIndex = 0;
        StateChanged += (_, _) => MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    public void NavigateTo(string id)
    {
        var item = NavigationItems.FirstOrDefault(x => x.Id == id);
        if (item != null) Navigation.SelectedItem = item;
    }

    private void Navigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Navigation.SelectedItem is not NavigationItem item || PageContent == null) return;
        PageTitle.Text = item.Title;
        if (!pages.TryGetValue(item.Id, out var page))
        {
            page = item.Id switch
            {
                "general" => BuildGeneral(),
                "language" => BuildLanguage(),
                "display" => BuildDisplay(),
                "input" => BuildInputMethods(),
                "models" => BuildModels(),
                "privacy" => BuildPrivacy(),
                "about" => BuildAbout(),
                _ => BuildOverview(),
            };
            pages[item.Id] = page;
        }
        int nextIndex = Navigation.SelectedIndex;
        bool animate = previousNavigationIndex >= 0 && SystemParameters.ClientAreaAnimation && !controller.Settings.ReduceMotion;
        double direction = nextIndex >= previousNavigationIndex ? 1 : -1;
        previousNavigationIndex = nextIndex;
        PageContent.BeginAnimation(OpacityProperty, null);
        PageTransition.BeginAnimation(TranslateTransform.YProperty, null);
        smoothScroll?.Reset();
        PageContent.Content = page;
        PageScroll.ScrollToTop();
        if (animate)
        {
            var duration = TimeSpan.FromMilliseconds(170);
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            PageContent.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = easing });
            PageTransition.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8 * direction, 0, duration) { EasingFunction = easing });
        }
    }

    private FrameworkElement BuildOverview()
    {
        var content = Page();
        var status = new Grid();
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var symbol = IconTile("\uE8C1");
        status.Children.Add(symbol);
        var statusText = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 12, 0) };
        var title = Text("", 16, FontWeights.SemiBold);
        title.SetBinding(TextBlock.TextProperty, new Binding("IsMonitoring") { Converter = new BooleanLabelConverter("Glint 已就绪", "翻译未运行") });
        statusText.Children.Add(title);
        var detail = Caption("");
        detail.Margin = new Thickness(0, 5, 0, 0);
        detail.SetBinding(TextBlock.TextProperty, new Binding("MonitorStatus"));
        statusText.Children.Add(detail);
        Grid.SetColumn(statusText, 1);
        status.Children.Add(statusText);
        var pause = Button("", () => controller.IsPaused = !controller.IsPaused);
        pause.SetBinding(ContentControl.ContentProperty, new Binding("IsPaused") { Converter = new BooleanLabelConverter("继续翻译", "暂停翻译") });
        Grid.SetColumn(pause, 2);
        status.Children.Add(pause);
        content.Children.Add(Card(null, status));

        var introduction = new StackPanel();
        introduction.Children.Add(Text("输入中文时，在候选旁查看对应的译文。", 14, FontWeights.Medium));
        introduction.Children.Add(Caption("切换到中文拼音，在任意普通输入框中输入“你好”。候选停留后，Glint 会在候选栏旁显示英文释义。", top: 8));
        var actions = Actions(
            Button("预览翻译栏", controller.PreviewHud, primary: true),
            Button("查看输入法支持", () => NavigateTo("input"), link: true));
        actions.Margin = new Thickness(0, 14, 0, 0);
        introduction.Children.Add(actions);
        content.Children.Add(Card(null, introduction));

        var cards = new Grid();
        cards.ColumnDefinitions.Add(new ColumnDefinition());
        cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        cards.ColumnDefinitions.Add(new ColumnDefinition());
        var appearance = new StackPanel();
        appearance.Children.Add(Summary("停留", "Settings.DwellMs", "{0} ms"));
        appearance.Children.Add(Summary("字号", "Settings.HudFontSize", "{0:0} pt"));
        appearance.Children.Add(Summary("释义数量", "Settings.MaxTranslations", "最多 {0} 项"));
        appearance.Children.Add(Button("调整显示与触发  ›", () => NavigateTo("display"), link: true));
        var appearanceCard = Card("显示与触发", appearance);
        appearanceCard.Margin = new Thickness(0);
        cards.Children.Add(appearanceCard);
        var capability = new StackPanel();
        var learning = Text("", 12);
        learning.SetBinding(TextBlock.TextProperty, new Binding("Settings.TargetLanguageName"));
        capability.Children.Add(SummaryRow("学习语言", learning));
        capability.Children.Add(SummaryText("核心词典", "CC-CEDICT + ECDICT"));
        var modelStatus = Caption("");
        modelStatus.SetBinding(TextBlock.TextProperty, new Binding("TranslationStatus"));
        modelStatus.Margin = new Thickness(0, 6, 0, 8);
        capability.Children.Add(modelStatus);
        capability.Children.Add(Button("管理翻译能力  ›", () => NavigateTo("models"), link: true));
        var capabilityCard = Card("翻译能力", capability);
        capabilityCard.Margin = new Thickness(0);
        Grid.SetColumn(capabilityCard, 2);
        cards.Children.Add(capabilityCard);
        cards.Margin = new Thickness(0, 0, 0, 16);
        content.Children.Add(cards);

        var shortcut = Button("", () => NavigateTo("general"), link: true);
        shortcut.SetBinding(ContentProperty, new Binding("Settings.ShortcutSummary"));
        shortcut.FontSize = 11;
        shortcut.HorizontalAlignment = HorizontalAlignment.Left;
        shortcut.SetResourceReference(Control.ForegroundProperty, "SecondaryTextBrush");
        content.Children.Add(shortcut);
        return content;
    }

    private FrameworkElement BuildGeneral()
    {
        var content = Page();
        content.Children.Add(Card("释义插入", Rows(
            SettingRow("插入触发键", ShortcutEditor(nameof(AppSettings.InsertModifiers), true), "点按后按下新的修饰键，数字 1 到 9 对应该序号。删除后关闭快捷键插入。"),
            SettingRow("鼠标点选", Switch("Settings.MouseInsert", "鼠标点选"), "点击翻译栏中的释义即可插入。"))));
        var speech = Rows(
            SettingRow("朗读触发键", ShortcutEditor(nameof(AppSettings.SpeechShortcut), false), "点按后按下新的快捷键。删除后不再用快捷键朗读。"),
            SettingRow("语速", Options("Settings.SpeechRate", [("较慢", -2), ("正常", 0), ("较快", 2)])),
            SettingRow("自动朗读", Switch("Settings.AutoSpeak", "自动朗读"), "候选稳定后朗读首项译文。"));
        content.Children.Add(Card("释义朗读", speech));
        content.Children.Add(Card("基础", Rows(
            SettingRow("开机自启动", Switch("Settings.LaunchAtLogin", "开机自启动"), "登录 Windows 后在托盘运行。"),
            SettingRow("隐藏任务栏图标", Switch("Settings.HideTaskbarIcon", "隐藏任务栏图标"), "可通过系统托盘重新打开设置。"),
            SettingRow("外观", Options("Settings.Theme", [("跟随系统", "System"), ("浅色", "Light"), ("深色", "Dark")])),
            SettingRow("减少动态效果", Switch("Settings.ReduceMotion", "减少动态效果"), "关闭界面和翻译栏动画。"))));
        content.Children.Add(Caption("关闭主窗口后，Glint 会继续在系统托盘运行。右键托盘图标可以暂停翻译或退出。"));
        return content;
    }

    private FrameworkElement BuildLanguage()
    {
        var content = Page();
        var direction = new StackPanel { Orientation = Orientation.Horizontal };
        direction.Children.Add(DirectionBox("输入语言", "简体中文"));
        var arrow = Text("→", 20);
        arrow.Margin = new Thickness(16, 0, 16, 0);
        arrow.VerticalAlignment = VerticalAlignment.Center;
        arrow.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
        direction.Children.Add(arrow);
        var learningName = Text("", 16, FontWeights.Medium);
        learningName.Margin = new Thickness(0, 6, 0, 0);
        learningName.SetBinding(TextBlock.TextProperty, new Binding("Settings.TargetLanguageName"));
        var learningBox = new StackPanel();
        learningBox.Children.Add(Caption("学习语言"));
        learningBox.Children.Add(learningName);
        var learningBorder = new Border { Padding = new Thickness(16, 12, 16, 12), MinWidth = 144, Child = learningBox, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        learningBorder.SetResourceReference(Border.BackgroundProperty, "ContentBrush");
        learningBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        direction.Children.Add(learningBorder);
        var language = new StackPanel();
        language.Children.Add(direction);
        var target = new ComboBox { Width = 220, HorizontalAlignment = HorizontalAlignment.Right, DisplayMemberPath = "Label", SelectedValuePath = "Value" };
        target.ItemsSource = new[] { new Choice("英语", "en"), new Choice("日语", "ja"), new Choice("韩语", "ko") };
        target.SetBinding(ComboBox.SelectedValueProperty, SettingBinding("Settings.TargetLanguage"));
        AutomationProperties.SetName(target, "学习语言");
        language.Children.Add(SettingRow("学习语言", target, "英语、日语和韩语都翻译当前的中文候选。词典优先、模型优先和智能融合同样生效。"));
        language.Children.Add(Caption("日语有汉字时先显示汉字，平假名写在括号里；没有汉字时直接显示假名。韩语直接显示译文。词性只在英语单词上显示。", top: 8));
        content.Children.Add(Card("学习语言", language));
        content.Children.Add(Card("翻译方式", SettingRow("优先来源", Options("Settings.TranslationMode", [
            ("智能融合", "Smart"), ("词典优先", "DictionaryFirst"), ("模型优先", "ModelFirst")]),
            "词典适合词语释义，模型适合短句。所选来源不可用或未命中时，自动尝试另一来源。")));
        var model = new StackPanel();
        var state = Text("");
        state.SetBinding(TextBlock.TextProperty, new Binding("TranslationStatus"));
        model.Children.Add(state);
        model.Children.Add(Caption("英语词语使用内置词典。日语和韩语目前没有对应词典，词语和短句都由各自的本地模型翻译。", top: 7));
        model.Children.Add(Button("管理翻译模型…", () => NavigateTo("models"), link: true));
        content.Children.Add(Card("翻译模型", model));
        var voices = new ComboBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Right, DisplayMemberPath = "Label", SelectedValuePath = "Value" };
        void FillVoices()
        {
            var selected = controller.Settings.Voice;
            voices.ItemsSource = new[] { new Choice("系统默认", "") }.Concat(controller.SpeechVoiceNames().Select(x => new Choice(x, x))).ToArray();
            voices.SelectedValue = voices.Items.Cast<Choice>().Any(x => Equals(x.Value, selected)) ? selected : "";
        }
        FillVoices();
        target.SelectionChanged += (_, _) => FillVoices();
        voices.SetBinding(ComboBox.SelectedValueProperty, SettingBinding("Settings.Voice"));
        AutomationProperties.SetName(voices, "朗读音色");
        var voice = Rows(
            SettingRow("音色", voices, "使用已安装的对应语言语音。没有该语言语音时不朗读。"),
            Actions(Button("试听语音", controller.SpeakPreview), Button("管理系统语音…", () => OpenUrl("ms-settings:speech"), link: true)));
        content.Children.Add(Card("朗读语音", voice));
        return content;
    }

    private FrameworkElement BuildDisplay()
    {
        var content = Page();
        content.Children.Add(Card("位置与触发", Rows(
            SettingRow("翻译范围", Text("当前选中候选"), "只读取输入法当前高亮的候选，不读取输入框正文。"),
            SettingRow("显示词性", Switch("Settings.ShowPartsOfSpeech", "显示词性"), "仅对单个英文词显示简洁的英文词性缩写。"),
            SettingRow("释义数量", Options("Settings.MaxTranslations", Enumerable.Range(1, 9).Select(x => ($"{x} 个", (object)x)).ToArray())),
            SettingRow("显示位置", Options("Settings.HudPosition", [("自动", "Automatic"), ("候选栏上方", "Above")]), "自动避开输入行，空间不足时调整位置。"),
            SettingRow("候选停留", Options("Settings.DwellMs", [("较快 · 50 ms", 50), ("平衡 · 75 ms", 75), ("较稳 · 100 ms", 100)]),
                "较快更早出译文，连续输入时可能翻到还没停住的候选。较稳少误触发，译文稍晚出现。平衡介于两者之间。"))));
        var textColor = Options("Settings.TextColor", [("自动", "Auto"), ("浅色", "#F2F2F7"), ("白色", "#FFFFFF"), ("深色", "#25252A"), ("暖黄色", "#FFE8A3")]);
        content.Children.Add(Card("外观", Rows(
            SettingRow("翻译栏背景", Options("Settings.BackgroundStyle", [("磨砂玻璃", "Frosted"), ("系统玻璃", "System"), ("纯色", "Solid")]), "玻璃效果随 Windows 的透明效果设置调整。"),
            SettingRow("文字颜色", textColor),
            SliderRow("文字大小", "Settings.HudFontSize", 12, 18, 1, "{0:0} pt"),
            SliderRow("与候选栏间距", "Settings.CandidateGap", 0, 24, 1, "{0:0} pt"),
            Actions(Button("预览翻译栏", controller.PreviewHud), Button("恢复默认外观", ResetAppearance, link: true)))));
        return content;
    }

    private FrameworkElement BuildInputMethods()
    {
        var content = Page();
        content.Children.Add(Card("候选读取", Rows(
            Info("微软拼音", "优先适配", "通过 Windows 输入法候选可访问性接口，读取当前高亮候选。"),
            Info("微信输入法", "读取当前高亮", "适用于 64 位普通窗口。32 位兼容路径仅用于默认位置安装的企业微信。"),
            Info("其他输入法", "按兼容性支持", "搜狗、小狼毫等输入法需要提供标准候选结构；无法确认高亮候选时，Glint 不会显示译文。"))));
        var test = new StackPanel();
        test.Children.Add(Text("检查输入法是否可用", 14, FontWeights.Medium));
        test.Children.Add(Caption("1. 保持 Glint 运行并切换到中文拼音。\n2. 在记事本等普通输入框中输入 nihao，不要按空格。\n3. 等候选稳定，检查候选栏旁是否出现译文。", top: 9));
        var live = Caption("");
        live.SetBinding(TextBlock.TextProperty, new Binding("MonitorStatus"));
        live.Margin = new Thickness(0, 12, 0, 0);
        test.Children.Add(live);
        test.Children.Add(Actions(Button("打开输入设置…", () => OpenUrl("ms-settings:typing")), Button("预览翻译栏", controller.PreviewHud, link: true)));
        content.Children.Add(Card(null, test));
        content.Children.Add(Card("兼容性说明", Caption("密码框、终端和管理员应用会跳过。系统或输入法未暴露高亮候选时，不会推测候选，也不会读取输入框中的内容。")));
        return content;
    }

    private FrameworkElement BuildModels()
    {
        var content = Page();
        content.Children.Add(Card("内置词典", Rows(
            Info("CC-CEDICT", "随应用提供", "常见中文词语与短语的英文释义。"),
            Info("ECDICT", "随应用提供", "英文词频排序与词性提示。"))));
        var opus = new StackPanel();
        opus.Children.Add(Info("OPUS-MT · 中文 → 英语", "本地翻译", "轻量离线模型，可翻译短语和短句。首次准备需要联网下载，日常翻译无需联网。"));
        opus.Children.Add(Info("OPUS-MT · 中文 → 日语", "本地翻译", "与英语相同，词语和短句都在本机翻译。有汉字时显示汉字，平假名写在括号里。"));
        opus.Children.Add(Info("OPUS-MT · 中文 → 韩语", "本地翻译", "与英语相同，词语和短句都在本机翻译。"));
        var availability = Caption("");
        availability.SetBinding(TextBlock.TextProperty, new Binding("ModelAvailability"));
        availability.Margin = new Thickness(0, 8, 0, 0);
        opus.Children.Add(availability);
        var state = Caption("");
        state.SetBinding(TextBlock.TextProperty, new Binding("ModelInstallStatus"));
        state.Margin = new Thickness(0, 10, 0, 12);
        opus.Children.Add(state);
        var install = Button("准备本地模型", async () => await InstallModelAsync(), primary: true);
        install.SetBinding(IsEnabledProperty, new Binding("IsInstallingModel") { Converter = new InverseBooleanConverter() });
        opus.Children.Add(Actions(install, Button("打开数据目录", controller.OpenDataFolder, link: true)));
        content.Children.Add(Card("翻译模型", opus));
        content.Children.Add(Caption("Apple 翻译与 MLX 依赖 Apple 平台，Windows 版使用内置词典和 OPUS-MT。词典与模型的许可证随应用附带。"));
        return content;
    }

    private async Task InstallModelAsync()
    {
        try { await controller.InstallModelAsync(); }
        catch { /* The controller exposes the preparation error inline. */ }
    }

    private FrameworkElement BuildPrivacy()
    {
        var content = Page();
        content.Children.Add(Card("系统访问", Rows(
            Info("输入法候选", "无需额外授权", "Windows 无需 macOS 辅助功能授权。Glint 仅访问标准输入法候选窗口。"),
            Info("安全输入", "自动跳过", "密码框、终端和无法确认安全状态的输入场景不读取候选。"),
            Info("管理员应用", "受系统限制", "普通权限的 Glint 无法读取或插入到权限更高的应用。"))));
        content.Children.Add(Card("隐私", Rows(
            Info("只在本地处理", "", "候选与译文不发送到网络服务，不保存候选文本、翻译结果或输入历史。"),
            Info("保持输入习惯", "", "不截图、不读取正文、不安装键盘事件监听，插入译文不写入系统剪贴板。"),
            Info("按需联网", "", "仅在主动准备本地模型时，下载运行库与模型数据。"))));
        content.Children.Add(Card("本地数据", SettingRow("偏好与模型", Button("打开数据目录", controller.OpenDataFolder), "偏好设置只记录显示、语音与启动选项。")));
        return content;
    }

    private FrameworkElement BuildAbout()
    {
        var content = Page();
        var about = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 520, Margin = new Thickness(0, 22, 0, 24) };
        about.Children.Add(new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Assets/glint-logo.png")), Width = 96, Height = 96, Margin = new Thickness(0, 0, 0, 22) });
        var title = Text("Glint", 25, FontWeights.SemiBold);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        about.Children.Add(title);
        var detail = Caption("输入中文时，在候选旁查看对应的译文。", top: 10);
        detail.TextAlignment = TextAlignment.Center;
        about.Children.Add(detail);
        var version = Caption("Windows 移植版 · 0.1.0", top: 7);
        version.Text = "Windows 移植版 · " + typeof(App).Assembly.GetName().Version?.ToString(3);
        version.TextAlignment = TextAlignment.Center;
        about.Children.Add(version);
        var links = Actions(Button("GitHub 原项目", () => OpenUrl("https://github.com/jsogn/Glint"), link: true), Button("原项目问题反馈", () => OpenUrl("https://github.com/jsogn/Glint/issues"), link: true));
        links.HorizontalAlignment = HorizontalAlignment.Center;
        links.Margin = new Thickness(0, 20, 0, 0);
        about.Children.Add(links);
        content.Children.Add(about);

        var test = new StackPanel();
        test.Children.Add(Caption("此处仅用于手动测试本地翻译，不会保存输入内容。"));
        var entry = new TextBox { Text = "你好", Margin = new Thickness(0, 10, 0, 10), MaxLength = 200 };
        AutomationProperties.SetName(entry, "手动翻译测试文本");
        test.Children.Add(entry);
        var result = Text("");
        result.TextWrapping = TextWrapping.Wrap;
        result.LineHeight = 22;
        result.Margin = new Thickness(0, 12, 0, 0);
        var translate = Button("测试翻译", () => { }, primary: true);
        translate.Click += async (_, _) =>
        {
            translate.IsEnabled = false;
            result.Text = "正在翻译…";
            try { result.Text = await controller.TestTranslationAsync(entry.Text); }
            catch { result.Text = "翻译暂不可用，请检查词典与模型状态。"; }
            finally { translate.IsEnabled = true; }
        };
        test.Children.Add(Actions(translate, Button("预览翻译栏", controller.PreviewHud)));
        test.Children.Add(result);
        content.Children.Add(Card("手动测试", test));
        return content;
    }

    private void ResetAppearance()
    {
        controller.Settings.BackgroundStyle = "Frosted";
        controller.Settings.HudPosition = "Automatic";
        controller.Settings.HudFontSize = 14;
        controller.Settings.CandidateGap = 6;
        controller.Settings.HudOpacity = 0.8;
        controller.Settings.SolidColor = "#1C1C1F";
        controller.Settings.TextColor = "Auto";
    }

    private static StackPanel Page() => new();

    private Border Card(string? title, params UIElement[] children)
    {
        var body = new StackPanel();
        if (title != null)
        {
            var heading = Text(title, 14, FontWeights.SemiBold);
            heading.Margin = new Thickness(0, 0, 0, 10);
            AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
            body.Children.Add(heading);
        }
        foreach (var child in children) body.Children.Add(child);
        var card = new Border { Padding = new Thickness(16), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 16), Child = body };
        card.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return card;
    }

    private static StackPanel Rows(params UIElement[] children)
    {
        var rows = new StackPanel();
        for (var i = 0; i < children.Length; i++)
        {
            if (i > 0)
            {
                var rule = new Border { Height = 1, Margin = new Thickness(0, 2, 0, 2) };
                rule.SetResourceReference(Border.BackgroundProperty, "BorderBrush");
                rule.SetBinding(VisibilityProperty, new Binding("Visibility") { Source = children[i] });
                rows.Children.Add(rule);
            }
            rows.Children.Add(children[i]);
        }
        return rows;
    }

    private FrameworkElement SettingRow(string title, UIElement control, string? caption = null)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = Text(title);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(0, 0, 16, 0);
        row.Children.Add(label);
        Grid.SetColumn(control, 1);
        row.Children.Add(control);
        stack.Children.Add(row);
        if (caption != null) stack.Children.Add(Caption(caption, top: 6));
        return stack;
    }

    private FrameworkElement SliderRow(string title, string path, double min, double max, double step, string format, string? caption = null)
    {
        var controls = new StackPanel();
        var value = Text("");
        value.HorizontalAlignment = HorizontalAlignment.Right;
        value.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
        value.SetBinding(TextBlock.TextProperty, new Binding(path) { StringFormat = format });
        controls.Children.Add(SettingRow(title, value));
        var slider = new Slider { Minimum = min, Maximum = max, TickFrequency = step, IsSnapToTickEnabled = true, Margin = new Thickness(0, 0, 0, 4) };
        slider.SetBinding(Slider.ValueProperty, SettingBinding(path));
        slider.SetResourceReference(Control.ForegroundProperty, "AccentBrush");
        AutomationProperties.SetName(slider, title);
        controls.Children.Add(slider);
        if (caption != null) controls.Children.Add(Caption(caption, top: 3));
        controls.Margin = new Thickness(0, 3, 0, 8);
        return controls;
    }

    private static TextBlock Text(string text, double size = 13, FontWeight? weight = null) => new() { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap };

    private TextBlock Caption(string text, double top = 0) => new() { Text = text, Style = (Style)FindResource("MutedText"), Margin = new Thickness(0, top, 0, 0) };

    private static Binding SettingBinding(string path) => new(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged };

    private static CheckBox Switch(string path, string title)
    {
        var toggle = new CheckBox();
        toggle.SetBinding(CheckBox.IsCheckedProperty, SettingBinding(path));
        AutomationProperties.SetName(toggle, title);
        return toggle;
    }

    private static ComboBox Options(string path, (string Label, object Value)[] choices)
    {
        var combo = new ComboBox { Width = 210, HorizontalAlignment = HorizontalAlignment.Right, ItemsSource = choices.Select(x => new Choice(x.Label, x.Value)).ToArray(), DisplayMemberPath = "Label", SelectedValuePath = "Value" };
        combo.SetBinding(ComboBox.SelectedValueProperty, SettingBinding(path));
        AutomationProperties.SetName(combo, path.Split('.').Last());
        return combo;
    }

    private Button Button(string title, Action action, bool primary = false, bool link = false)
    {
        var button = new Button { Content = title };
        if (primary) button.Style = (Style)FindResource("PrimaryButton");
        else if (link) button.Style = (Style)FindResource("LinkButton");
        button.Click += (_, _) => action();
        AutomationProperties.SetName(button, title);
        return button;
    }

    private static WrapPanel Actions(params UIElement[] controls)
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 6, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (var child in controls)
        {
            if (child is FrameworkElement element) element.Margin = new Thickness(0, 0, 12, 4);
            panel.Children.Add(child);
        }
        return panel;
    }

    private FrameworkElement ShortcutEditor(string property, bool modifiersOnly)
    {
        var label = Text("", 12);
        label.SetBinding(TextBlock.TextProperty, new Binding("Settings." + property) { Converter = new ShortcutDisplay(modifiersOnly) });
        var border = new Border { Child = label, Padding = new Thickness(10, 5, 10, 5), CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Focusable = true };
        border.SetResourceReference(Border.BackgroundProperty, "ControlBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        var binding = new Binding("Settings." + property) { Converter = new ShortcutDisplay(modifiersOnly) };
        border.MouseLeftButtonDown += (_, args) => { border.Focus(); args.Handled = true; };
        border.GotKeyboardFocus += (_, _) => { BindingOperations.ClearBinding(label, TextBlock.TextProperty); label.Text = "请按快捷键"; };
        border.LostKeyboardFocus += (_, _) => label.SetBinding(TextBlock.TextProperty, binding);
        border.PreviewKeyDown += (_, args) =>
        {
            args.Handled = true;
            if (args.Key == Key.Escape) { Keyboard.ClearFocus(); return; }
            if (args.Key is Key.Back or Key.Delete) { AssignShortcut(property, ""); Keyboard.ClearFocus(); return; }
            if (!HotkeySetting.TryCapture(args, modifiersOnly, out var stored)) return;
            AssignShortcut(property, stored);
            Keyboard.ClearFocus();
        };
        var clear = Button("删除", () => AssignShortcut(property, ""), link: true);
        clear.SetBinding(VisibilityProperty, new Binding("Settings." + property) { Converter = new ShortcutVisibility() });
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(border);
        clear.Margin = new Thickness(8, 0, 0, 0);
        row.Children.Add(clear);
        return row;
    }

    private void AssignShortcut(string property, string value)
    {
        if (property == nameof(AppSettings.InsertModifiers)) controller.Settings.InsertModifiers = value;
        else controller.Settings.SpeechShortcut = value;
    }

    private Border IconTile(string glyph)
    {
        var icon = Text(glyph, 21);
        icon.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");
        var tile = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(10), Child = icon, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        tile.SetResourceReference(Border.BackgroundProperty, "SelectionBrush");
        return tile;
    }

    private FrameworkElement Summary(string label, string path, string? format = null)
    {
        var value = Text("", 12);
        value.SetBinding(TextBlock.TextProperty, new Binding(path) { StringFormat = format });
        return SummaryRow(label, value);
    }

    private FrameworkElement SummaryText(string label, string value) => SummaryRow(label, Text(value, 12));

    private FrameworkElement SummaryRow(string label, TextBlock value)
    {
        var row = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(Caption(label));
        Grid.SetColumn(value, 1);
        row.Children.Add(value);
        return row;
    }

    private FrameworkElement Info(string title, string status, string description)
    {
        var body = new StackPanel { Margin = new Thickness(0, 7, 0, 7) };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(Text(title, 13, FontWeights.Medium));
        if (!string.IsNullOrEmpty(status))
        {
            var state = Caption(status);
            state.Margin = new Thickness(16, 0, 0, 0);
            Grid.SetColumn(state, 1);
            row.Children.Add(state);
        }
        body.Children.Add(row);
        body.Children.Add(Caption(description, top: 5));
        return body;
    }

    private Border DirectionBox(string title, string language)
    {
        var stack = new StackPanel();
        stack.Children.Add(Caption(title));
        var label = Text(language, 16, FontWeights.Medium);
        label.Margin = new Thickness(0, 6, 0, 0);
        stack.Children.Add(label);
        var box = new Border { Padding = new Thickness(16, 12, 16, 12), MinWidth = 144, Child = stack, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        box.SetResourceReference(Border.BackgroundProperty, "ContentBrush");
        box.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return box;
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Win32Exception) { }
        catch (InvalidOperationException) { }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed class ShortcutDisplay(bool modifiersOnly) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            modifiersOnly ? HotkeySetting.DisplayModifiers(value as string ?? "") : HotkeySetting.DisplayShortcut(value as string ?? "");
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => Binding.DoNothing;
    }

    private sealed class ShortcutVisibility : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => Binding.DoNothing;
    }

    private sealed record NavigationItem(string Id, string Title, string Glyph);
    private sealed record Choice(string Label, object Value)
    {
        public override string ToString() => Label;
    }

    private sealed class BooleanLabelConverter(string yes, string no) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is true ? yes : no;
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => Binding.DoNothing;
    }

    private sealed class InverseBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is not true;
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is not true;
    }

    private sealed class StringVisibilityConverter(string target) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value as string == target ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => Binding.DoNothing;
    }
}
