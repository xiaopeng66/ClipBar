# ClipBar 重命名方案（原 Clipboard11 → ClipBar，已完成）

> 状态：**已实施完成（2026-11-04）**，完成记录见 §5。
>
> 生成时间：2026-11-04 · 基线：386 例测试全绿，`dist\Clipboard11.exe` 70 MB。

---

## 0. 新名字与理由

**ClipBar**（Clipboard + Bar）：

- 与本机既有托盘工具命名一脉相承（PulseBar、TrafficMonitor 任务栏插件），「Bar」指常驻托盘/快捷唤出的小工具；
- 语义直白：剪贴板 + 工具条；
- 联网核查（2026-11-04）：未发现同名的在役 Windows 剪贴板工具，无显著冲突；
- 备选名（未采用）：ClipStack、ClipDeck。

显示名统一用 `ClipBar`；界面中文文案不变。

## 1. 变更点全量盘点（grep 实测：源内 "Clipboard11" ≈900 处 + "clipboard11" ≈250 处，其中绝大多数在 obj/ 构建产物，删除后重建）

| # | 变更对象 | 位置 | 说明 |
| --- | --- | --- | --- |
| 1 | 项目目录 | `projects/clipbar` → `projects/clipbar` | 整棵树移动 |
| 2 | 解决方案 | `Clipboard11.slnx` → `ClipBar.slnx` | 文件名 + 内容路径 |
| 3 | 项目目录/文件 | `src/Clipboard11.Core` → `src/ClipBar.Core`；`src/Clipboard11.App` → `src/ClipBar.App`；两个测试项目同理；`*.csproj` 文件名 | csproj 内 ProjectReference/AssemblyName/RootNamespace 同步 |
| 4 | 命名空间 | `Clipboard11.Core` / `Clipboard11.App` / `Clipboard11.App.Tests` / `Clipboard11.Core.Tests` → `ClipBar.*` | 全部 .cs 的 namespace/using + XAML `x:Class` + `clr-namespace` |
| 5 | 程序集/exe | AssemblyName → `ClipBar`（dist 产物变 `ClipBar.exe`） | csproj 设置；删旧 `Clipboard11.exe` |
| 6 | 应用清单 | `app.manifest` assemblyIdentity | 跟随替换 |
| 7 | **数据目录** | `%LOCALAPPDATA%\Clipboard11` → `%LOCALAPPDATA%\ClipBar` | `AppPaths` 常量替换 + **一次性迁移**：目标不存在且旧目录存在时 `Directory.Move`（history.db / settings.json / images / extensions / snipdo 导出 / app.log 整体搬移，同盘瞬间完成） |
| 8 | 单实例/唤出 | 互斥体 `Local\Clipboard11.SingleInstance`、事件 `Local\Clipboard11.Show` → `Local\ClipBar.*` | App.xaml.cs、SingleInstanceGuard、ShowSignalListener、SnipDo 启动脚本模板 |
| 9 | SnipDo 集成 | 动作目录 `0Clipboard11` → `0ClipBar`；清单 `identifier_pasteaction: clipboard11` → `clipbar`；启动脚本路径指向新 exe | `SnipDoPluginBuilder`；旧 `0Clipboard11` 目录删除后重装 |
| 10 | 窗口/托盘文案 | 窗口 Title `Clipboard11`、`Clipboard11 设置`、托盘 tooltip、消息窗默认标题 | 各 XAML/cs 字符串 |
| 11 | 构建脚本 | `build.cmd` 内解决方案名与产物名 | 全局替换 |
| 12 | 文档 | README.md（标题/路径/exe 名/SnipDo 目录名）、GOAL.md、docs\*.md | 全局替换（历史审查文档中的名字一并替换，路径 `projects/clipbar` → `projects/clipbar`） |
| 13 | dist 旧产物 | `dist\Clipboard11.exe`、`dist-lite\Clipboard11.exe` | 发布新名后删除旧文件 |

## 2. 不改的东西

- 数据库 schema、表结构、文件格式（只搬目录不改内容）；
- 界面中文文案（「剪贴板」「搜索剪贴板」等与产品名无关）；
- 图标资源、快捷键、交互行为；
- 历史审查文档中的日期与结论（只换产品名）。

