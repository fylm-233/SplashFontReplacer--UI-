# Adobe 字体替换整合方案与实现说明

> 目标：把"Adobe 系字体替换"整合进现有的「多合一启动图字体UI替换器」，覆盖 CS 时代到 2026，
> 并让字体选择交互与达芬奇页保持一致、启动图行为与达芬奇启动图替换器保持一致。

---

## 0. 关于参考教程的说明（必须先看）

给定链接 `https://www.pcmoe.net/forum.php?mod=viewthread&tid=91246` 在本机被站点的 JS 安全校验拦截
（返回的是"安全验证 / 页面重载开启"空壳页，无正文；Wayback 也没有存档）。

从补充的整页截图可以判读出的信息：

- 标题为「**【教程】Adobe 全家桶等软件启动图修改**（更新）详细教程」，带"精华"章。
- 正文示例图是 **JetBrains 系列（Rider / PyCharm / GoLand / WebStorm / IntelliJ）** 与
  **Visual Studio 2022** 的自定义启动图，底部才是「Adobe 全家桶」章节。
- 中段是一张"软件 / 位置 / 文件"对照表，列出各软件启动图所在的文件（如 `*.bmp`、`*.png`、`*.rsrc`）。

也就是说：**该教程讲的是"启动图（splash）修改"，不是"字体替换"**。
截图分辨率仅 393×1920，表格里的路径文字无法辨识（放大 8 倍仍是糊的），因此无法据此还原
Adobe 各版本启动图的确切资源路径。

本方案的取舍：

1. 按你的需求文字，**先实现 Adobe 字体替换**（本文第 1–5 节），这是可验证、可交付的部分；
2. 启动图模块**保持与达芬奇启动图替换器完全一致的行为**（第 6 节），不猜测 Adobe 的资源路径；
3. 若你确实需要"Adobe 启动图替换"，请提供表格那一屏的清晰截图（或文字），我按同样的等长替换逻辑补上（第 7 节）。

---

## 1. 机制原理

Adobe 多数桌面产品（After Effects / Premiere Pro / Media Encoder / Audition / Prelude /
Character Animator）的中文界面字体来自 **AIR/UCF 语言包**：

```
<安装目录>\Dictionaries\<locale>\<app>_<locale>.dat
<安装目录>\Support Files\Dictionaries\<locale>\...      ← AE 这类带 Support Files 的产品
```

`.dat` 是纯文本键值表，字体相关的两类键：

```
"$$$/AE/FontStyle/Large=12 plain STSong{{*MSWindows*}}10 plain SimSun"
 ^键名                              ^Mac 侧：字号 样式 字体   ^平台分隔符  ^Windows 侧
"$$$/AE/Basic_Text/LStr/0008=STSong{{*MSWindows*}}SimSun"          ← 插件自绘界面字体
```

- 值格式：`<字号 2 位> <样式 plain|bold|italic> <字体族名>`；
- `{{*MSWindows*}}` 是 Adobe 语言包的跨平台分隔符，左边 Mac、右边 Windows；
- 部分键（如 `FontStyle/SmallItalic`、`FontStyle/Monospace`）只有单平台值，没有分隔符。

`.dat` 通常位于一个 **UCF 签名包**里：

```
Dictionaries\zh_CN\
├─ mimetype                       application/vnd.adobe.air-ucf-package+zip
├─ after_effects_zh_CN.dat        实际语言包
└─ META-INF\signatures.xml        XML-DSig + XAdES 签名
```

`signatures.xml` 里有三条 Reference：`#PackageContents`（签名自身）、`mimetype`、`<dat 文件名>`。
改了 `.dat` 必须同步第三条的 `DigestValue`，否则 Adobe 判定语言包损坏。
最外层的 RSA `SignatureValue` 无法伪造，但 Adobe 只校验**清单层摘要**，所以同步摘要即可生效
（这一点在之前的 AE 项目里已被实测验证）。

---

## 2. 版本差异与适配要点

**核心设计：不硬编码版本号，全部按文件实际内容探测。** 这样 CS 到 2026 用同一套代码，
新版本出来也不会失效。

