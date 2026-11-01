using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace RCL.Core;

/// <summary>
/// Bilingual UI layer. Chinese stays the source text in XAML and code; when the language is
/// English every rendered string is looked up here and swapped at load time. A missing entry
/// simply stays in Chinese, so new UI never breaks the English build.
/// </summary>
public static class Loc
{
    public const string Chinese = "zh";
    public const string English = "en";

    private static readonly Dictionary<string, string> English2 = new(StringComparer.Ordinal)
    {
        // navigation / window chrome
        ["启动"] = "Launch",
        ["实例"] = "Instances",
        ["下载"] = "Downloads",
        ["快捷启动"] = "Quick Launch",
        ["工具"] = "Tools",
        ["设置"] = "Settings",
        ["最小化"] = "Minimize",
        ["关闭"] = "Close",

        // accounts
        ["添加 Roblox 账号"] = "Add Roblox account",
        ["登录页面由 Roblox 官方提供"] = "Sign-in is provided by Roblox",
        ["正在打开 Roblox 登录页面…"] = "Opening the Roblox sign-in page…",
        ["登录完成后点「完成添加」。"] = "Click \u201cFinish\u201d once you are signed in.",
        ["取消"] = "Cancel",
        ["完成添加"] = "Finish",
        ["Roblox 账号"] = "Roblox account",
        ["管理账号"] = "Manage accounts",
        ["未登录 Roblox"] = "Not signed in",
        ["登录后所有实例都会使用这个账号"] = "Every instance launches with this account",
        ["添加账号"] = "Add account",
        ["登录一次后，切换账号即可让所有实例使用对应账号启动。"] =
            "Sign in once, then switch accounts to launch every instance with the chosen one.",
        ["添加账号后，启动的实例会使用它"] = "Instances launch with the account you add here",
        ["恢复启动前登录"] = "Restore previous sign-in",
        ["网页登录添加"] = "Add via web sign-in",
        ["手动 Cookie 添加"] = "Add cookie manually",
        ["刷新头像"] = "Refresh avatars",
        ["账号已隐藏"] = "Account hidden",
        ["启动实例时自动使用"] = "used automatically when launching",
        ["匿名显示账号"] = "Hide account details",
        ["显示账号信息"] = "Show account details",
        ["已开启账号匿名显示"] = "Account details hidden",
        ["已显示账号信息"] = "Account details shown",
        ["已保存账号"] = "Saved accounts",
        ["0 个"] = "0",
        ["需重新登录"] = "Sign-in expired",
        ["待验证"] = "Unverified",
        ["切换"] = "Switch",
        ["移除"] = "Remove",
        ["还没有保存账号。用上面的「网页登录添加」开始。"] =
            "No saved accounts yet. Start with \u201cAdd via web sign-in\u201d.",
        ["Cookie 安全"] = "Cookie safety",

        // fast flags
        ["添加 FastFlag"] = "Add FastFlag",
        ["添加单项"] = "Single",
        ["导入 JSON"] = "Import JSON",
        ["Flag 名称"] = "Flag name",
        ["值（JSON）"] = "Value (JSON)",
        ["直接粘贴一个 JSON 对象，支持一次导入多条 Flag。"] =
            "Paste a JSON object to import several flags at once.",
        ["从文件导入"] = "Import from file",
        ["从剪贴板粘贴"] = "Paste from clipboard",
        ["确定"] = "OK",
        ["搜索 Flag…"] = "Search flags…",
        ["添加 / 导入"] = "Add / Import",
        ["应用预设"] = "Apply preset",
        ["从其他启动器导入"] = "Import from another launcher",
        ["导出"] = "Export",
        ["立即写入版本"] = "Write to version now",
        ["这个 Flag 不在 RCL 的常见列表内，请确认来源可靠"] =
            "This flag is not in RCL's common list; make sure the source is trustworthy",
        ["删除"] = "Delete",
        ["还没有 FastFlags。可以点「应用预设」快速添加一组常用配置。"] =
            "No FastFlags yet. Use \u201cApply preset\u201d to add a common set.",
        ["从其他启动器导入 FastFlags"] = "Import FastFlags from another launcher",
        ["导入"] = "Import",
        ["没有找到其他启动器的 FastFlags 文件。"] =
            "No FastFlags files from other launchers were found.",
        ["全部导入到目标实例"] = "Import everything into the target instance",

        // download / install
        ["安装客户端"] = "Install client",
        ["已安装版本"] = "Installed versions",
        ["执行器版本"] = "Executor versions",
        ["安装 Roblox 客户端"] = "Install Roblox client",
        ["从 Roblox 官方部署接口下载客户端文件，安装到系统标准版本目录。"] =
            "Downloads the client from Roblox's deployment API into the standard versions folder.",
        ["官方部署"] = "Official deployment",
        ["默认渠道与 Roblox 官方更新保持一致。"] = "The default channel tracks Roblox's official release.",
        ["刷新"] = "Refresh",
        ["正在查询…"] = "Checking…",
        ["查询中"] = "Checking",
        ["下载并安装"] = "Download and install",
        ["校验并修复"] = "Verify and repair",
        ["打开版本目录"] = "Open versions folder",
        ["网络诊断"] = "Network diagnostics",
        ["测试连接"] = "Test connection",
        ["尚未测试"] = "Not tested yet",
        ["安装位置与隔离"] = "Install location and isolation",
        ["RCL 读取系统 Roblox 版本目录。不同版本可以在实例里单独绑定，实现版本隔离。"] =
            "RCL reads the system Roblox versions folder. Each instance can bind its own version.",
        ["绑定目标实例"] = "Bind target instance",
        ["选择实例后，点版本右侧的「绑定」即可锁定版本。"] =
            "Pick an instance, then click \u201cBind\u201d next to a version to pin it.",
        ["刷新列表"] = "Refresh list",
        ["本机版本"] = "Local versions",
        ["最新"] = "Latest",
        ["运行中"] = "Running",
        ["打开"] = "Open",
        ["绑定"] = "Bind",
        ["没有检测到已安装的 Roblox 客户端。先到「安装客户端」下载。"] =
            "No Roblox client found. Install one from \u201cInstall client\u201d first.",
        ["执行器支持版本"] = "Executor-supported versions",
        ["数据来源 WEAO（weao.xyz）；下载后会把目标实例锁定到该版本。"] =
            "Source: WEAO (weao.xyz); the target instance is pinned to that version after download.",
        ["WEAO 网站"] = "WEAO website",
        ["刷新数据"] = "Refresh data",
        ["目标实例"] = "Target instance",
        ["切换到本页时会从 WEAO 查询执行器支持的版本。"] =
            "Opening this page queries WEAO for executor-supported versions.",
        ["打开下载目录"] = "Open download folder",
        ["按支持版本分组"] = "Group by supported version",
        ["0 个版本"] = "0 versions",
        ["当前"] = "Current",
        ["已安装"] = "Installed",
        ["没有从 WEAO 获取到版本数据。检查网络后点「刷新数据」。"] =
            "No version data from WEAO. Check your network and hit \u201cRefresh data\u201d.",
        ["Windows 执行器"] = "Windows executors",
        ["· 支持"] = "\u00b7 supported",
        ["已更新"] = "Updated",
        ["免费"] = "Free",

        // instances
        ["实例管理"] = "Instance management",
        ["新建实例"] = "New instance",
        ["搜索实例…"] = "Search instances…",
        ["多开"] = "Multi-instance",
        ["复制实例"] = "Duplicate instance",
        ["删除实例"] = "Delete instance",
        ["打开目录"] = "Open folder",
        ["概览"] = "Overview",
        ["模组"] = "Mods",
        ["基本信息"] = "Basic info",
        ["实例名称"] = "Instance name",
        ["标识颜色"] = "Accent color",
        ["备注"] = "Notes",
        ["版本与隔离"] = "Version and isolation",
        ["每个实例可以锁定一个 Roblox 版本"] = "Each instance can pin one Roblox version",
        ["留空时自动使用最新安装的版本；指定版本后，即使 Roblox 更新，这个实例也会继续使用旧版本目录。"] =
            "Leave empty to use the newest installed version; a pinned instance keeps using its folder even after Roblox updates.",
        ["刷新版本列表"] = "Refresh versions",
        ["创建独立副本…"] = "Create isolated copy…",
        ["把当前版本复制到实例目录，做到文件级隔离"] =
            "Copies the current version into the instance folder for file-level isolation",
        ["跟随执行器支持版本（WEAO）"] = "Follow executor version (WEAO)",
        ["检测到新版本时自动下载并锁定"] = "Download and pin automatically when a new version appears",
        ["立即检查"] = "Check now",
        ["开启后会按 WEAO 中执行器支持的 Roblox 版本自动锁定实例；下载新版本约 230 MB。"] =
            "Pins the instance to the Roblox version your executor supports; a new download is about 230 MB.",
        ["行为"] = "Behavior",
        ["允许与其他实例同时运行（多开）"] = "Allow running alongside other instances (multi-instance)",
        ["启动时把实例模组覆盖到 Roblox 版本目录"] =
            "Overlay instance mods onto the Roblox version folder on launch",
        ["覆盖前 RCL 会自动备份原始文件；可在「模组」页还原。"] =
            "RCL backs up the original files first; restore them from the \u201cMods\u201d tab.",
        ["实例数据"] = "Instance data",
        ["创建桌面快捷方式"] = "Create desktop shortcut",
        ["重置游玩时长"] = "Reset playtime",
        ["0 条"] = "0 entries",
        ["只在启动时写入实例绑定的版本目录"] =
            "Only written to the instance's version folder on launch",
        ["模组覆盖"] = "Mod overlay",
        ["0 项"] = "0 items",
        ["导入文件"] = "Import file",
        ["导入文件夹"] = "Import folder",
        ["还原安装"] = "Restore installation",
        ["启用"] = "Enable",
        ["还没有模组。可以导入社区模组包，或直接把自己的文件拖进模组目录。"] =
            "No mods yet. Import a community pack, or drop your files into the mods folder.",
        ["安装状态"] = "Install status",
        ["启动参数"] = "Launch arguments",
        ["会追加到 Roblox 命令行。支持空格分隔与引号，例如：-windowMode 1"] =
            "Appended to the Roblox command line. Spaces and quotes work, e.g. -windowMode 1",
        ["启动实例"] = "Launch instance",
        ["多开启动"] = "Launch multi-instance",
        ["路径"] = "Paths",
        ["实例目录"] = "Instance folder",
        ["模组目录"] = "Mods folder",
        ["危险操作"] = "Danger zone",
        ["删除实例会同时移除它的模组、FastFlags、备份和游玩记录。"] =
            "Deleting an instance also removes its mods, FastFlags, backups and playtime.",
        ["删除这个实例"] = "Delete this instance",
        ["还没有实例"] = "No instances yet",
        ["创建一个实例来保存你的版本、模组与启动设置。"] =
            "Create an instance to keep its version, mods and launch settings.",

        // launch page
        ["启动 Roblox"] = "Launch Roblox",
        ["默认实例"] = "Default instance",
        ["单实例"] = "Single instance",
        ["选择实例"] = "Choose instance",
        ["实例设置"] = "Instance settings",
        ["允许多开"] = "Allow multi-instance",
        ["就绪"] = "Ready",
        ["没有检测到 Roblox 客户端"] = "No Roblox client found",
        ["先到「下载」页安装官方客户端，或导入一个已有版本后再启动。"] =
            "Install the official client from \u201cDownloads\u201d, or bind an existing version first.",
        ["前往下载"] = "Go to Downloads",
        ["快速加入"] = "Quick join",
        ["粘贴游戏链接或 Place ID 直接启动，不必先打开 Roblox 主页。"] =
            "Paste a game link or Place ID to launch straight into it.",
        ["加入游戏…"] = "Join game…",
        ["重新加入上次游戏"] = "Rejoin last game",
        ["正在运行"] = "Running",
        ["全部结束"] = "End all",
        ["结束"] = "End",
        ["最近游玩"] = "Recently played",
        ["还没有加入过游戏"] = "No games joined yet",
        ["查看游戏"] = "View game",
        ["启动日志"] = "Launch log",
        ["仅记录本次打开 RCL 后的操作"] = "Only records actions since RCL was opened",
        ["就绪。"] = "Ready.",
        ["关于实例隔离"] = "About instance isolation",

        // quick launch
        ["添加应用"] = "Add app",
        ["搜索快捷启动…"] = "Search quick launch…",
        ["添加 Potassium 启动项"] = "Add Potassium entry",
        ["启动配置"] = "Launch configuration",
        ["名称"] = "Name",
        ["目标路径"] = "Target path",
        ["浏览…"] = "Browse…",
        ["工作目录"] = "Working directory",
        ["进程名"] = "Process name",
        ["用于判断程序是否已运行；.exe 会自动填入，脚本或 .lnk 可手动填写进程名。"] =
            "Used to tell whether the app is already running; filled in for .exe, set it by hand otherwise.",
        ["以管理员身份运行"] = "Run as administrator",
        ["运行状态"] = "Status",
        ["自动保存"] = "Saved automatically",
        ["快捷启动说明"] = "About quick launch",
        ["添加快捷启动"] = "Add quick launch",
        ["把常用的 exe、脚本或 Potassium 放在这里，一次点击即可启动。"] =
            "Keep your usual exe, scripts or Potassium here and launch them with one click.",
        ["添加 Potassium"] = "Add Potassium",

        // settings
        ["外观"] = "Appearance",
        ["界面语言"] = "Interface language",
        ["切换后重启 RCL 生效。英文界面覆盖主要文案，动态状态提示仍可能显示中文。"] =
            "Restart RCL to apply. English covers the main interface; some live status messages may stay in Chinese.",
        ["集成"] = "Integrations",
        ["数据与关于"] = "Data and about",
        ["启动设置"] = "Launch settings",
        ["控制每次启动时的确认与收尾行为。"] = "Controls confirmation and cleanup around each launch.",
        ["Roblox 已在运行时，启动前先询问"] = "Ask before launching while Roblox is running",
        ["启动 Roblox 后关闭 RCL 窗口"] = "Close RCL after launching Roblox",
        ["记录实例游玩时长"] = "Track playtime per instance",
        ["新建实例默认开启多开"] = "New instances enable multi-instance",
        ["启动后优化 Roblox 进程（降低 CPU 优先级）"] =
            "Optimize the Roblox process after launch (lower CPU priority)",
        ["工作集上限"] = "Working set limit",
        ["MB（0 = 不限）"] = "MB (0 = unlimited)",
        ["CPU 核心"] = "CPU cores",
        ["（0 = 不限）"] = "(0 = unlimited)",
        ["Roblox 客户端更新"] = "Roblox client updates",
        ["启动前自动更新（立即）"] = "Update before launch (immediately)",
        ["发现新版本后等一天再更新"] = "Wait a day after a new version appears",
        ["只手动更新"] = "Manual updates only",
        ["只在实例未绑定固定版本时生效；Roblox 正在运行时会跳过本次检查。"] =
            "Only applies to instances without a pinned version; skipped while Roblox is running.",
        ["下次打开 RCL 或点击快捷方式时优先显示的实例。"] =
            "Instance shown first when RCL starts or a shortcut is used.",
        ["Roblox 链接接管"] = "Roblox link handling",
        ["接管 Roblox 链接"] = "Take over Roblox links",
        ["恢复原启动器"] = "Restore original launcher",
        ["客户端安全"] = "Client safety",
        ["这里可以还原 RCL 写入的客户端修改；不会删除你的模组源文件。"] =
            "Reverts the client changes RCL made; your mod source files are untouched.",
        ["还原 RCL 客户端修改"] = "Revert RCL client changes",
        ["打开官方版本修复"] = "Open official repair",
        ["尚未检查。还原前请先关闭所有 Roblox 客户端。"] =
            "Not checked yet. Close every Roblox client before reverting.",
        ["沿用 PCL 的配色层级：主题色会同步到标题栏、按钮与选中态。"] =
            "Follows PCL's color hierarchy: the accent color flows into the title bar, buttons and selected states.",
        ["主题色"] = "Accent color",
        ["自定义"] = "Custom",
        ["应用颜色"] = "Apply color",
        ["背景图"] = "Background image",
        ["选择图片…"] = "Choose image…",
        ["清除背景"] = "Clear background",
        ["不透明度"] = "Opacity",
        ["背景图会铺在页面内容下方；内容区域保持半透明白色以保证可读性。"] =
            "The image sits under the page content; content stays translucent white for readability.",
        ["把 RCL 的状态同步到 Discord；所有连接只在本机进行。"] =
            "Shows RCL's state on Discord; everything stays on this machine.",
        ["启用 Discord 状态"] = "Enable Discord presence",
        ["应用"] = "Apply",
        ["打开开发者门户"] = "Open developer portal",
        ["未启用。填写 Application ID 并勾选启用后，启动游戏时才会显示状态。"] =
            "Disabled. Fill in an Application ID and enable it to show presence while playing.",
        ["说明"] = "Notes",
        ["多开设置"] = "Multi-instance settings",
        ["RCL 使用单例互斥体 + 目录联接槽位，让多个 Roblox 客户端互不踢出。"] =
            "RCL holds the singleton mutex and gives each client a junction slot so they don't kick each other.",
        ["槽位"] = "Slots",
        ["最大槽位数量"] = "Maximum slots",
        ["保存"] = "Save",
        ["清理空闲槽位"] = "Clean up idle slots",
        ["打开槽位目录"] = "Open slots folder",
        ["当前会话"] = "Current sessions",
        ["结束全部 RCL 会话"] = "End all RCL sessions",
        ["管理 RCL 的数据目录，查看版本与致谢。"] = "Manage RCL's data folder and check the version.",
        ["数据"] = "Data",
        ["打开数据目录"] = "Open data folder",
        ["打开日志"] = "Open log",
        ["备份配置…"] = "Backup settings…",
        ["导出诊断包"] = "Export diagnostics",
        ["更新"] = "Updates",
        ["更新方式"] = "Update mode",
        ["自动：发现新版本就下载并重启"] = "Automatic: download and restart on a new release",
        ["提示：发现新版本先询问"] = "Ask before updating",
        ["手动：只在设置里检查"] = "Manual: check from settings only",
        ["GitHub 仓库（默认更新源）"] = "GitHub repository (default source)",
        ["更新源（JSON 清单地址，留空时使用上面的 GitHub 仓库）"] =
            "Custom manifest URL (leave empty to use the GitHub repository above)",
        ["GitHub 模式读取 Releases 里的 RCL.exe；清单模式至少包含 version、url、sha256。"] =
            "GitHub mode reads RCL.exe from Releases; a manifest needs version, url and sha256.",
        ["更新源（JSON 清单地址）"] = "Update source (JSON manifest URL)",
        ["启动时检查更新"] = "Check for updates on startup",
        ["保存并检查"] = "Save and check",
        ["PCL 风格 Roblox 启动器 · 实例隔离 · 多开 · FastFlags · 模组"] =
            "PCL-style Roblox launcher \u00b7 instance isolation \u00b7 multi-instance \u00b7 FastFlags \u00b7 mods",
        ["重置"] = "Reset",
        ["恢复默认启动设置、主题色与背景；实例数据不会被删除。"] =
            "Restore default launch settings, accent color and background; instance data is kept.",
        ["重置所有设置"] = "Reset all settings",
        ["打开 RCL 数据目录"] = "Open RCL data folder",

        // tools
        ["服务器浏览器"] = "Server browser",
        ["快捷方式"] = "Shortcuts",
        ["多开与进程"] = "Multi-instance and processes",
        ["运行日志"] = "Runtime log",
        ["配置导入"] = "Config import",
        ["查看公开服务器并直接加入；加入操作使用当前选中的实例。"] =
            "Browse public servers and join directly; joins use the selected instance.",
        ["查询游戏"] = "Look up game",
        ["查询"] = "Search",
        ["支持完整游戏链接、placeId=123 或纯数字。"] =
            "Accepts full game links, placeId=123, or a bare number.",
        ["公开服务器"] = "Public servers",
        ["服务器 ID"] = "Server ID",
        ["玩家"] = "Players",
        ["加入"] = "Join",
        ["查询一个游戏后，这里会列出公开服务器。"] = "Look up a game to list its public servers here.",
        ["快捷方式会带上实例 ID；双击后直接启动该实例，可附带最近一次加入的游戏。"] =
            "The shortcut carries the instance ID; double-click launches it with the last game attached.",
        ["为当前实例创建"] = "Create for current instance",
        ["打开桌面目录"] = "Open Desktop folder",
        ["已有的 RCL 快捷方式"] = "RCL shortcuts",
        ["桌面上还没有 RCL 快捷方式。"] = "No RCL shortcuts on the Desktop yet.",
        ["多开状态"] = "Multi-instance status",
        ["还没有创建多开槽位。以多开方式启动实例后会自动建立。"] =
            "No slots yet. They are created the first time you launch in multi-instance mode.",
        ["Roblox 进程"] = "Roblox processes",
        ["包含 RCL 启动的会话与系统中其他客户端。结束进程会丢失未保存进度。"] =
            "Includes sessions started by RCL and other clients on this PC. Ending a process loses unsaved progress.",
        ["当前没有运行中的 Roblox 进程。"] = "No Roblox processes are running.",
        ["RCL 日志"] = "RCL log",
        ["打开日志文件"] = "Open log file",
        ["Roblox 日志目录"] = "Roblox log folder"
    };

