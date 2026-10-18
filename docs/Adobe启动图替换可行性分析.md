# Adobe 启动图替换可行性再分析

> 依据：用户抓取的教程纯文字（软件 / 位置 / 文件 / 备注四列路径表）+ 本机 AE 2026 实测。
> 结论先行：**可行，但必须放弃「等长覆盖」路线**。教程里 Adobe 系的目标分成三类，只有一类需要动 PE 资源，另外两类要么更简单、要么不值得做。

---

## 一、结论（TL;DR）

| 判定 | 内容 |
|---|---|
| 总体可行性 | **可行**。教程列出的 10 个 Adobe 应用中，8 个能在不逆向任何私有格式的前提下实现 |
| 不需要 PE 手术的 | Premiere Pro、Illustrator、Media Encoder、Lightroom —— 启动图是**磁盘上的独立 PNG 文件**，直接覆盖即可，零风险 |
| 需要 PE 资源编辑的 | After Effects、Audition、Character Animator、Animate —— 启动图在 exe/dll 的**命名资源**里，需 `UpdateResource` 重建 |
| 建议不做的 | InDesign / InCopy（`.idrc` 私有容器）、Photoshop（`.idx + .dat` 分卷索引）—— 需逆向格式，收益低风险高 |
| 与达芬奇引擎的关系 | **不能复用「等长覆盖」**（`PngCodec.FitToExactLength`）。Adobe 的资源是独立资源项，允许变长，走 `UpdateResource` 反而更稳 |
| 签名影响 | AE 的 `AfterFXLib.dll` 实测**本来就是 `HashMismatch`**，改不改都一样；`AfterFX.exe` 是 `Valid`，但它不是我们的修改目标 |
| 唯一未闭环项 | 改完后 AE 启动时肉眼确认渲染生效（需实际启动一次 AE，无法静态验证） |

---

## 二、教程路径逐条研判

### A 类：独立 PNG 文件（直接覆盖，最稳）

| 软件 | 教程路径 | 技术判定 | 风险 |
|---|---|---|---|
| Premiere Pro | `<install>\PNG\pr_splash*.png` | 普通文件读写，无需解析任何结构 | 极低。仅需匹配宽高比，尺寸可任意 |
| Illustrator | `Support Files\Contents\Windows\ai_cc_splash*.png` | 同上 | 极低 |
| Media Encoder | `LAUNCHAMEBACKGROUND.png` | 同上 | 极低 |
| Lightroom | `LRC_SPLASH.PNG` | 同上 | 极低 |

这一类的价值被低估了：**Premiere Pro 是 Adobe 系里用户量最大的视频软件，而它恰好属于最简的一类**。只要做「按文件名模式扫描安装目录 → 预览 → 覆盖 → 备份/还原」，就能拿下 PR + AI + AME + LR 四个应用，完全不需要碰 PE 格式。

### B 类：PE 命名资源（需 `UpdateResource`）

| 软件 | 教程路径 | 技术判定 | 风险 |
|---|---|---|---|
| After Effects | `AfterFXLib.dll` → `PNG\AE_SPLASH.png` | **已实测可行**，见第三节 | 低 |
| Audition | `AuUI.dll` | 同一套 `UpdateResource` 流程 | 低 |
| Character Animator | `Character Animator.exe` | 同上，但改的是主 exe，需先备份 | 中（改主 exe 心理压力大） |
| Animate | `Animate.exe` | 同上 | 中 |
| 3ds Max / ACDSee / VS2022 | 各自 MUI/资源 DLL | 同上，属「其他软件」范畴 | 低 |

### C 类：私有容器（建议不做）

| 软件 | 教程路径 | 为什么不做 |
|---|---|---|
| InDesign / InCopy | `idrc_PNGA\400.idrc` 等 | `idrc` 是 InDesign Resource Container，内含 PNG 但带自己的目录/偏移表。要先逆出目录结构、重算偏移，任何一个字段算错就是软件启动崩溃。而 ID 的启动图用户关注度远低于 PR/AE |
| Photoshop | `IconResources.idx` + `PSIconsHighRes.dat` / `PSIconsLowRes.dat` | 索引文件记录每个条目的偏移与长度，`.dat` 是拼接的裸数据。改一张图要重写索引 + 整体重排 `.dat`。教程本身也是让人用**在线网页工具**处理，说明社区也没做出好用的本地工具 |

---

## 三、关键技术验证（AE 2026 实测）

目标文件：`C:\Program Files\Adobe\Adobe After Effects 2026\Support Files\AfterFXLib.dll`
文件大小 **49,345,032** 字节，PE32+，7 个节，`.rsrc` 位于 RVA `0x2490000`，共 **3015** 个资源条目。

### 3.1 教程说的路径是真的

扫描出的命名 PNG 资源（`type=PNG`，全部 `lang=1033`）：