| 差异点 | 版本表现 | 本工具的探测方式 |
| --- | --- | --- |
| 语言包位置 | AE 在 `Support Files\Dictionaries`；PR/ME/AU 多在 `<App>\Dictionaries` | 注册表 `SOFTWARE\Adobe\<产品>\<版本>\InstallPath` + 磁盘扫描，逐层找 `Dictionaries`（含 `Support Files` 变体） |
| 文件名 | `after_effects_zh_CN.dat`、`premiere_zh_CN.dat`… | 按 `*_<locale>.dat` 匹配，同目录取体积最大的 |
| 语言目录名 | `zh_CN` / `zh_TW` / `ja_JP`… | 优先 `zh_CN`，否则取第一个含有效 `.dat` 的目录，并在界面标出 |
| 编码 / BOM | 有的带 UTF-8 BOM，有的不带 | 读前 3 字节判断，写回时保持一致 |
| 换行 | CRLF / LF 混用 | 检测首个换行序列，按原样写回 |
| 平台分隔符 | CC 之后基本都有 `{{*MSWindows*}}`；早期 CS 可能只有单平台值 | **逐键判断**：值里有分隔符就只重写 Windows 侧并保留 Mac 侧；没有就整值写成 `字号 样式 字体` |
| 键名前缀 | `$$$/AE/…`、`$$$/PR/…`、`$$$/MediaCore/…` | 正则 `^\$\$\$/[A-Za-z0-9_/]*FontStyle/(Large|Medium|Small)(Bold|Italic)?$`，不认死前缀 |
| 字体键数量 | AE 2026 有 9 条（8 双平台 + SmallItalic 单平台） | 扫描后统计并显示，有几条改几条 |
| 插件字体键 | `STSong{{*MSWindows*}}SimSun` 形态，各版本键名不同 | 按"值形态 + Windows 侧是常见中文字体"识别，不再依赖固定键名清单 |
| 签名 | CS6 及更早可能没有 `META-INF`；CC 之后有 | 有签名才同步摘要；无签名直接跳过 |
| 摘要算法 | 早期 SHA-1，CC 之后 SHA-256（个别 SHA-512） | 读 `<DigestMethod Algorithm="…">`；读不到则按 `DigestValue` 长度回退（28=SHA-1 / 44=SHA-256 / 88=SHA-512） |

参考对照（用于人工排查，代码不依赖它）：

| 世代 | 目录结构 | 值格式 | 签名 |
| --- | --- | --- | --- |
| CS4–CS5.5 | `Dictionaries\<locale>\*.dat` | 多为单平台 | 常无 `META-INF` |
| CS6 | 同上 | 单/双平台混用 | 部分有，SHA-1 |
| CC / CC 2014–2019 | 同上（AE 起用 `Support Files`） | 双平台 | 有，SHA-1 / SHA-256 混用 |
| 2020–2026 | 同上 | 双平台 | 有，SHA-256 |

> 表中"CS 世代"部分是按同族机制推断的，本机只有 AE 2026 可实测；工具对这两种形态都有对应分支，
> 遇到老版本会在界面上把实际探测到的格式（单/双平台、有无签名、算法）显示出来，便于核对。

---

## 3. 覆盖范围

| 软件 | 状态 | 说明 |
| --- | --- | --- |
| After Effects（CS4–2026） | ✅ 已实测 | 本机 AE 2026 完成"应用 → 校验 → 还原"全流程 |
| Premiere Pro / Media Encoder / Audition / Prelude / Character Animator | ✅ 按同族机制支持 | 同为 AIR 语言包 + FontStyle 键；本机未安装，靠探测逻辑自适应 |
| Photoshop / Illustrator / InDesign / Animate / Dreamweaver | ❌ 不在范围 | 它们的界面字体不在这类 `FontStyle` 语言包里；强行改字符串表只会改文案、不会改字体，甚至导致界面异常 |
| 未安装任何 Adobe 产品 | — | 页面显示"未检测到"，仍可"手动选择…"指定 `Dictionaries\zh_CN` 目录 |

其他软件（JetBrains、Visual Studio 等）在教程里是**启动图**章节，不属于字体替换，未纳入。

---

## 4. 界面：与达芬奇页保持同一套交互

「Adobe 字体」页与「达芬奇 UI 字体」页同构，用户不需要重新学：