    private static readonly DependencyProperty OriginalsProperty = DependencyProperty.RegisterAttached(
        "Originals", typeof(Dictionary<string, string>), typeof(Loc), new PropertyMetadata(null));

    public static bool IsEnglish =>
        string.Equals(AppSettings.Current.Language, English, StringComparison.OrdinalIgnoreCase);

    public static string Language => IsEnglish ? English : Chinese;

    /// <summary>Translates one source string; unknown text is returned unchanged.</summary>
    public static string T(string? chinese)
    {
        if (string.IsNullOrEmpty(chinese) || !IsEnglish)
            return chinese ?? "";
        return English2.TryGetValue(chinese, out var english) ? english : chinese;
    }

    public static bool HasTranslation(string chinese) => English2.ContainsKey(chinese);

    /// <summary>True when the string contains Chinese characters (i.e. needs a translation).</summary>
    public static bool NeedsTranslation(string? text) =>
        !string.IsNullOrEmpty(text) && text.Any(c => c >= 0x4E00 && c <= 0x9FFF);

    /// <summary>
    /// Swaps every rendered string in <paramref name="root"/> using the table. Originals are kept
    /// per element, so calling this again never translates an already translated value.
    /// </summary>
    public static void Apply(DependencyObject root)
    {
        if (!IsEnglish || root is null)
            return;

        try
        {
            foreach (var node in Walk(root))
                TranslateNode(node);
        }
        catch (Exception ex)
        {
            Log.Warn("应用界面语言失败：" + ex.Message);
        }
    }

