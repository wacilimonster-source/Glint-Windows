# Windows 移植说明

- 实现日期：2026-11-13
- 上游基线：`c243b461515bf8aa7ee222cb5f68afedf9915cea`
- 工程：`Windows/Glint.Windows/Glint.Windows.csproj`
- 平台：.NET 10 / WPF / Windows x64
- 状态：0.1.6。标准候选协议和 UIA 选中事件已由独立 fixture 验证。微信输入法 3.0.2.22 的当前高亮已由独立进程验证；微软拼音的真实界面仍需人工看译文栏。豆包输入法 1.0.0.23 的当前高亮已由独立进程验证

## 架构

macOS 的 Swift、AppKit、Accessibility 与 Apple 翻译 API 不能作为 Windows 可执行程序直接运行。Windows 工程按现有模块职责重建平台适配，复用原词典数据库、模型数据、测试样本与品牌资源，不改变现有 macOS 工程。

| 原版职责 | Windows 实现 |
| --- | --- |
| AppKit 生命周期、菜单栏 | WPF Application、Windows 托盘、命名 Mutex 单实例与事件激活 |
| SwiftUI 设置 | WPF 侧栏与 8 个页面，DynamicResource 浅色/深色主题 |
| AX 候选 | 独立 MTA 线程上的 UI Automation；只进入明确 IME 宿主及 `IME_Candidate_Window` 树 |
| 当前高亮 | Selection / SelectionItem pattern 的真实选中状态；不把首候选当高亮 |
| 单行 NSPanel | 持久 WPF HUD，NOACTIVATE / TOOLWINDOW，不抢焦点；禁用鼠标点选时穿透鼠标 |
| SQLite 词典 | Windows 自带 winsqlite3.dll；只读打开原 CC-CEDICT / ECDICT 数据 |
| CTranslate2 模型 | 私有 Python embeddable + 固定校验版本 CTranslate2 / SentencePiece，持久本地 CPU worker |
| 系统朗读 | SAPI 已安装的英语语音，不联网 |
| 偏好 | LocalApplicationData/Glint/settings.json，校验范围与原子覆盖 |
| 登录启动 | 当前用户 Run 注册表项，用户切换开关时才修改 |

应用没有第三方 NuGet 依赖；可选推理运行环境按需下载，不进入主程序 ZIP。

## 候选与安全边界

读取基于微软的 [IME UI Automation 协议](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-implementing-providers-for-imes)。候选标识不等于所有输入法都实现了同样的可访问性树，兼容性必须按真实输入法与宿主版本验证。

1. 先检查前台进程、焦点身份、密码属性、可用性与窗口权限。
2. 只使用 TextPattern 选区端点比较判断非空选区，不调用 GetText、Value 或读取输入框 Name。
3. 仅在明确输入法宿主中找指定候选容器；仅读取真实选中候选项的 Name。
4. 高亮变化立即取消旧代次、解除快捷键；HUD 保持原有亮度显示旧译文，等新译文完成后直接替换。稳定停留 50、75 或 100ms（默认 100ms）后才触发翻译。
5. 每次结果显示前重新核对候选 Revision；失焦、结束组合或安全焦点立即关闭 HUD。
6. 译文插入由显式快捷键或鼠标点选触发，鼠标点选默认开启，可在通用配置关闭。等待用户释放修饰键，重新读取并确认同一候选与焦点，Escape 后等待组合结束，再使用 SendInput Unicode。任何检查失败均停止；候选变化时旧译文不可点击。

没有键盘钩子、OCR、屏幕抓取、剪贴板访问、输入历史、远程翻译 API。运行日志不输出真实候选或译文。

UIA 调用放在单一后台线程，提供方异常不会阻塞 WPF 线程；有扫描节点与时间预算。提供方自身的单次 UIA 调用仍可能长时间阻塞，停止时不等待该调用，也不会反复创建新线程。发生这种情况会丢失监控能力，不能声称硬实时。

