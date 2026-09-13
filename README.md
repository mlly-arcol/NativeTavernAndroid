# NativeTavern Android

NativeTavern 的安卓版（.NET MAUI 原型阶段）。目标：与 Windows 桌面版（[NativeTavern](https://github.com/mlly-arcol/NativeTavern)，WPF + .NET 10）共享业务核心，只重做移动端界面。

## 技术方案

| 部分 | 技术 |
| --- | --- |
| 界面 | .NET MAUI 10（当前仅 net10.0-android）+ XAML |
| 公共业务逻辑 | `NativeTavern.Core` 类库（**链接引用**桌面仓库源码，不复制） |
| AI 通信 | 共享 HttpClient + SSE 流式（OpenAI 兼容 / Claude，ProviderRouter 分发） |
| 聊天记录 | SQLite + Dapper，仓储与桌面端完全同源，schema 自动建表/迁移 |
| Markdown | Markdig（与桌面端同管线），解析后渲染为 MAUI 原生控件 |
| API Key | MAUI `SecureStorage`（Android Keystore），替换桌面 DPAPI 实现 |

## 项目结构

```text
NativeTavernAndroid/
├── NativeTavern.Android.sln
└── src/
    ├── NativeTavern.Core/        # 平台无关业务核心（链接 E:\NativeTavern 源码）
    │   └── NativeTavern.Core.csproj
    └── NativeTavern.Maui/        # 安卓 App（聊天原型）
        ├── MauiProgram.cs        # DI 组装 + 数据目录重定向 + 建库
        ├── App.xaml              # 应用资源
        ├── Views/                # ChatPage（聊天）、CharactersPage（角色库）、
        │   │                     # SettingsPage（设置）、MarkdownContentView（消息 Markdown 渲染）
        ├── ViewModels/           # MAUI 专用 VM（CommunityToolkit.Mvvm）
        ├── Security/             # SecureStorage 版 ISecretProtector
        ├── Platforms/Android/    # MainActivity、AndroidManifest
        └── Resources/            # 图标、启动图、样式
```

### 共享源码机制（重要）

`NativeTavern.Core.csproj` 通过 `<Compile Include="..\..\..\NativeTavern\...">` **直接链接**桌面仓库中的平台无关文件，因此克隆时两个仓库必须位于同一父目录下：

```text
parent/
├── NativeTavern/          # git clone https://github.com/mlly-arcol/NativeTavern
└── NativeTavernAndroid/   # git clone https://github.com/mlly-arcol/NativeTavernAndroid
```

链接范围：

- `Models/`、`Providers/`、`Data/`（含 schema.sql 内嵌资源）、`Importers/`
- Services：ChatService、CharacterService、CharacterStatusService、ReplySuggestionService、PromptService、AttachmentService、ConversationSummaryService、SettingsService、PluginService、KnowledgeService、BackupService、DataExportService 等
- Helpers：AppPaths（已支持 `UseRoot` 覆盖数据根目录）、AppVersion、JsonDefaults、ManagedFile、ProgressiveParagraphBuffer、LanguageCodes

**规则：修 bug / 改逻辑一律改 `NativeTavern` 桌面仓库中的源文件，两端同时生效；不要往 Core 里复制文件。**
桌面 WPF 专用代码（ViewModels、Views、LocalizationService、DpapiSecretProtector、TrayService、MarkdownViewer）不参与链接。

桌面端为此做了两处向后兼容小改动：

1. `AppPaths.UseRoot()` — 允许移动端把数据目录重定向到 Android `FileSystem.AppDataDirectory`；桌面端不调用，行为不变。
2. `Helpers/AppVersion.cs` + `Helpers/LanguageCodes.cs` — 把版本号与语言常量从 WPF `App`/`LocalizationService` 中解耦，供共享服务使用。

## 构建

前置条件（一次性，已完成）：

```powershell
dotnet workload install maui-android
```

```powershell
# 只验证业务核心
dotnet build src/NativeTavern.Core/NativeTavern.Core.csproj

# 构安卓 APK（首次构建会自动下载 Android SDK 组件，需要网络）
dotnet build src/NativeTavern.Maui/NativeTavern.Maui.csproj -c Debug -f net10.0-android
```

产物：`src/NativeTavern.Maui/bin/Debug/net10.0-android/` 下的 `NativeTavern.Android.apk`。
真机安装：开启 USB 调试后连接手机，运行 `dotnet build -t:Install` 或 `adb install <apk>`。

## 原型范围（第一版）

已搭好骨架并预留：

- 在线 AI 聊天、流式输出、随时停止
- 会话保存、切换、删除（共享 SQLite 仓储，与桌面端数据库结构一致）
- 每条回复后生成 3 个剧情回复建议
- 角色动态状态插件能力的调用链（安装对应 `.ntplugin` 后生效）
- 设置页：Provider 模板、Base URL、API Key（SecureStorage）、模型、生成参数、连接测试

## 0.2.0 新增

- **角色库（角色页）**：从聊天页 🎭 按钮进入；导入 SillyTavern 格式角色卡（PNG 内嵌 / JSON，走共享 `CharacterCardImporter`，PNG 卡自动截取头像）；删除角色；一键以该角色开新对话（角色开场白自动插入）
- **聊天 Markdown 渲染**：助手消息用 `MarkdownContentView` 渲染（Markdig 解析 → MAUI 原生控件），支持标题、粗斜体、删除线、行内代码、代码块（横向滚动）、列表、引用、表格、分隔线与可点击链接；流式输出按 55ms 防抖重建，与桌面端 `MarkdownViewer` 行为对齐

待接入（下一步）：

1. 图片附件、群聊、世界书/Persona/Preset 绑定弹窗
2. 第二阶段：本地 GGUF 推理（需要安卓端推理引擎适配，先测内存/发热/耗电）

## 验证重点（真机）

长对话滚动流畅度、流式输出节流表现、输入法弹出/收起时的布局（`Editor.AutoSize` + 键盘遮挡）。

0.2.0 另需验证：文件选择器在不同 ROM 上对 `.json` 的可见性（已放宽 MIME 兜底）；Markdown 长代码块横向滚动与流式重建的流畅度；PNG 角色卡头像截取是否正常。