    private static void TranslateNode(DependencyObject node)
    {
        switch (node)
        {
            case TextBlock tb when !BindingOperations.IsDataBound(tb, TextBlock.TextProperty):
                Set(tb, "Text", tb.Text, value => tb.Text = value);
                break;
            case HeaderedContentControl header when
                header.Header is string headerText &&
                !BindingOperations.IsDataBound(header, HeaderedContentControl.HeaderProperty):
                Set(header, "Header", headerText, value => header.Header = value);
                break;
            case ContentControl content when
                content.Content is string contentText &&
                !BindingOperations.IsDataBound(content, ContentControl.ContentProperty):
                Set(content, "Content", contentText, value => content.Content = value);
                break;
            case Window window when !BindingOperations.IsDataBound(window, Window.TitleProperty):
                Set(window, "Title", window.Title, value => window.Title = value);
                break;
        }

        if (node is FrameworkElement element && element.ToolTip is string tip)
            Set(element, "ToolTip", tip, value => element.ToolTip = value);
    }

    private static void Set(DependencyObject element, string key, string? value, Action<string> assign)
    {
        if (string.IsNullOrEmpty(value))
            return;

        if (element.GetValue(OriginalsProperty) is not Dictionary<string, string> originals)
        {
            originals = new Dictionary<string, string>(StringComparer.Ordinal);
            element.SetValue(OriginalsProperty, originals);
        }

        if (!originals.TryGetValue(key, out var original))
        {
            original = value;
            originals[key] = original;
        }
        assign(T(original));
    }

    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            foreach (var descendant in Walk(child))
                yield return descendant;
        }
    }
}
