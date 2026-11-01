# RCL · Roblox Can Launcher

PCL2 风格的 Windows Roblox 启动器：实例隔离、多开、账号切换、FastFlags、模组覆盖与执行器版本跟随。

RCL 只做「启动器该做的事」：管理本地客户端文件、按你的配置启动 Roblox、通过官方接口取信息。它不注入游戏进程、不修改客户端运行时、不绕过任何检测。

[官网 / 使用说明](https://z416946520.github.io/RCL-Roblox-Can-Launcher/) · [下载最新版](https://github.com/z416946520/RCL-Roblox-Can-Launcher/releases/latest)

## 功能

### 启动与实例

- **实例隔离**：每个实例有独立的 FastFlags、模组目录、启动参数与绑定版本，互不影响。
- **多开**：持有 Roblox 单例互斥体，并为每个并发客户端准备独立的目录联接槽位。
- **启动安全**：链接按白名单解析，只把合法的 `roblox://` / `roblox-player:` 交给客户端；非法参数不会透传。
- **启动崩溃自愈**：启动后监视日志与退出码，判定「启动即崩溃」时自动重试 → 还原 FastFlags/模组再试 → 把 Roblox 本地缓存挪开再试（保留登录与画质设置）→ 仍失败才报错并给日志路径。
- **无参启动**：不选游戏时用 `-app` 拉起 Roblox 主界面，而不是停在托盘。
- **快捷加入**：粘贴游戏链接或 Place ID 直接开服，支持指定 JobId、最近服务器与快速重连。

### 客户端管理

- 启动前自动比对官方版本：默认「立即更新」，也可设为「等一天」或「只手动更新」；实例绑定了固定版本时跳过检查。
- 从官方部署接口取版本，三个 CDN 镜像自动排序（按实测速率罚分）；指定渠道不可用时回退到 production，断网时使用本地缓存的版本信息。
- 大于 4 MB 的包按 HTTP Range 分段并行下载，失败只重下该段；断点信息落盘，中断后续传。
- 全部包先解压到 `{版本}.installing` 暂存目录，校验通过后整体替换安装目录；失败保留原安装。
- 安装完成后写入 `rcl-install.json` 标记；残留暂存目录超 6 小时自动清理。
- 支持绑定已有版本目录，也能识别 Fishstrap / Bloxstrap 等第三方启动器的安装位置。

### 账号

- 内置 WebView2 登录，或手动粘贴 `.ROBLOSECURITY`。
- Cookie 用 DPAPI 加密保存，切换账号只替换 Cookie 条目，保留其余本地存储。
- 首次接管前备份原始 Cookie，随时可还原。
- 账号头像自动下载、7 天轮换刷新，主页即时显示。

### FastFlags

- 实例级配置，启动时写入所选版本的 `ClientAppSettings.json`，写前自动备份，可一键还原。
- 内置常用预设，支持单条添加、JSON 批量导入、从其他启动器导入。
- 启动时拉取 Roblox 官方公开 flag 列表（`DFStringAllowedPublicFlags`），只用于界面提示：不在列表里的 flag **照常写入**，仅显示提醒。
- 每次启动前还会把「非公开 flag」的数量写进日志，方便排查。

### 模组

- 把实例模组目录里的文件按相对路径覆盖到版本目录，覆盖前逐文件备份，可在「模组」页还原。
- 崩溃隔离：记住上次成功启动时的模组集合，反复启动崩溃时只停用「上次正常之后新增」的模组，并在日志和弹窗里列出被停用的项。
- 只动本地内容文件，不触碰客户端可执行文件。

### 工具与界面

- 双语界面：简体中文 / English，设置 → 外观切换，重启生效；英文覆盖全部按钮、标签与说明文字。
- 服务器列表、指定服务器加入、执行器版本跟随（WEAO 每 30 分钟检查一次）。
- 客户端修改清理：还原 RCL 写入的 FastFlags、模组与空闲多开槽位，不碰其他启动器的文件。
- 进程优化（可选）：启动后降低 CPU 优先级，并可限制工作集大小与 CPU 核心数；安全启动时自动跳过。
- 诊断包：一键打包日志（自动轮转，保留最近 10 份）、设置、实例元信息与环境信息，不含账号令牌。
- DLL 侧载加固：启动时从 System32 预加载常用系统模块，避免被同目录同名 DLL 劫持。
- 接管 `roblox://` / `roblox-player:` 协议，可随时恢复原处理程序。
- Discord Rich Presence、游玩时长统计、主题色与背景图、桌面快捷方式。
- 自动更新：默认从本仓库 GitHub Releases 拉取新版 `RCL.exe`，启动时检查；可选「自动下载并重启 / 先询问 / 只手动」，也支持自定义 JSON 清单源。
- 更新文件校验：GitHub 提供 sha256 时按哈希校验，否则按发布资源大小校验，再交给独立脚本替换自身并重启。

## 系统要求

- Windows 10 / 11（x64）
- .NET 8 桌面运行时（发布包为自包含单文件，无需单独安装）
- WebView2 运行时（仅内置登录窗口需要；缺失时可改用 Cookie 手动登录）

## 下载

到 [Releases](https://github.com/z416946520/RCL-Roblox-Can-Launcher/releases/latest) 下载 `RCL.exe` 直接运行。程序不写系统目录，所有数据都在用户目录下。

## 从源码构建

```powershell
dotnet publish RCL/RCL.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=None -p:DebugSymbols=false `
  -o dist/RCL
```

产物是单文件 `dist/RCL/RCL.exe`，与 CI 中的发布命令一致。

## 数据目录

| 路径 | 内容 |
| --- | --- |
| `%APPDATA%\RCL\instances\` | 每个实例的配置、模组、备份 |
| `%APPDATA%\RCL\avatars\` | 账号头像缓存 |
| `%APPDATA%\RCL\cache\` | 公开 FastFlag 列表等缓存 |
| `%APPDATA%\RCL\logs\rcl.log` | 运行日志 |
| `%APPDATA%\RCL\multi-instance\` | 多开槽位的目录联接 |
| `%LOCALAPPDATA%\Roblox\Versions\` | Roblox 客户端版本（官方目录） |

## 边界与说明

- RCL 不提供、也不计划提供任何修改客户端运行时、注入进程或绕过检测的功能。
- 修改本地客户端文件（FastFlags、模组覆盖）属于个人使用范畴，是否违反 Roblox 条款由使用者自行判断；RCL 会在写入前备份，并提供一键还原。
- 多开与账号切换只在本地生效，不涉及服务端行为。

## 致谢

界面语言参考 PCL-CE（Plain Craft Launcher Community Edition），启动与多开机制参考 Voidstrap / Bloxstrap 的公开实现，模组与 FastFlags 的交互参考 Froststrap。所有实现均为独立重写，不包含上述项目的代码或资源。
