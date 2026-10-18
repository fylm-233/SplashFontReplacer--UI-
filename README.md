# 多合一启动图字体 UI 替换器 / SplashFontReplacer

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Release](https://img.shields.io/badge/Release-v1.0.0-blue.svg)](https://github.com/fylm-233/SplashFontReplacer-多合一启动图字体UI替换器/releases)
[![Issues](https://img.shields.io/badge/Feedback-Issues-green.svg)](../../issues)

> **English version: [README_EN.md](README_EN.md)**

把三个原本分散的替换工具整合成一个 WinUI 3 桌面程序，并打包为单一可执行文件：

| 模块 | 目标 | 手段 |
| --- | --- | --- |
| 达芬奇启动图 | `Resolve.exe` 内嵌 PNG | 等长重建 PNG 覆盖资源块，文件大小与 PE 结构不变 |
| 启动图（Adobe 等） | Adobe 全系 + Unreal / Maya / 3ds Max / JetBrains 全家桶 / VS 2022 / Live2D 的启动画面 | 四条写回通道统一调度：PE 资源（`UpdateResource`）、松散图片文件、容器内嵌图片（等长原地覆盖）、jar/zip 条目；同一张图的多分辨率变体自动成组 |
| 达芬奇 UI 字体 | `platforms\qwindows.dll` | 等长改写 Qt 字体回退表中的字体族名 |
| Adobe 字体 | `Dictionaries\<locale>\<app>_<locale>.dat` | 重建 FontStyle 字体键（字号+字体），并同步 `signatures.xml` 摘要；覆盖 AE / PR / ME / AU 等，CS6–2026 自适应 |

各模块独立备份、独立还原，互不影响。界面用左侧导航明确分区：**达芬奇启动图 / 启动图（Adobe 等）/ 达芬奇 UI 字体 / Adobe 字体 / 关于与说明**。

### 相关文档

- 完整使用说明（安装、操作、FAQ、未测试项）：[`docs/使用说明.md`](docs/使用说明.md)
- Adobe 字体机制、版本差异与适配要点：[`docs/Adobe字体替换整合方案.md`](docs/Adobe字体替换整合方案.md)
- Adobe 启动图可行性论证：[`docs/Adobe启动图替换可行性分析.md`](docs/Adobe启动图替换可行性分析.md)
- 闭源工具逆向还原与重写记录：[`docs/闭源工具实现还原与重写记录.md`](docs/闭源工具实现还原与重写记录.md)

---

## 1. 下载与运行

从 [Releases](https://github.com/fylm-233/SplashFontReplacer--UI-/releases/latest) 下载：

| 文件 | 说明 |
| --- | --- |
| `多合一启动图字体UI替换器.exe` | 单文件版，双击即用 |
| `多合一启动图字体UI替换器-目录版.zip` | 目录版，解压后运行，适合不喜欢自解压的用户 |

运行环境与已验证测试环境见 Release 说明，简要地说：**Windows 10 1809（17763）及以上 x64，无需安装 .NET 或 Windows App Runtime**。

**免责声明**：本工具会修改 Adobe / DaVinci / JetBrains 等商业软件的资源文件，属于非官方修改。使用风险由使用者自行承担，请勿用于任何违反软件最终用户许可协议（EULA）的用途。操作前请退出目标软件，并务必备份原始文件（工具默认会自动备份）。仅可在合法持有授权的软件上使用。

---

## 2. 技术栈

| 层 | 技术 |
| --- | --- |
| UI 框架 | WinUI 3（Windows App SDK） |
| 语言 / 运行时 | C#，.NET 8（自包含发布，目标框架 `net8.0-windows10.0.19041.0`） |
| 系统互操作 | P/Invoke：`BeginUpdateResource/UpdateResource/EndUpdateResource`（PE 资源）、Win32 公共对话框、`CheckSumMappedFile`、文件映射 |
| 图像处理 | WinRT `Windows.Graphics.Imaging` 解码 / 缩放 / 编码（PNG / BMP / JPEG） |
| 图像重打包 | 自研 PNG 裸重打包：zlib 重压（level 9）、扫描线自适应重过滤、有损降级阶梯（透明归一化 → 6/5/4 位量化 → 丢 alpha）、私有 ancillary 块等长补齐 |
| 打包分发 | 「完整目录 + 内嵌 payload 的单文件自解压启动器」（非 `PublishSingleFile`，原因见下） |
| 测试 | 自研控制台自测工程 `tools/stest`（95 项断言）+ `imgtest` / `atest` / `verify` / `dlgprobe` |

### 为什么不是 `PublishSingleFile=true`

Windows App SDK 自包含部署要求 **exe 同目录**存在 WinRT 激活清单（`Microsoft.UI.Xaml` 等 `activatableClass` 项）与对应 DLL。`PublishSingleFile` + `IncludeAllContentForSelfExtract` 会把运行时解压到临时目录，但进程路径仍是原始 bundle exe，WinRT 激活工厂按清单找不到 DLL，启动即抛 `0x80040111 ClassFactory 无法响应来自 COM 的请求`。

因此改为单文件自解压启动器：首次运行把 payload 解压到 `%LOCALAPPDATA%\MultiReplacer\app\<构建标识>\` 并从该目录拉起主程序，构建标识取自程序集 MVID，重新构建自动换目录并清理旧目录。**目标机器不需要安装 .NET 运行时或 Windows App Runtime。**

---

## 3. 从源码构建

### 环境要求

- .NET 8 SDK（`dotnet --list-sdks` 需包含 8.x）
- Windows 10 SDK（Windows App SDK NuGet 包会自动处理）
- 可选：Python 3（仅用于更高压缩率地打 payload）

### 一键构建

```cmd
build.cmd
```

或

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

产物位于 `build\dist\`，与 Release 提供的内容一致。

只重新打包启动器（应用未改动时，可跳过约 20 秒的应用发布）：

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1 -SkipAppPublish
```

### 构建脚本内部做的四步

1. `dotnet publish src\MultiReplacer.App -c Release -r win-x64 -p:Platform=x64 -p:DebugType=none -o build\app` —— 得到完整应用目录（含 Windows App SDK 自包含运行时）。
2. 把 `build\app` 压缩为 `src\MultiReplacer.Bootstrapper\payload.zip`。
3. `dotnet publish src\MultiReplacer.Bootstrapper -c Release -r win-x64 -o build\dist` —— 得到内嵌 payload 的单文件 exe。
4. 另存一份目录版 zip。

> 图标 `src/MultiReplacer.App/Assets/app.ico` 不入库，首次构建前如缺失，运行 `python tools/make-icon.py` 生成。

### 运行自测

```cmd
dotnet tools/stest/bin/Release/net8.0-windows10.0.19041.0/stest.dll
```

覆盖 PNG/BMP 嗅探、四条写回通道、已安装软件枚举、原生对话框过滤器、真实 Photoshop 2026 / After Effects 2026 实测，以及导入链路回归（原图往返像素一致性、超体积图有损降级、不可压缩图拒绝策略）。

---

## 4. 使用方式

完整图文说明见 [`docs/使用说明.md`](docs/使用说明.md)。快速上手：

### 启动图（Adobe 等）

1. 打开页面自动列出已安装软件，选中即自动填入目录并扫描；
2. 扫描结果按「疑似启动图」打分排序，同一张图的多分辨率变体自动成组；
3. 选中后在右侧看原图；点「选择新的 png…」挑替换图（PNG/JPG/BMP，按原资源尺寸等比裁切，可切换拉伸）；
4. 「应用替换」把同组变体一起写入（自动请求管理员权限），成功后自动重扫刷新；
5. 「还原备份」随时回滚。

操作前请**完全退出目标程序**。首次应用会生成 `.bak` 原始备份，可随时还原。

容器内嵌通道（如 Photoshop 的 `PSIconsHighRes.dat`）要求替换图与该区域**等长**，工具会通过重压/重过滤/有损降级把新图重新压进固定长度；若实在压不进去会明确报出「最省的一版还有多少字节、超出百分之几」，据此换一张更简单的图即可。

### Adobe 字体（AE / PR / ME / AU 等）

1. 自动检测已安装 Adobe 应用，也可手动指定 `Dictionaries` 目录；
2. 选字体族与大/中/小三档字号；
3. 「应用」以首次备份为基线重建语言包，并同步签名摘要；
4. 「恢复官方语言包」回滚。

### 达芬奇启动图 / 达芬奇 UI 字体

分别指定 `Resolve.exe` 与 `platforms\qwindows.dll`，扫描后选择目标资源写入。更详细的界面说明与注意事项见[使用说明文档](docs/使用说明.md)。

---

## 5. 反馈与贡献

发现 Bug 或有功能建议，请到 [Issues](../../issues) 提交。仓库已配置 issue 模板，提交时请尽量附上：

- **复现步骤**（如何操作、点了什么）
- **操作系统版本**
- **运行环境**（程序版本、目标软件与版本、是否以管理员身份运行）
- **错误日志**（`%LOCALAPPDATA%\MultiReplacer\logs\` 下的当天日志）

欢迎 PR：请保持每个模块「独立备份、独立还原」的边界，不要合并任意两个写回通道。

---

## 6. 开源协议

[MIT](LICENSE)。项目会修改第三方商业软件的资源文件，仅限个人在合法授权的软件上使用。