微软拼音候选菜单的打开事件即使发生在窗口仍标记为离屏时，也会启动最长约 10 秒的重试，以等待输入过程中才出现的选中状态；只有窗口可见且确认真实高亮后才发布候选。菜单关闭会取消重试。真实输入法首次输入仍需人工验证。

## 翻译与安装

英语词典保留上游清洗、权威义项优先、ECDICT 词频与词性补充和精确有界 LRU。缓存仅在内存，键包含目标语言、偏好与模型版本。智能融合、词典优先和模型优先均有失败回退；非英语返回空，不悄悄回退到英语。

短句使用原 `mt-opus-zh-en-0.1.0.zip` 数据，复用 `model-lock.json` 每个模型文件的锁。Windows 运行库另有 `Runtime/runtime-lock.json`，固定 HTTPS URL 与 SHA256。ZIP 解包检查路径逃逸、链接、大小限制和模型文件白名单；安装在本用户 Glint 专属 staging 中进行，校验并实际推理成功后原子替换，失败保留旧版本。

worker 以隔离模式启动，标准输入输出传输当前候选；输出 ID 必须匹配，取消或超时会终止旧 worker，下一次重新启动。worker 不访问网络，不缓存用户请求到磁盘。下载进程不接收用户候选。

## 外观与交互

- 保留原侧栏页序、中文文案和紧凑卡片；使用 Windows 窗口按钮和可调整尺寸窗口。
- 设置页切换使用轻微位移和淡入；滚轮滚动按系统设置连续跟随输入，反向时立即调整目标，触控平移保留系统惯性，并在边缘提供轻微反馈。系统关闭动画或启用“减少动态效果”时停用这些动效。
- HUD 固定单行 36 DIP，默认间距 6 DIP，淡入 80ms、淡出 60ms；遵守关闭动画与减少动态效果。
- 横向定位优先与候选栏左侧对齐：微软拼音优先使用选中项所属列表的左边缘，微信使用系统提供的可见窗口边界，豆包在可见窗口边界基础上按 DPI 校准默认皮肤的候选行左侧留白；屏幕空间不足时仍会向内避让。其他皮肤的透明留白和实际对齐效果需要在对应输入法中核验。
- 自动定位根据宿主提供的输入行、插入符或组合窗口几何判断方向：原文在候选栏下面时译文放上方；原文在上面时优先放下方。取不到可靠几何时默认放下方；屏幕空间不足时回退另一侧，微软拼音回退上方时为空出原文保留净空。整段输入框不算输入行，宿主未暴露有效几何时无法保证避让。
- 玻璃选项使用 Windows Composition 的 host backdrop 画刷、高斯模糊和圆角几何裁剪；像素由系统合成，应用不读取或保存桌面图像。禁用透明效果、系统不支持或初始化失败时使用圆角半透明填充。
- 纯色使用固定的主题自适应圆角背景，不再提供背景颜色和不透明度选项。
- 玻璃背景的回退填充和自动文字颜色随应用浅色或深色主题变化；用户指定的文字颜色保持不变。
- 最小窗口 820×590 DIP，所有页面可滚动；可切换跟随系统、浅色和深色。
- 通用配置可隐藏主窗口的任务栏图标；设置窗口仍可从系统托盘重新打开，默认显示任务栏图标。

## 验证

2026-11-14 用户实测：微软和微信输入法均未触发，真实兼容性未通过。0.1.1 的主动诊断显示记事本焦点检查已通过，失败停在候选列表发现；可见的 `TextInputHost` `Windows.UI.Core.CoreWindow` 当时只有一个 UIA 根节点。该次报告没有事件监听，也没有记录全部可见顶层窗口，不能据此断定输入法不提供候选接口。