## 3. 实施顺序

1. 停止运行中的 ClipBar/Clipboard11；
2. 写入本方案（旧目录内，随树迁移）；
3. 目录/解决方案重命名（mv）；
4. 全局内容替换（`Clipboard11`→`ClipBar`、`clipboard11`→`clipbar`，限定 .cs/.xaml/.csproj/.slnx/.cmd/.md/.manifest）；
5. 手工补数据目录一次性迁移（AppPaths：目标缺失且旧目录存在 → Move）；
6. 删除全部 bin/obj（避免旧路径增量缓存）；
7. `dotnet build` + `dotnet test`（预期 386 全绿）+ 编码检查；
8. `build.cmd` 发布 → `dist\ClipBar.exe` / `dist-lite\ClipBar.exe`，删除旧名 exe；
9. SnipDo 重装：删除旧 `0Clipboard11` → `ClipBar.exe --install-snipdo` → 验证 `0ClipBar` 与启动脚本新路径；
10. 启动应用验证：数据目录已迁移（129 条历史在）、窗口/托盘名称、托盘图标；
11. 回填本文档 §5 与 GOAL。

## 4. 风险与回退

- **数据迁移失败**：Move 前检查目标不存在；Move 抛异常时回退为「保留旧目录 + 使用新目录」并弹提示（不丢数据，代价是历史从头开始）——实现按此防御。
- **SnipDo 旧动作残留**：显式删除旧目录，避免双动作。
- **回退**：git 无版本管理，回退 = 反向替换 + 目录名改回 + 数据目录改回（迁移是 Move，反向同样可行）。
- **旧 exe 误启动**：迁移后旧 exe 若被运行会重建空 `%LOCALAPPDATA%\Clipboard11`——提示用户删除旧 exe 或忽略（历史仍在 ClipBar）。

## 5. 完成记录（2026-11-04 回填）

| # | 状态 | 验证 |
| --- | --- | --- |
| 1 项目根目录 | ✅ 完成 | 用户关闭 ZCode 后手动改名 `projects/clipboard11` → `projects/clipbar`；文档内绝对路径引用已同步修正 |
| 2 解决方案 | ✅ | `ClipBar.slnx` |
| 3 项目目录/csproj | ✅ | `src/ClipBar.Core`、`src/ClipBar.App`、`tests/ClipBar.*.Tests`，csproj 文件与 AssemblyName=`ClipBar` |
| 4 命名空间 | ✅ | 97 个文件全局替换，源内零残留（仅 AppPaths 保留旧目录字面量用于迁移） |
| 5 程序集/exe | ✅ | `dist\ClipBar.exe` 70 MB / `dist-lite\ClipBar.exe` 9 MB，旧名 exe 已删除 |
| 6 app.manifest | ✅ | 已替换 |
| 7 数据目录迁移 | ✅ 实测 | 首次启动即完成 Move：`%LOCALAPPDATA%\ClipBar` 含 history.db(167 KB)/settings/images(67 文件)/extensions，旧目录已不存在；设置完整（Light/Medium） |
| 8 单实例/唤出 | ✅ | `Local\ClipBar.SingleInstance` / `Local\ClipBar.Show` |
| 9 SnipDo | ✅ 实测 | 旧 `0Clipboard11` 已删；`--install-snipdo` 重装为 `0ClipBar`（main.ps1 指向新 exe；PS 5.1 预览中文乱码仅为显示读法，文件为设计规定的无 BOM UTF-8） |
| 10 窗口/托盘文案 | ✅ 实测 | 窗口名 `ClipBar`、页脚 `149 项`（真实历史完整） |
| 11 构建脚本 | ✅ | `build.cmd` 引用 `ClipBar.slnx`、echo 新产物名 |
| 12 文档 | ✅ | README/GOAL/docs 全部替换 |
| 13 dist 旧产物 | ✅ | 旧 `Clipboard11.exe/.pdb` 已删 |

**额外加固**：注册表 Run 值改名后若用户开过自启会静默失效——`StartupService` 现在重写时清理旧值 `Clipboard11`，启动自愈改为「设置开 + 注册表失效 → 重写新值」而非把设置翻成关。