| 交互 | 达芬奇页 | Adobe 页 |
| --- | --- | --- |
| 目标选择 | qwindows.dll 路径 + 浏览 + 检测状态 | 应用下拉（自动检测）+ 重新检测 + 手动选择 |
| 状态提示 | 顶部 InfoBar（成功/警告/错误） | 同 |
| 候选列表 | 左栏：字体族名 / 状态 / 字节 / 说明，支持搜索，推荐置顶 | 完全一致 |
| 选中反馈 | 单选，右侧显示当前字体、槽位信息 | 单选，右侧显示当前字体、字号、键数、签名算法 |
| 确认 | ContentDialog 二次确认，列出目标文件与备份路径 | 同 |
| 结果 | 日志区逐条追加 + 完成弹窗提示"重启生效" | 同 |
| 还原 | "恢复字体备份" | "恢复官方语言包" |
| 备份基线 | 首次应用时创建，之后每次从基线重建 | 同（避免反复叠加） |

差异仅在于 Adobe 页多了一组**字号**（大/中/小）与一个「同时替换插件对话框字体」开关——
这是 Adobe 语言包本身的能力，不改变交互流程。

---

## 5. 实现与验证

### 代码位置

```
src/MultiReplacer.Core/Adobe/AdobeDictionary.cs   发现 / 分析 / 重建 / 摘要同步 / 部署还原
src/MultiReplacer.App/Views/AdobePage.xaml(.cs)   界面
```

关键 API：

```csharp
List<AdobeAppInfo> apps = AdobeDictionary.Discover();          // 注册表 + 磁盘
AdobeDictionary.Analyze(app);                                  // 填充键数/当前字体/摘要算法
AdobePatchSummary s = AdobeDictionary.Prepare(app, backupRoot, options, out error);
AdobeDictionary.Deploy(s.StagingDirectory, app);               // 需要时自动提权
AdobeDictionary.Restore(backupDir, app);
```

### 实测结果（本机 AE 2026，沙盒副本，未改动真实安装）

```
发现 1 个可处理应用
产品   : After Effects / 2026
语言包 : ...\Support Files\Dictionaries\zh_CN\after_effects_zh_CN.dat
签名   : True  算法: sha256
字体键 : 9  插件键: 9
当前   : SimSun  10/9/9
改写样式键 9，插件键 9，摘要 sha256
应用后 : Microsoft YaHei UI  16/14/9
Large  : "$$$/AE/FontStyle/Large=12 plain STSong{{*MSWindows*}}16 plain Microsoft YaHei UI"
摘要已写入签名文件: True
还原后 : SimSun  10/9/9
```

说明：Mac 侧 `12 plain STSong` 原样保留，只重写 Windows 侧；文件长度变化（+217 字节）对 `.dat` 无影响
（它不是等长约束的二进制补丁）。

---

## 6. 启动图模块：与达芬奇启动图替换器一致

启动图模块**未改动**，仍是达芬奇启动图替换器的那套行为：

扫描内嵌 PNG → 按"仅启动图 / 仅 Logo / 最小边长 / 关键字"筛选 → Shift/Ctrl 多选 →
选替换图 → **按目标资源长度等长重建 PNG**（重编码 IDAT，必要时缩放/调色深，凑到精确字节数）→
应用（写回 exe，PE 结构与文件大小不变）→ 备份/还原。

Adobe 侧为什么没有纳入启动图：实测 `AfterFX.exe` 里只有 1 个 256×256 的 PNG（图标），
Adobe 的启动画面不是 exe 内嵌 PNG，而是各自独立的图片/资源容器。用"扫描内嵌 PNG"的引擎去套，
会得到错误结论，所以宁可不做。

---

## 7. 待确认

1. 若你需要的是**Adobe 启动图替换**（教程正文那一块），请提供"软件 / 位置 / 文件"那张表的清晰截图。
   拿到路径表后，可以复用现有等长替换引擎（PNG/BMP 双格式），预计新增一个"Adobe 启动图"入口。
2. 若 Adobe 字体替换还有你想覆盖的产品（比如 Photoshop 的界面字体），需要另找该产品的字体入口
   （PS 走 `Locales\zh_CN` 的字符串表，字体本身是程序内固定资源），这属于另一套机制，不在本方案内。