0.1.3 的用户诊断确认：微软拼音的选中事件能到达，候选菜单的 AutomationId 是 `IME_Candidate_Window`，高亮项是其子列表中的 ListItem。`ElementFromHandle` 看到的 `TextInputHost` CoreWindow 仍然只有根节点，跨线程解组这个 XAML 元素也不可用。因此当前高亮的文本和位置只在事件回调线程读取，并只保存在内存里，直到候选菜单关闭或焦点不再安全。候选菜单刚打开时，第一项经常已经选中，不会再发选中事件，所以打开时就读取当前高亮。第一次弹出时输入法自己的焦点项可能仍被标成离屏，这时改认前台窗口，避免把这一次候选丢掉。翻页时选中项常常被复用而不再发出选中事件，因此只监听该项的名称变化并立刻重读整词。候选已经读到后不再做整窗扫描。单词在词典中有结果时立即显示，不等待本地模型；短句和词典未命中仍使用模型。诊断记录事件结构时不写入这段文字。独立 fixture 仍须观察到跨进程选中事件。真实微信输入法仍未验收。

后续修复：增加原生 UIA3 路径，支持现代 XAML 候选树；以 HWND 祖先关系验证 Chromium 多进程焦点，使用 TextEdit 的 ActiveComposition 区分拼音组合区与普通选区。候选查找覆盖微软及微信宿主和当前前台树，仍仅匹配明确的 IME_Candidate_Window 与真实选中项。跨进程协议 fixture 验证已加入自检，不能代替真实微软或微信输入法验收。输入法支持页现在报告焦点、候选列表与高亮读取的不同失败阶段。

2026-11-14 微信输入法 3.0.2.22 在记事本的候选诊断：`wetype_renderer` 的 `WeType.NativeCandidate.Direct2D.v2` 窗口可见时，UIA 只返回窗口根节点，原始视图没有子节点；MSAA 客户区有 0 个子项且没有选中项。报告保留的近期事件中没有该渲染进程的候选事件。报告中 `TextInputHost` 的标准候选事件不能作为微信输入法候选结构的证据。当前没有证据证明此版本提供可读取的高亮候选，Windows 微信输入法适配仍未完成；这次结果不排除其他可访问接口或版本差异。

