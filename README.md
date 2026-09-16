# PartMap — 3MF 图像文件管理器

[![Release](https://img.shields.io/github/v/release/hellysu/partmap-oss)](https://github.com/hellysu/partmap-oss/releases/latest) [![CI](https://github.com/hellysu/partmap-oss/actions/workflows/pr.yml/badge.svg)](https://github.com/hellysu/partmap-oss/actions/workflows/pr.yml) [![License: MIT](https://img.shields.io/github/license/hellysu/partmap-oss)](LICENSE)

**English:** PartMap is an open-source Windows desktop app for visually organizing `.3mf` and `.gcode.3mf` files around product diagrams. It supports hotspot-to-file mapping, drag and drop, file history, portable metadata, shared multi-computer folders, and optional Bambu Studio slicing.

It is designed for real 3D-printing workflows where one product contains many printable parts and operators need a fast visual way to find, version, move, and slice the right files without introducing a database or cloud dependency.

PartMap 是一个 Windows 开源桌面工具，把产品示意图变成可操作的 3MF / G-code 3MF 文件索引。图片上的每个部件热点可以绑定一个或多个文件组，文件可双击打开、F2 重命名，并可拖到资源管理器或切片软件。

## 使用方法

1. 双击 `PartMap.exe`。
2. 点击“新建产品”，输入产品名，并选择包装图和原模型目录。PartMap 直接管理该目录，不复制或移动 3MF。
3. 打开“布局编辑”，在左侧“待归类文件”中选择自动生成的文件组。
4. 点击“框选部件”，在包装图对应部位拖出矩形并命名。
5. 继续把其他文件组拖到已有热点；同一部件可绑定多个组。已归类文件默认从左栏隐藏，可用“显示全部”查看。
6. 关闭“布局编辑”进入浏览模式。热点平时透明，鼠标移入会轻微高亮并变为手形；点击后在右侧查看文件。

如果当前产品仍有待归类文件，打开或切换到该产品时会自动进入布局编辑并展开左侧栏。

## 整个模型文件夹迁移到其他电脑

新版会在每个产品的 3MF 根目录维护 `配置/PartMap.product.json` 和 `配置/包装图.*`。便携配置包含产品名称、热点坐标、文件分组与人工映射规则，并可随整个模型文件夹一起迁移到另一台电脑。

从资源管理器把 `.3mf` 拖到图片热点，会复制到当前模型目录并直接绑定该部件；拖到右侧“部件文件”栏，会加入当前选中的部件。拖到其他位置仍按普通导入处理，同名文件不会覆盖。

底部“读取子目录”默认关闭，因此只读取和监控模型根目录；需要时可为当前产品单独开启。关闭开关不会删除已经保存的子目录映射。产品选择框旁的“改名”只修改产品显示名称，不会改目录或移动模型文件。

底部“显示”可以切换“全部 3MF”“仅 `.gcode.3mf`”或“仅普通 `.3mf`”。选择单一格式时，右侧只保留对应的一栏；选择全部时恢复普通 3MF 与 G-code 3MF 上下双栏。筛选只改变界面内容，不会删除文件或修改热点映射。

`.gcode(1).3mf`、`.gcode(2).3mf` 等 Windows 重名副本同样按 G-code 3MF 识别和分栏，界面会隐藏完整特殊后缀。右侧分隔条只调整上下文件列表高度，不会再把标题行拉成大块空白。

开启子目录读取后，可用“排除文件夹…”选择模型根目录内不需要扫描的一个或多个子文件夹，也可在同一窗口移除排除项。排除路径按相对路径写入产品配置，文件和历史映射不会被删除。

在右侧选中一个或多个文件后，点击“移除选中组”即可解除这些文件组与当前部件的绑定；也可以直接把所选文件拖回左侧待归类区域。若同一组还被其他热点引用，其他部件的绑定保持不变。

右侧“归到历史”会把所选真实文件移动到模型根目录下的 `历史` 文件夹，该文件夹不会被扫描或显示。归档文件名追加当天日期，例如 `头盔_2026-08-27.3mf`；同名时依次追加 `_v1`、`_v2`。G-code 文件的日期位于 `.gcode.3mf` 或 `.gcode(1).3mf` 之前。

1. 在原电脑用新版 PartMap 打开一次产品并保存，确认模型根目录出现 `配置/PartMap.product.json` 和 `配置/包装图.*`。
2. 把整个 3MF 文件夹复制到移动硬盘或另一台非局域网电脑。
3. 在另一台电脑打开 PartMap，点击顶部“导入文件夹”，选择包含 `配置/PartMap.product.json` 的 3MF 根目录。
4. 程序会恢复产品、包装图、热点和映射，并把模型根路径更新为新电脑上的实际位置。

PartMap 不会为了便携配置复制原始 `.3mf`；以后修改热点、分组或文件名时，`配置/` 下的便携数据会同步更新。切片、归档等明确的文件操作仍会按界面提示修改对应文件。

## 便携目录

```text
PartMap.exe
products/             # 集中保存包装图、热点、映射和模型路径
settings.<电脑名>.json # 每台电脑各自最近打开的产品
```

包装图会复制到 `products` 对应产品目录，配置中的程序内路径使用相对路径；映射网络盘会尽量转换成两台电脑通用的 UNC 路径。模型仍保留在你选择的原目录，不会被复制。复制完整 PartMap 目录后，图片、热点和映射会一起保留。

## 文件操作

- 双击或 Enter：使用 Windows 默认程序打开 3MF。
- 从资源管理器拖入一个或多个 `.3mf`：复制到当前产品模型根目录；同名文件不会覆盖。
- F2：重命名实际文件。
- Delete 或“删除文件”：本地文件移到 Windows 回收站；共享目录会明确提示可能永久删除。
- 拖动文件卡片：拖到桌面、资源管理器或切片软件。
- “所在文件夹”：在资源管理器中选中文件。
- “更换目录”：重新绑定产品的原模型目录，不复制文件。
- 编辑模式拖动虚线热点：调整热点位置；右下角方块调整大小。

文件卡片会显示真实文件的最后修改日期。拖入复制会保留源文件的最后修改时间；复制期间先写入临时文件，完成后再显示为 `.3mf`，避免共享目录出现半复制模型。

文件名以 `.gcode.3mf` 结尾时，卡片和自动分组会隐藏这段完整后缀；实际文件名保持不变，F2 重命名框仍显示真实名称。

## 两台电脑同时使用

- 两台电脑必须运行同一新版 `PartMap.exe`，并对程序目录和模型目录拥有读写权限。
- 产品配置使用共享文件锁和版本号保护；不同电脑的热点与映射会自动同步。
- 如果两台电脑恰好同时修改同一个产品配置，先保存者成功，另一台会载入最新版并提示重新操作，不会静默覆盖。
- 重命名和删除会立即作用于共享目录中的真实文件，另一台电脑随后自动刷新。
- 推荐使用两台电脑都一致的 UNC 路径，例如 `\\服务器\共享\模型`，不要使用盘符不同的映射驱动器。
- 网络共享通常不使用本机 Windows 回收站，请在服务器或 NAS 上开启回收站、快照或备份。

## 下载与 Windows 兼容版

GitHub Releases：<https://github.com/hellysu/partmap-oss/releases/latest>

- `PartMap-Setup-win-x64-*.exe`：推荐普通用户使用的完整安装版，支持开始菜单、可选桌面快捷方式和 Windows 卸载。
- `PartMap-Portable-win-x64-*.zip`：64 位便携版，完整解压后直接运行 `PartMap.exe`，适合移动硬盘或不想安装时使用。
- `PartMap-Portable-win-x86-*.zip`：仅用于 32 位 Windows，或 x64 版本无法启动时进行兼容排查。
- 所有发布包均为 .NET 8 自包含版本，不要求电脑预装 .NET。
- 便携版不能只复制单独的 EXE，必须保留 `PartMap.exe` 旁边的 DLL 和子目录。
- 安装版默认安装到当前用户的 `%LocalAppData%\Programs\PartMap`，避免 PartMap 自身数据目录遇到 Program Files 写权限问题。
- 程序目录和模型共享目录需要读写权限。如果启动失败，日志位于 `%LocalAppData%\PartMap\PartMap-startup.log`。

## 从源码构建

需要 .NET 8 或更新 SDK：

```powershell
dotnet build PartMap.csproj -c Release
dotnet run --project tests/PartMap.SmokeTests/PartMap.SmokeTests.csproj -c Release
dotnet publish PartMap.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false
```

## 开源开发

- 许可证：MIT（见 LICENSE）。
- 开发说明：DEVELOPMENT.md。
- 贡献指南：CONTRIBUTING.md。
- 安全问题：SECURITY.md。

## License

PartMap is released under the MIT License. See [LICENSE](LICENSE).
