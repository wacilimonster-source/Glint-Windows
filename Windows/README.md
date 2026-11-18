# Glint Windows（非官方移植）

这是独立维护的非官方 Windows 移植，与上游 [jsogn/Glint](https://github.com/jsogn/Glint) 没有隶属或官方背书关系。macOS 原版的网站、发布页和 Homebrew 不是本移植的下载或支持渠道。

Glint 给中文母语者边输入边看译文。你继续用微软拼音、微信输入法或豆包输入法打字，它不替换输入法，也不读取输入框正文。当前高亮的拼音候选停住后，英语、日语或韩语译文出现在候选栏旁边。单词、短语和短句都可以看；译文可以朗读，也可以插入到正在输入的位置。候选原文和翻译结果都不会保存。

本目录根据原项目源码移植。保留原版中文设置界面、侧栏、单行翻译栏、离线词典、朗读与托盘交互。原 macOS 工程完整保留在仓库中。

## 使用

1. 完整解压 `Glint-0.1.6-win-x64.zip` 到固定目录，双击 `Glint.exe`。便携包已包含 .NET 运行环境，无需另装 .NET。
2. 在“关于”页面输入中文并点击“测试翻译”，或点击“预览翻译栏”检查外观。
3. 保持 Glint 运行，在记事本等 64 位普通窗口中用微软拼音或微信输入法输入 `nihao`，不要按空格。能确认当前高亮时，译文会显示在候选栏旁。
4. 需要短句翻译时，在“插件与模型”点击“准备本地模型”。首次联网下载后，OPUS-MT 完全离线运行，无需安装 Python。

关闭主窗口后继续在托盘运行，双击托盘图标可重新打开。从托盘菜单退出。

当前版本为 **Windows 移植预览版**。已验证离线词典、模型推理、设置、浅深色界面与程序生命周期；真实输入法的候选显示、切换及插入仍需人工验收，不能视为所有输入法均已兼容。

微软拼音使用 Windows 候选可访问性接口。微信输入法读取 64 位窗口中的当前高亮候选。32 位兼容路径仅用于 Windows x64 上默认安装位置的企业微信主进程。

## 快捷键

仅在存在当前有效候选译文时注册，其他时间释放，不监听普通键盘输入。

| 快捷键 | 操作 |
| --- | --- |
| `Ctrl + Alt + 1` … `9` | 插入对应序号的译文 |
| `Ctrl + Alt + S` | 朗读第一项译文 |

插入前会重新检查候选、焦点、密码状态和权限；先结束当前拼音组合，再使用 Windows Unicode 输入，不修改剪贴板。

## 支持范围

- Windows 10 22H2 / Windows 11，推荐 Windows 11 x64。实际构建与验证在 Windows x64 完成。
- 当前学习语言为英语。CC-CEDICT、ECDICT 随程序提供；OPUS-MT 可选安装。
- Windows ARM64 可从源码构建词典版本，尚未实机验证；可选模型运行环境目前仅支持 x64。
- 微软拼音依赖输入法公开的可访问性结构。微信输入法读取系统候选接口里的当前高亮。豆包输入法在候选栏出现时读取当前高亮。其他输入法没有明确高亮时不会推测候选。
- 跳过密码框、终端、不安全或无法确认的焦点、高权限窗口。
- Apple Translation、MLX、macOS 输入法桥接和 macOS 更新安装器不适用于 Windows。
- 磨砂效果随 Windows 版本、透明效果设置与远程会话而变化，不可用时回退到随主题变化的半透明背景。

## 本地模型

安装器下载固定版本的 Python embeddable、CTranslate2、SentencePiece 等运行库以及原项目的 OPUS-MT int8 数据。所有下载校验 SHA256，模型同时逐文件校验，并在成功完成真实推理后标记就绪。下载和翻译分别运行，候选文本不会进入下载进程。

如本机缺少 C++ 运行环境，模型准备可能失败。安装 [Microsoft Visual C++ x64 Redistributable](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist) 后重试。

偏好和可选模型位于 `%LOCALAPPDATA%\Glint`，不会写入输入历史。删除该目录可清除偏好和下载数据；应用目录中的两部词典保持只读。

## 从源码运行

需要 Windows 和 .NET 10 SDK。在仓库根目录使用 PowerShell：

```powershell
.\Windows\scripts\build.ps1
.\Windows\scripts\test.ps1
.\Windows\scripts\run.ps1
```

`run.ps1` 在构建成功后请求旧实例退出，等待新实例完成词典准备并报告就绪。测试报告与各页面渲染图位于 `Artifacts\Windows\self-test`。

生成包含 .NET 的便携包：

```powershell
.\Windows\scripts\publish.ps1
```

离线构建环境可用 `publish.ps1 -UseInstalledRuntime`，将本机已安装的 .NET 10 桌面运行库完整打入便携包。

输出为 `Windows\dist\Glint-0.1.6-win-x64.zip` 和同名 `.sha256` 文件。

## 署名与许可证

Glint 源码遵循仓库根目录的 MIT License，版权声明仍为 `Copyright (c) 2026 Jhon_Walker`。词典、模型和运行库的许可见 [第三方许可](../THIRD-PARTY-NOTICES.md)。详细实现、平台差异和验收清单见 [Windows 移植说明](../docs/WINDOWS-PORT.md)。