同日补充 WinEvent 采集：用户在记事本使用微信输入法并切换候选时，目标候选窗发出窗口显示和位置变化事件，事件对应窗口本身（object ID 和 child ID 均为 0），没有观察到候选项或选中事件。该诊断仅在用户主动采集时监听可访问性事件，过滤微信输入法进程及候选窗，不读取事件名称、候选文字或输入正文。TSF 的候选接口属于宿主应用的线程管理器，IMM 文档也规定线程不能访问其他线程创建的输入上下文，因此不能假设 Glint 自己的上下文能获取记事本的候选；参考 [TSF UI 元素管理器](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nn-msctf-itfuielementmgr) 和 [IMM 多线程限制](https://learn.microsoft.com/en-us/windows/win32/intl/developing-ime-aware-multiple-thread-applications)。

后续对记事本中显示的微信输入法候选做原生对象接口探测：候选窗 `WeType.NativeCandidate.Direct2D.v2` 和外层 `WeType.NativeCandidate.Decoration.v2` 通过 `AccessibleObjectFromWindow` 的 `OBJID_NATIVEOM` 请求 `IDispatch` 与 `ITfCandidateListUIElement` 均返回 `0x80004005`。这只说明本次查询未获取接口，不排除输入法的其他未公开接口。复现可运行 `Windows/scripts/wetype-nativeom-probe.ps1`，脚本持续 75 秒，只记录上述窗口类及接口查询结果，不记录候选或输入正文；[Windows 原生对象接口说明](https://learn.microsoft.com/en-us/windows/win32/winauto/using-objid-nativeom-to-expose-a-native-object-model-interface-for-a-window)。

同日静态检查本机微信输入法 3.0.2.22 的 `wetype_tip_core.dll` 和 `wetype_renderer.exe`：两者包含 `candidateUpdate`、`select_candidate_index` 等协议字段名，渲染器包含 `IpcServerRenderer` 和微信输入法命名管道相关符号。这些字符串说明组件内部存在候选与进程间通信线索，但未证明存在第三方只读订阅接口，也不能确认字段代表当前高亮状态；不能仅凭字段名连接或发送命令。未接入该私有通信，未读取其运行时消息或进程内存。

进一步静态检查发现 `wetype.commands.CandidateList` 描述中有 `candidates` 和 `focused_index` 字段，且 `RendererDetail` 包含 `candidate_data`。这是潜在高亮字段的线索，尚未验证字段值、索引范围或对应关系，也未发现第三方只读订阅协议。相同内部消息模型还包含提交、剪贴板等非候选字段，不应将整条消息当作可安全读取的候选接口。

随后进行了实际命名管道动态探测：用 `PipeDirection.In` 建立独立连接，不写入任何命令，并通过 `GetNamedPipeServerProcessId` 验证服务端进程。用户在记事本用微信输入法输入并切换高亮的 60 秒观察中，`renderer_for_tip` 连接被关闭，收到 0 字节；`server_for_renderer` 收到 1 次、211 字节。随后在没有要求用户输入的 5 秒复测中，后者仍收到 1 次、211 字节。消息可从偏移 0 按 protobuf wire format 完整解析，只输出字段编号、wire 类型及长度，不输出标量值或文本。两次字节数相同不足以证明消息完全相同，也没有证据将这条消息与候选更新关联；仍不能据此验证 `focused_index`。探针未发送订阅命令，结论仅覆盖“新建只读连接后主动推送”的情形，不覆盖可能需要握手的协议。

`Windows/scripts/test.ps1` 运行真实数据库基准、偏好边界与往返、取消请求、语言能力边界、真实词性以及已安装时的本地模型推理，并渲染 8 个页面的浅深主题和最小窗口。基准沿用原英文词典分类，短语和模型专属分类单列，不计入词典准确率。

UIA 模块进行了隔离编译、结构尺寸与监控启停验证。自动化对临时输入框注入按键未生成可观察的微软拼音候选，因此不能据此宣称微软拼音已端到端验证。协议 fixture 验证与真实输入法验收应分开记录。

人工验收项目：

- Windows 11 微软拼音：记事本输入 `nihao`，观察候选旁英文；方向键切换高亮、连续输入、空格提交、Escape、失焦与输入法切换。
- 只有停留候选才启动翻译；快速切换不出现旧译文；组合内候选变化不闪烁新建窗口。
- Ctrl+Alt+数字插入正确项，不残留拼音，不触碰剪贴板；按住修饰键或切到其他窗口不能误插入。
- 密码框、终端、管理员窗口与文本选区不出现译文。
- 多屏、125%/150% DPI、屏幕边缘、长句和 9 项译文的单行布局。
- 真机磨砂、系统减少动态效果、SAPI 试听和自动朗读、托盘退出、登录启动。
- 微信、搜狗、小狼毫分别记录可访问性候选树后再扩大支持声明。

## 当前功能差异

0.1.2 的 Debug 自检已通过，包括独立进程里的真实选中事件。便携包仍使用本机 .NET 10 桌面运行库打包。词典基准 Top-1 / Top-3 均为 132/133（99.25%），2 个阻断用例与 15 项禁用译项检查通过，本地模型真实推理通过。真实拼音候选端到端路径仍未完成手工验收。

学习语言可以在英语、日语和韩语之间单选。英语仍走内置词典，未命中或短句回退到 OPUS-MT。日语和韩语使用同一套优先来源；这两门语言还没有内置词典，因此词语和短句都由各自固定的本地 OPUS-MT 模型翻译，不会悄悄改成英语。日语有汉字时显示为「汉字（平假名）」，没有汉字时直接显示假名。模型标识只允许 `mt-opus-zh-en`、`mt-opus-zh-ja` 和 `mt-opus-zh-ko`，数据经 SHA256 校验，不能执行第三方代码。尚不提供 Apple Translation、MLX、Rime 的 Windows 专属桥接、词典在线重建更新和 Windows 自动更新。Windows 版使用固定且可释放的译文操作快捷键；这些差异均不能以无效控件伪装为已经完成。

## 微信输入法 TSF 通道探测

- 探测日期：2026-11-14。
- 微信输入法：3.0.2.22（系统 TIP 核心 DLL 的 FileVersion / ProductVersion）。
- Windows：26H2，build 26300.9974。
- 探针：`Windows/scripts/wetype-tsf-probe.ps1`。使用系统 PowerShell 7 和 .NET 编译器，无新增 NuGet 依赖，不加载 Glint 主项目。
- 运行命令：`pwsh -NoProfile -File .\Windows\scripts\wetype-tsf-probe.ps1 -Seconds 60`。
- 测试方式：三个独立进程各运行 60 秒；用户分别在记事本切换微信输入法，输入 `nihao`，切换高亮并按 Escape。探针在 STA 上注册 sink 并泵消息，每 250ms 枚举调用线程的 UI Elements。仅观察窗口结构标志，日志不输出 HWND、线程 ID、候选原文或正文。

接口声明根据本机 Windows SDK `10.0.26100.0/um/msctf.h` 核对：`ITfUIElementMgr` 的 IID 为 `EA1EA135` 开头，`EA1EA136` 是 `ITfUIElementSink`，`ITfUIElement` 是 `EA1EA137`；后者没有 `GetType()`。候选类型以 `ITfCandidateListUIElement` QueryInterface 判断，方法顺序使用 SDK 声明，不能使用提示词中的缩略顺序作为 COM vtable 顺序。

| 步骤 | 第 1 次 | 第 2 次 | 第 3 次 |
| --- | --- | --- | --- |
| 前台线程和焦点 HWND（Win32，非 HRESULT） | 焦点存在、外部线程 | 焦点存在、外部线程 | 焦点存在、外部线程 |
| CoCreateInstance → ITfThreadMgr | `0x00000000` | `0x00000000` | `0x00000000` |
| ITfThreadMgr.Activate | `0x00000000` | `0x00000000` | `0x00000000` |
| TF_GetThreadMgr（调用线程） | `0x00000000` | `0x00000000` | `0x00000000` |
| QueryInterface → ITfUIElementMgr | `0x00000000` | `0x00000000` | `0x00000000` |
| QueryInterface → ITfSource | `0x00000000` | `0x00000000` | `0x00000000` |
| AdviseSink → ITfUIElementSink | `0x00000000` | `0x00000000` | `0x00000000` |
| EnumUIElements（起始采样） | `0x00000000` | `0x00000000` | `0x00000000` |
| IEnumTfUIElements.Next（起始采样） | `0x00000001` | `0x00000001` | `0x00000001` |
| 全程枚举得到 UI Element 的次数 | 0 | 0 | 0 |
| Begin / Update / End 回调数 | 0 / 0 / 0 | 0 / 0 / 0 | 0 / 0 / 0 |
| 外部焦点下微信候选窗可见的采样数 | 77 | 77 | 51 |
| UnadviseSink / Deactivate | 均 `0x00000000` | 均 `0x00000000` | 均 `0x00000000` |

`0x00000001` 是枚举器的 `S_FALSE`（无元素），不是接口查询失败。每次还观察到微信候选窗从不可见变为可见并恢复不可见。原始摘要（仅结构和计数）：

```text
elapsedMs=60014 summary Begin=0 Update=0 End=0 enumerated=0 foreignFocusSamples=232 visibleCandidateSamples=77
elapsedMs=60007 summary Begin=0 Update=0 End=0 enumerated=0 foreignFocusSamples=232 visibleCandidateSamples=77
elapsedMs=60020 summary Begin=0 Update=0 End=0 enumerated=0 foreignFocusSamples=232 visibleCandidateSamples=51
```

最终结论：本轮限定的独立外部进程 TSF 候选观察通道不存在。Windows 微信输入法不支持通过本轮测试的方式，由独立 Glint 进程使用自己的 `ITfUIElementMgr` 读取记事本中的候选；原因是三次成功注册后均无 UI Element 或回调，创建成功的管理器属于探针线程。按照三次失败即结束的规则，未继续添加探测变体。

结论边界：没有取得记事本线程的 `ITfThreadMgr`，也未验证该线程上是否注册了 UI Elements；公开 `TF_GetThreadMgr` 没有指定其他线程的参数。本次结果不能证明微信输入法在记事本进程内部完全不实现 TSF UI Element。因为没有取得任何 UI Element，候选接口及 Behavior 的 QueryInterface、GetCount、GetSelection 和 GetString 均未执行，不能报告它们的 HRESULT，更不能据此验证 `focused_index`。

验证：从脚本提取的完整 C# 探针已独立 `dotnet build` 通过，0 警告、0 错误；脚本三次实际运行均完成注册、观察、解除注册及 Deactivate，没有错误输出。本轮只新增脚本并追加本文档，未修改 `Glint.PlatformChecks` 或 `Glint.Windows`，因此没有运行产品自检或声称正式候选链路已验证。

## 微信输入法文字与颜色辅助功能探测

2026-11-14，对微信输入法 3.0.2.22 参考 macOS reader 的“候选文字 + 文字前景色”路径进行独立动态验证。探针位于 `Windows/scripts/WeTypeAccessibilityProbe/`，复用系统 UIA 类型库声明，无新增 NuGet 依赖，不接入正式 Glint 候选链路。

```powershell
dotnet build Windows/scripts/WeTypeAccessibilityProbe/WeTypeAccessibilityProbe.csproj -o build/wetype-accessibility-probe
.\build\wetype-accessibility-probe\WeTypeAccessibilityProbe.exe 90
```

探针只对 `wetype_renderer` 的可见 `WeType.NativeCandidate.Direct2D.v2` 窗口查询接口，并在窗内五个横向点位调用命中接口，校验进程及窗口祖先归属。用户在记事本使用微信输入法输入、保持候选、切换高亮并按 Escape。输出只含接口编号、HRESULT、结构和计数，不调用候选 Name、Value 或 GetText；若 TextRange 可用，仅检查其前景色属性是否返回数值。

| 接口 | 有效采样结果 |
| --- | --- |
| UIA ElementFromHandle | 成功；ControlType 50033，0 子项 |
| Text / Text2 / TextChild | Pattern 10014 / 10024 / 10029 均返回空对象；调用未抛错 |
| Styles | Pattern 10025 返回空对象 |
| LegacyIAccessible / Selection / SelectionItem | Pattern 10018 / 10001 / 10010 均返回空对象 |
| UIA ElementFromPoint | `0x80070005`，E_ACCESSDENIED；未尝试提权 |
| MSAA AccessibleObjectFromPoint | 获得客户端对象，role=10（ROLE_SYSTEM_CLIENT），child=SELF，0 子项 |
| MSAA 客户端对象 | role=10，child=SELF，0 子项 |
| QueryInterface → IServiceProvider | `0x00000000` |
| QueryService → IAccessibleEx | `0x80070057`，E_INVALIDARG；未取得接口 |
| TextRange 前景色属性 | 未执行，原因是没有 Text/Text2 range |

实现中修正了初版只枚举装饰窗原生子窗口的遗漏：该版本未命中窗口的运行不计入接口证据。后续一次命中后因点位查询的 UnauthorizedAccessException 中断；最终版本记录该 HRESULT 并继续查询独立 MSAA 路径。以上结果取自最终版本的实际候选采样。独立构建通过，0 警告、0 错误。

最终 90 秒运行记录 69 次可见候选窗口采样、345 次窗内点位采样，错误输出为空。69 次 UIA 窗口查询均无上述 patterns，345 次 UIA 点位查询均为 E_ACCESSDENIED，MSAA 点位均为无子项的客户端对象。探针正常结束。

结论：本次测试的公开接口没有提供候选文字节点、文字范围及颜色属性，macOS reader 的亮度分组算法仍缺少 Windows 输入数据。接口语义参考 [UIA 文字属性](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-textattributes) 和 [IAccessibleEx 客户端服务查询](https://learn.microsoft.com/en-us/windows/win32/winauto/using-iaccessibleex-from-a-client)。

## 微信输入法当前高亮

微信输入法 3.0.2.22 不把候选项放进 UIA 或 MSAA 树，候选窗 `WeType.NativeCandidate.Direct2D.v2` 只有根节点。候选数据在**正在输入的那个界面线程**的 TSF 里：`TF_GetThreadMgr` 取得的 `ITfUIElementMgr` 能枚举到描述为「微信输入法输入候选窗口」的 `ITfCandidateListUIElement`，`GetSelection` 和 `GetString` 给出当前高亮。这个接口标记为 `[local]`，别的进程不能直接调用。

Glint 只在看见 `wetype_renderer` 的候选窗、并且前台不是密码框、终端或高权限窗口时，才把 `WeTypeSink.dll` 短暂挂到 64 位界面线程的窗口过程。前台窗口和编辑焦点可以在同一进程的不同线程上。Glint 通过 `GetGUIThreadInfo` 找到焦点窗口，向它发送用于采样的 `WM_NULL`，并在拥有它的线程上装钩子和查询快照。线程内读取当前高亮的那一项，写入当前用户会话的内存映射 `Local\Glint.WeTypeCandidate.v1`。卸下钩子后，同一线程用 10ms 定时器继续刷新；候选结束后清空文字并停止。

2026-11-14 独立进程验证：一个 WinForms 文本框用 `TF_IPPMF_FORPROCESS` 启用微信输入法，输入 `nihao` 后按两次右方向键。另一进程只通过上述桥接读到高亮依次为「你好」「👋」「拟好」。译文栏仍要求候选含中文，因此纯表情高亮不会出译文。快照解码和桥接模块加载已进入 Windows 自检。

### Windows x64 上的 32 位企业微信

2026-11-18 在企业微信 5.0.8.6009、微信输入法 3.0.2.44 上运行结构探针。聊天候选可见时，前台窗口和焦点窗口均为 `0x2F91BC8`，PID 63612、线程 57588，`IsWow64Process2` 返回 x86（`0x014C`）。焦点属于企业微信主进程，根窗口等于前台窗口，候选窗 PID 为 10408。另一次采样看到 `updated_web` 的 64 位子进程获得焦点，该时刻候选窗不可见。

同机复测公开接口采到 33 次候选窗。UIA 和 MSAA 都只返回根节点，未提供候选子项或文字范围。

64 位 Glint 按目标进程位数选择桥接。32 位路径仅接受系统标准安装目录中的企业微信主进程，要求焦点窗口属于其前台窗口，目标是同用户同会话内的普通权限进程，并根据 UIA 密码属性和原生窗口样式跳过密码框。Glint 按需启动并复用 x86 的 `WeTypeHook32.exe`。辅助进程连接到限制为当前用户的专用管道，双方验证对端的进程与会话。辅助进程用 `WeTypeSink32.dll` 在目标线程同步采样一次，再卸下钩子。两种架构使用相同的 264 字节布局，x86 写入 `Local\Glint.WeTypeCandidate.x86.v1`，每次取样结束后清空槽内文字。候选文字由 sink 通过 TSF 获取，辅助进程只回传操作状态。两条路径都不监听键盘、不读输入框正文、不落盘。[Windows 窗口钩子的位数要求](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowshookexw)

用 `.\Windows\scripts\wetype-wxwork-focus-probe.ps1 -Seconds 90` 复查焦点归属。自检验证 x86 辅助进程能加载 x86 DLL、回报架构并拒绝无效窗口。真机验收时，用微信输入法在企业微信聊天输入框输入 `nihao` 并停留，检查译文显示。用方向键切换到另一条中文候选，检查译文跟随。用空格提交、Esc 取消、点击输入框外或切换输入法，分别检查译文栏消失。再检查密码框和终端不显示译文栏，最后在 64 位记事本重复候选显示、切换与取消。

2026-11-18 真机验收通过企业微信的候选显示、方向键切换及上述四种结束操作，Windows 终端和 WinForms 密码框未显示译文。64 位记事本回归发现前台窗口与编辑焦点的线程分别为 30508 和 36080，均属于 PID 48984。改用实际焦点线程后，确认快照由 36080 写入，记事本和企业微信均能显示译文并在 Esc 后收起。

## 豆包输入法

- 探测日期：2026-11-16。
- 豆包输入法：1.0.0.23。文本服务 `{9D2B2E2B-3C93-4D2F-9D35-6EEB85F0D2B0}`，语言配置 `{2B4D4B3A-4D4F-4C0A-8E66-7F771A2B9C10}`。
- 测试方式：独立进程用 `TF_IPPMF_FORPROCESS` 只在本进程启用豆包，在自有文本框输入 `nihao`。候选窗出现时文本框仍是空的，按 Escape 后宽候选窗隐藏。

先套微软拼音的公开候选树。组合期间 `ImeService` 有可见顶层窗 `OimeDirectUIWindow`，约 909×135。它没有子窗口。UIA 只有一个空 `Pane`，没有 `IME_Candidate_Window`，没有子节点，窗口标题和 Name 都是空的，也没有 TextPattern。MSAA 客户区子项数是 0。窗内点位查询没有返回候选项。

再套微信输入法的线程内候选接口。输入线程上 `TF_GetThreadMgr`、`ITfUIElementMgr` 和 `EnumUIElements` 都成功，但枚举到的只有描述为 `Doubao IME Reading Info` 的元素。对 `ITfCandidateListUIElement` 的 QueryInterface 返回 `0x80004002`。候选窗保持可见时仍然如此。`ImmGetCandidateList` 返回 0 字节。安装目录里的 `tsf-oime-core.dll` 有 `OimeCandidateListUIElement` 类型，这次组合没有把它登记到输入线程的 UI 元素管理器。

候选窗属于 `ImeService` 的界面线程。对该线程安装 `WH_CALLWNDPROC` 后 `SendMessageTimeout` 返回成功，但钩子过程没有进入该进程，因此没有读到这个线程自己的 TSF。不把这次未进入的调用当成“线程上没有候选接口”。

`\\.\pipe\ObricIme\oime-server` 只等不发请求时没有字节。输入法安装目录里的 `doubaoime-rpc-new.dll` 导出 `RpcPipe_GetCandidateListUtf8`。它的第一个参数是输入法自己用来标识这条管道的名字，后面是缓冲区、容量和高亮序号。返回值是 UTF-8 字节数，正文按行分隔，序号对应当前高亮。

2026-11-16 独立进程验证：一个 WinForms 文本框用 `TF_IPPMF_FORPROCESS` 启用豆包，输入 `nihao`。本进程读到高亮「你好」；按一次右方向键后序号变为 1。另一个没有焦点的进程用同一调用读到同一份列表，序号仍是按键后的高亮。候选窗约 909×135，按 Escape 后隐藏。纯表情高亮仍不显示译文。

Glint 只在看见 `ImeService` 里足够宽的 `OimeDirectUIWindow`，并且前台不是密码框、终端或高权限窗口时，才调用这个只读函数。不调用按键、提交、剪贴板或其他管道命令，不读取输入框正文。取不到高亮时不猜测第一项。32 位目标进程不在这条路径里；读取发生在 Glint 自己的进程。
