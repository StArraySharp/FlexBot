# FlexBot <VERSION>

## 下载哪个

| 平台 | 文件 |
|---|---|
| Windows x64 | `FlexBot-<VERSION>-win-x64.zip` |
| Linux x64 | `FlexBot-<VERSION>-linux-x64.zip` |
| Linux arm64 | `FlexBot-<VERSION>-linux-arm64.zip` |
| macOS Intel | `FlexBot-<VERSION>-osx-x64.zip` |
| macOS Apple Silicon | `FlexBot-<VERSION>-osx-arm64.zip` |
| **内置插件（平台无关）** | `FlexBot-BuiltInPlugins-<VERSION>.zip` |

平台包已自包含 .NET 运行时，**不需要**另装 .NET。**两个文件都要下**：平台包是宿主，插件在插件包里。
校验：`sha256sum -c SHA256SUMS.txt`（Windows 可用 `certutil -hashfile <文件> SHA256` 逐个比对）。

## 首次使用

1. 解压平台包；
   · Windows：双击 `FlexBot.exe`
   · Linux：`chmod +x FlexBot && ./FlexBot`
   · macOS：先 `xattr -dr com.apple.quarantine FlexBot` 再运行（包未签名，不去隔离会被拦）
2. 控制台会打印 WebUI 地址 + 一个随机登录密码（明文只打印这一次、不写日志）
3. 浏览器登录 → 设置页填「主人 QQ」（必填，权限最高），建议顺手设置自己的登录密码
4. 插件页 → 添加插件 → 选 `FlexBot-BuiltInPlugins-<VERSION>.zip`（一次装 8 个内置插件）
5. Agent 插件需要先在它的「设置」里填模型 ApiKey / BaseUrl / Model，再加载（否则会按预期加载失败）

## 本版变化（1.0.2）

- **托盘**：GUI 模式关窗不再退出，收进系统托盘；单击图标恢复，右键菜单可「打开控制台 / 退出」。
  Windows 走 Win32（子类化窗口过程拦 WM_CLOSE）；Linux 走 GTK delete-event + AppIndicator。
  macOS 暂不支持（拦关闭需接管 Photino 的 NSWindow delegate，会破坏其窗口回调），请用 `--no-gui` + 浏览器 WebUI 常驻。
- **命令行**：新增 `--help` / `--version` / `--gui`；未知参数不再被静默忽略（打印提示并以退出码 2 退出）。
  `--gui` 表示"必须开窗"：环境开不了窗口直接失败（退出码 3），不静默退化。
- **Agent**：模型池把主模型与备用回落统一管理；`!agent` 支持状态查询（上下文 / 缓存命中率 / 额度）与中断在途回复。
- **修复**：插件设置 JSON 大小写不敏感 —— 此前 WebUI 存的 camelCase 字段在插件侧静默读成 null，
  表现为"保存成功但功能完全不生效"（备用模型回落链与人格配置都中过）。
- **fs 插件**：迁移到 Zio 虚拟文件系统做结构化沙箱（含 GHSA-h39g-6x3c-7fq9 的 `/../` 越界修复），
  并保留自有路径预检作为第三道防线。
- **其他**：超长帮助改合并转发；编译缓存按指纹清理旧产物；exe / 窗口 / 托盘统一图标。

## 注意

- 平台包**不含 `plugins/`**：宿主首次启动会自动创建空目录，插件由插件包提供
- WebUI 默认监听 `0.0.0.0`（局域网可达），请勿把端口直接暴露到公网
- 升级时保留 `memory/`（插件配置/记忆/身份绑定表）与 `logs/`，其余覆盖即可
- 通道：默认 OneBot（LLBot / NapCat）；可切到 QQ 官方机器人开放平台，其能力为前者的子集
- 包内另附 `使用说明.txt`；插件包与宿主是两条发布线，包名都带版本号
