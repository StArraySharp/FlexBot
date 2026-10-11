# JsonKit —— Newtonsoft.Json 工具（csx 脚本插件）

演示**第三方 NuGet 依赖由宿主自动解析**：这个插件的包里**不含任何 DLL**，
只有 `main.cs` 与 `plugin.json`，依赖写在一行声明里：

```json
"NuGet": ["Newtonsoft.Json@13.0.4"]
```

宿主加载插件时自动完成：下载 nupkg（含传递依赖）→ 挑最合适的 `lib/{tfm}` 资产 → 铺进
`plugins/JsonKit/libs/` → 编译期当 MetadataReference → 运行期由 ALC 从影子目录解析。
全过程不需要 csproj、不需要 dotnet SDK、不需要外部脚本。下载缓存在 `.cache/nuget/`。

## 安装

WebUI 插件页 →「添加插件」→ 选 `samples/csx-json.zip`（会自动下载 Newtonsoft.Json）；
或把 `main.cs` / `plugin.json` 放进 `plugins/JsonKit/` 后点「刷新」再「加载」。

## 用法

群员（任何人）：

```
json {"a":1,"b":[1,2,3]}        美化并回报 类型/深度
json [1,2,{"x":[true,null]}]    嵌套数组也行
```

管理员命令：

```
!jsonver                         Newtonsoft.Json 版本与程序集实际加载路径
!jsonmin {"a": 1, "b": [1,2,3]}  压缩成单行
!plugin nuget JsonKit            手动（重新）解析 NuGet 依赖
```

`MaxChars` 可在插件设置里调（美化结果超过该长度会截断，默认 1500）。

## 它验证了什么

- 宿主的 NuGet 解析器：下载 → TFM 优选 → 铺 `libs/` → 与影子目录/ALC/热重载全部兼容
- 编译期：Roslyn 对着下载来的 `Newtonsoft.Json.dll` 编译通过
- 运行期：`typeof(JsonConvert).Assembly.Location` 指向
  `plugins/.shadow/JsonKit_<ticks>/libs/Newtonsoft.Json.dll`（证明是从影子副本加载的）
- 加载自检：`record` 序列化 → 反序列化 → 字段比对，日志里会打印 `→ OK`

## 注意

包版本要写精确版本号。少数包在构建期要求授权（例如 `SixLabors.ImageSharp` ≥ 3.2 会报
`No Six Labors license found`），这类包请选用不需要授权键的版本，或先取得授权。