```
AE_SPLASH                  766 x  516     369.8 KB
AE_SPLASH_AT_2X           1532 x 1032    1167.0 KB
AE_SPLASH_AT_3TO2X        1149 x  774     706.2 KB
AE_BETA_SPLASH             766 x  516     196.9 KB
AE_BETA_SPLASH_AT_2X      1532 x 1032     326.6 KB
AE_BETA_SPLASH_AT_3TO2X   1149 x  774     349.3 KB
```

Resource Hacker 里显示为 `PNG\AE_SPLASH`，与教程写法 `PNG\AE_SPLASH.png` 一致（后缀是 RH 的习惯性显示）。

**重要发现：存在三套 DPI 变体（1x / 1.5x / 2x），外加一整套 BETA 变体。**
只改 `AE_SPLASH` 是不够的——在 150% / 200% 缩放的屏幕上，AE 会去取 `_AT_2X` / `_AT_3TO2X`，用户会看到「改了一半」。所以工具必须**从用户选的一张图自动生成 3 个尺寸并同时写入**，这也是与达芬奇替换器最大的行为差异（达芬奇只有一张）。

### 3.2 `UpdateResource` 可行，且允许变长

用 Win32 `BeginUpdateResourceW` / `UpdateResourceW` / `EndUpdateResourceW` 在临时副本上实测：

| 用例 | 替换后大小 | 结果 |
|---|---|---|
| 小图替换（77,981 B） | — | 成功 |
| **超大图替换（2,161,323 B，原图仅 1,195,050 B）** | 文件 49,345,032 → 49,994,240 | **成功** |
| 兄弟资源 `AE_BETA_SPLASH` 是否完好 | — | **是** |
| `LoadLibraryExW(LOAD_LIBRARY_AS_DATAFILE)` | — | **成功**（PE 结构未损坏） |

这条最关键：**资源项可以自由变大变小**，不需要像达芬奇那样把 PNG 压到等长。达芬奇的等长约束来自「PNG 数据是嵌在连续资源块里的裸字节，改长度会错位」；Adobe 走标准资源树，`UpdateResource` 会自己重排 `.rsrc` 节并更新数据目录。

### 3.3 签名：不是阻碍

```
AfterFXLib.dll   Status=HashMismatch   ← 本来就是坏的
AfterFX.exe      Status=Valid
```

`AfterFXLib.dll` 在原始安装状态下签名就已经校验失败，所以「必须保持字节长度以维持签名」这条约束在这里**根本不成立**。反过来说，工具仍应在 UI 上明确提示「修改后该文件的数字签名将不再有效」，并默认先备份。

> 附注：安装目录里存在 7 个 `.bak`（时间戳均为 2026-10-17 17:14），其中 3 个崩溃上报程序的原文件已被删除、`AfterFXLib.dll` 在 `0xEF9E6C` 处有 3 字节补丁（`c3 32 c0 c3` → `90 b0 01 c3`，把布尔函数强制返回 true）。所以 `HashMismatch` 来自这次第三方授权补丁，不是 Adobe 安装器。
> 对本文结论没有影响（签名本来就不成立），但工具写入前必须自己生成带时间戳的备份，不能依赖这些来历不明的 `.bak`；`UpdateResource` 会保留 `.text` 段里已有的补丁字节。

### 3.4 `XML\AE_SPLASH` 不需要动

同目录下还有一个 `XML\AE_SPLASH` 资源，内容是：

```xml
<?xml version='1.0'?><prop.map version='4'>… dvaui::ui::UI_SubView …
```

这是启动窗口的**界面布局定义**（控件树），不是图片路径表，也不含尺寸常量。替换 PNG 时不用碰它。但要注意它的含义：**窗口尺寸由布局固定，所以替换图的宽高比必须与原图一致**（766:516 ≈ 1.484），否则会被拉伸或裁切。

---

## 四、三条技术路线对比

| 路线 | 做法 | 适用 | 结论 |
|---|---|---|---|
| ① 等长覆盖（现有达芬奇引擎） | 扫描 PNG 签名 → 重编码到精确字节数 → 原地写 | 仅限裸嵌 PNG | ❌ 不适用于 A/B 类。A 类是独立文件没必要，B 类是资源树会错位 |
| ② `UpdateResource` 重建 | 解析 PE 资源树 → `BeginUpdateResource` → 逐项 `UpdateResource` → `EndUpdateResource` | B 类 | ✅ **推荐**，已实测 |
| ③ 直接覆盖文件 | 读原图取尺寸 → 缩放/重编码 → 覆盖磁盘文件 | A 类 | ✅ **推荐**，最简最稳 |

**最终方案：按目标类型自动选路（②/③），①保留给达芬奇模块不动。**
用户视角完全无感——仍然是「扫描 → 选中 → 换图 → 应用 → 还原」，只是底层按目标自动切换引擎。

---

## 五、风险清单与对策

