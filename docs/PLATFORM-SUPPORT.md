# 跨平台支持评估

目前已实现的客户端只有 Windows 版，使用 WinForms 和 .NET Framework 4.x。macOS、Linux、Android、iOS 均尚无客户端、安装包或目标设备验证结果。下面是基于现有源码及官方平台文档的可行性评估，不代表已完成适配。

## 当前状态

| 平台 | 当前实现 | 可行路径 | 仍需验证 |
| --- | --- | --- | --- |
| Windows | 已有 WinForms 客户端及 Windows 构建、测试入口 | 保留现有版本，继续兼容项目原定的 Win7 SP1 / 8.1 / 10 / 11 范围 | 各目标 Windows 版本、缩放、网卡与防火墙环境 |
| macOS | 未实现 | 新增现代 .NET + Avalonia 客户端，复用传输协议 | Intel / Apple Silicon 构建与实机、文件权限、发现、发布流程 |
| Linux | 未实现 | 与现代桌面客户端共用 Avalonia 界面和传输核心 | 目标发行版、显示环境、原生依赖、文件系统及网络环境 |
| Android | 未实现 | 共用协议与部分界面，增加 Android 存储及生命周期适配 | 文档 URI、目录授权、网络权限、前后台切换与实机传输 |
| iOS / iPadOS | 未实现 | 共用协议与部分界面，增加 Apple 文档及网络权限适配 | 安全作用域文件访问、局域网授权、发现机制、签名构建与实机 |

Windows 本机构建或回环传输测试只能证明对应环境下的行为，不能证明其他操作系统已经可用，也不能替代跨设备互传测试。现有项目列出的 Windows 兼容目标也不等于已经在每个版本和设备上完成测试。

## 为什么现有程序不能直接运行到其他平台

`src/Program.cs` 和 `src/MainForm.cs` 依赖 `System.Windows.Forms`；`build.ps1` 使用 Windows 的 .NET Framework 编译器和引用程序集。微软将 WinForms 定义为 Windows 桌面 UI，.NET Framework 实现也仅用于 Windows。因此，现有 `LanTransfer.exe` 不能作为官方支持的 macOS、Linux、Android 或 iOS 客户端。[微软 WinForms 说明](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/overview/)

`src/TransferCore.cs` 没有引用 WinForms 或 Windows 原生调用；TCP 流、HMAC-SHA256 认证、文件指纹、分块传输、冲突保护和续传逻辑有较高复用价值。但它仍假设文件可用普通磁盘路径访问，并包含 Windows 文件名与大小写语义，需要在独立的跨平台构建和测试中处理。

## 建议架构

保留现有 .NET Framework / WinForms 构建，继续服务需要 Win7 兼容和轻量客户端的使用场景。未来另建现代 .NET 客户端，并将可共用的传输代码放入独立核心项目或通过受控的共享源码方式使用；不要直接把现有 Windows 构建切换到现代运行时而丢失原有兼容目标。

现代客户端优先评估 Avalonia：其官方文档列出了 Windows、macOS、Linux、Android 和 iOS 目标，适合保留 C# 核心并逐步扩展平台。具体框架版本、运行时版本、系统最低版本和支持等级，应在实施时按实际选定版本锁定，不能把框架支持列表直接当作本项目的支持承诺。[Avalonia 支持平台](https://docs.avaloniaui.net/docs/supported-platforms)

