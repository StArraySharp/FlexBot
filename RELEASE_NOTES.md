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

## 注意

- 平台包**不含 `plugins/`**：宿主首次启动会自动创建空目录，插件由插件包提供
- WebUI 默认监听 `0.0.0.0`（局域网可达），请勿把端口直接暴露到公网
- 升级时保留 `memory/`（插件配置/记忆/身份绑定表）与 `logs/`，其余覆盖即可
- 通道：默认 OneBot（LLBot / NapCat）；可切到 QQ 官方机器人开放平台，其能力为前者的子集
- 包内另附 `使用说明.txt`；插件包与宿主是两条发布线，包名都带版本号