| 风险 | 影响 | 对策 |
|---|---|---|
| 目标程序正在运行，文件被占用 | 写入失败 | 复用 `Patcher.IsProcessRunning`，扩展进程名表（AfterFX / Adobe Premiere Pro / Adobe Media Encoder / Audition / Animate / Character Animator / Lightroom） |
| 改主 exe（CH / AN）后无法启动 | 软件不可用 | 强制备份 + 「一键还原」按钮 + 写入后 `LoadLibraryEx(AS_DATAFILE)` 结构自检 |
| 只改了 1x，高分屏没变 | 用户以为失败 | 自动生成全部 DPI 变体；UI 明示「将写入 N 个尺寸」 |
| 宽高比不符被拉伸 | 观感差 | 应用前按原图比例做居中裁切（cover）而非拉伸（stretch），并在预览里显示裁切结果 |
| Creative Cloud「修复」还原文件 | 改动丢失 | 在结果提示里说明；还原按钮可重复执行 |
| 权限不足 | 写入被拒 | 复用现有 `ElevationService`，自动提权重试（`--elevated` IPC 通道已就绪） |
| 签名失效 | 安全软件告警 | UI 常驻提示 + 日志记录 |

---

## 六、对现有代码的复用与新增

### 可直接复用

| 现有组件 | 用途 |
|---|---|
| `PngCodec.Parse` | 校验替换图是合法 PNG、读取宽高 |
| `PngCodec.ExtractAsset` | 从 PE 资源或文件里取出原图做预览 |
| `PeTools.Parse` / `ComputeChecksum` | PE 头解析；改完资源后重算 CheckSum |
| `PngScanner.Scan` | A 类文件的兜底识别（不依赖文件名） |
| `Patcher.IsProcessRunning` | 进程占用检查 |
| `ElevationService` | 提权写入 |
| `AppSettings` / `AppLog` | 配置与日志 |

### 需新增（Core）

1. **`PeResource.cs`** —— PE 资源树解析（type / name / lang 三级），列出所有 `PNG` 类型条目及其尺寸、语言、RVA。
2. **`ResourceWriter.cs`** —— `BeginUpdateResourceW` / `UpdateResourceW` / `EndUpdateResourceW` 封装，含失败回滚与写后自检。
3. **`SplashTargets.cs`** —— 目标注册表：内置各软件「安装目录探测规则 + 目标文件模式 + 目标类型（A/B）」。
4. **`ImageFit.cs`** —— 等比 cover 缩放 + PNG 重编码（新增 `System.Drawing.Common` 依赖，Windows 专用，随自包含发布打包）。

### 需新增（App）

5. **`AdobeSplashPage`**（或在现有启动图页加「Adobe 系」分组）—— 应用下拉框 + 目标列表（显示资源名、原尺寸、当前预览）+ 选图 + 多尺寸自动生成提示 + 应用/还原。
6. 启动图页顶部加一个「目标类型」分段控件：`达芬奇` / `Adobe 系`，避免两个模块的交互混在一起。

### 不动的东西

- 达芬奇启动图模块的 `SplashCore` 全部逻辑（含等长覆盖）保持不变。
- 字体模块（`FontInventory` / `QtFontTable` / `AdobeDictionary`）保持不变。
- 打包流程 `build.ps1` 不变（新增依赖会随 `dotnet publish` 自动进 payload）。

---

## 七、实施计划

| 阶段 | 内容 | 覆盖应用 |
|---|---|---|
| **P0** | A 类：文件型 PNG 扫描 / 覆盖 / 备份 / 还原 + 自动多尺寸 | PR、AI、AME、LR |
| **P1** | B 类：`PeResource` + `ResourceWriter` + AE 实测闭环 | AE |
| **P2** | B 类扩展：进程名表与目标注册表补全 | AU、CH、AN |
| **P3（可选）** | 其他软件：BMP 覆盖（Unreal / Maya）、jar 重打包（JetBrains / Live2D）、MUI DLL（VS2022） | 教程非 Adobe 部分 |
| **不做** | `.idrc` 容器、Photoshop `.idx + .dat` | ID、IC、PS |

**建议先做 P0 + P1**：P0 一天内可交付且零风险，P1 已有完整实测支撑（资源已定位、`UpdateResource` 已验证、签名约束已排除），做完即覆盖「PR + AE」这两个 Adobe 系最高频的启动图替换需求，占总需求的绝大部分。

---

## 八、需要确认的点

1. **是否接受「InDesign / InCopy / Photoshop 不支持」**？若要支持 PS，需额外投入逆向 `.idx` 索引格式，建议单独立项。
2. **改主 exe（Character Animator / Animate）是否纳入**？技术上与 DLL 一致，但一旦损坏影响面更大，建议默认关闭、需二次确认。
3. **教程里非 Adobe 的软件**（Unreal、Maya、JetBrains、VS2022、Live2D、3ds Max、ACDSee）是否要一并做？其中 Unreal/Maya 是 BMP、JetBrains 是 jar 重打包，技术上都简单，但会让「目标注册表」膨胀，维护成本上升。