.NET MAUI 官方支持列表包含 Windows、macOS、Android 和 iOS，未列出 Linux，因此若 Linux 是明确目标，不建议把仅依赖官方 MAUI 目标作为统一方案。移动端开发仍需要对应工具链；微软明确指出 iOS 开发需要联网的 Mac。[MAUI 支持平台](https://learn.microsoft.com/en-us/dotnet/maui/supported-platforms?view=net-maui-10.0)

协议可以继续使用 `LANTRANSFER/1`，以便新客户端与现有 Windows 版互传。重构时需保持长度帧、指令、UTF-8、整数编码、认证、哈希及续传语义，并增加新旧实现双向互传测试；若将来改变协议，再明确版本协商。跨平台适配本身不会使当前文件内容自动获得加密。

## macOS 与 Linux 改造重点

### 界面与系统集成

- 重建 Avalonia 窗口和交互，不能直接复用 WinForms 控件。将发送队列、接收状态、设备列表、进度及日志整理为与具体 UI 无关的状态。
- 将文件/文件夹选择、拖放、剪贴板、打开目录及设置保存封装为平台服务；界面更新统一调度到 UI 线程。
- 在不同 DPI、字体、窗口尺寸及桌面环境中检查布局。现代客户端与现有 Win7 客户端分别构建、分别验证。
- 按目标平台处理应用图标、安装包、架构、原生依赖及签名分发。macOS 和 Linux 的具体支持范围需以实际构建和设备验证确定。

### 文件系统与传输核心

- **路径分隔符：** `SafePath` 校验会将反斜杠替换为 `/`，后续实际路径拼接却没有同样处理。在 POSIX 文件系统中反斜杠可能是文件名字符。应明确协议中的 `/` 分隔规则，并保证校验和落盘使用同一份规范化结果。
- **大小写：** 根文件名去重和目录范围比较使用 `OrdinalIgnoreCase`，续传缓存键还将相对路径转为大写。应明确哪些比较属于协议限制，哪些必须服从实际文件系统；不能默认 macOS 的所有卷或 Linux 路径都大小写不敏感。
- **可移植文件名：** `Path.GetInvalidFileNameChars()` 随运行环境变化。需要选择一致的跨平台文件名约定或明确的拒绝策略，处理 Windows 保留名、结尾空格/点、Unicode 和大小写冲突。不能自动覆盖或悄悄改名。现有 Windows 限制可作为兼容约定，但需写明其含义。
- **链接和根目录保护：** 保留路径逃逸及符号链接保护，在目标文件系统重新验证链接识别、目录边界和续传缓存保护；不能仅因为 Windows 测试通过就认为行为一致。
- **文件元数据：** 修改时间、文件占用和权限错误在各平台可能不同。保留“完整性校验成功后才成为最终文件”的原则，并验证现有文件锁策略在目标系统的实际行为。
- **网络：** 验证网卡枚举、IPv4 广播地址计算、UDP 端口回退、TCP 取消及防火墙行为。继续保留手动 IP + 端口连接，以覆盖广播被隔离的网络环境。

## Android 与 iOS 额外工作

### 文件不一定是磁盘路径

当前发送入口接受 `string[] sources`，通过 `FileStream` 读取文件；`HashStream` 还会把流位置设为零，发送和续传也会重新定位。这要求普通文件路径及可定位的流。

Android 的 Storage Access Framework 通过系统选择器提供文档或目录 URI，不应假定 `content://` 能转换为普通路径。iOS 文档选择器可返回应用容器之外的安全作用域 URL，需要在授权有效期间访问，并正确释放。应抽象文件元数据、打开读写流、目录枚举及访问授权，而不是把手机选择器结果直接传给现有路径 API。[Android 文档存储访问](https://developer.android.com/training/data-storage/shared/documents-files)、[Apple 目录访问](https://developer.apple.com/documentation/uikit/providing-access-to-directories)、[Apple 安全作用域访问](https://developer.apple.com/documentation/foundation/url/startaccessingsecurityscopedresource())

对于不可定位的提供方流，需要选择重新打开并顺序读取、受控缓存或明确拒绝该能力。不能为了复用 `FileStream` 就无提示地复制整批大文件到应用缓存。接收端也需要决定先写入应用私有续传目录再导出，还是直接使用被授权的文档提供方；前者需要额外空间，后者需要验证随机读写、原子完成和冲突保护能力。

### 局域网与设备发现

iOS 访问局域网需配置用途说明并处理用户拒绝。现有 UDP 广播收发还涉及 `com.apple.developer.networking.multicast` entitlement，不能只把 `Discover()` 复制到移动端就承诺可用。若使用 Bonjour，需按 Apple 要求声明服务类型。移动首版可以先提供手动 IP + 端口连接，发现机制另行实现和验证；手动连接同样需要正确处理局域网访问授权。[Apple 局域网隐私说明](https://developer.apple.com/documentation/technotes/tn3179-understanding-local-network-privacy)

Android 也需按所选目标 SDK、网络 API 和发现方式配置权限，并检查 Wi-Fi 隔离、多网卡及真机表现；不能依赖桌面网卡枚举和广播行为完全相同这一假设。

### 生命周期与后台传输

当前后台 `Thread` 只负责不阻塞桌面界面，不等于手机操作系统允许应用持续在后台接收。移动首版宜明确以前台传输为目标，处理离开页面、切换应用、锁屏、网络变化、系统回收及再次授权后恢复。现有断点续传可以复用，但必须将必要任务状态持久化，并确认文件授权仍然有效。

后续若加入 Android 后台传输，需要评估适用的系统后台任务或前台服务方案及对应限制。Android 官方对特定前台服务类型和目标版本规定了运行时限，因此不能承诺无限期后台接收。[Android 前台服务类型](https://developer.android.com/develop/background-work/services/fgs/service-types)、[Android 前台服务时限](https://developer.android.com/develop/background-work/services/fgs/timeout)

## 实施顺序与验收

1. 保持现有 Windows 构建和传输回归测试通过，先提取与 UI 无关的状态及平台接口。
2. 新增现代 .NET 核心构建与测试入口；在 macOS/Linux 上验证路径规则、链接保护、中文及 Unicode 文件名、大小写冲突、重复跳过、内容冲突、取消、断线及损坏前缀续传。
3. 新增 Avalonia 桌面客户端，验证 Windows 与 macOS/Linux 之间双向互传，再形成各平台可下载的安装包或运行产物。
4. 实现 Android 存储适配、权限及前台传输，验证与桌面端的双向互传和恢复。
5. 实现 iOS 文档访问、局域网权限、适用的发现方式及生命周期，完成 Mac 工具链构建和 iPhone/iPad 实机验证。

每个平台达到“能构建、能启动、能选择文件和目录、能与现有版本双向传输、能处理权限拒绝和断线恢复”后，再更新本项目的正式支持矩阵。当前文档只记录评估，本次没有引入 Avalonia、MAUI 或新的跨平台客户端。
