using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using FlexBot.PluginApi;
using FlexBot.Scripting;

namespace FlexBot;

// ② 拆分：卸载 / 重载 + 插件设置 —— 从 PluginManager.cs 纯搬移至此（逻辑未改，partial 保证一致性）。
sealed partial class PluginManager
{
    // ===================== 卸载 / 重载 =====================

    // 卸载：自动先级联卸载全部依赖方（a 依赖 b：卸 b 时连 a 一起卸）
    public async Task<bool> UnloadAsync(string name)
    {
        if (!IsSafePluginName(name)) { Console.WriteLine($"[plugin] 卸载被拒绝：非法插件名 {name}"); return false; }
        if (PluginOpsBlocked("unload", name)) return false;
        return await UnloadAsyncCore(name, viaCascade: false);
    }

    private async Task<bool> UnloadAsyncCore(string name, bool viaCascade)
    {
        LoadedPlugin? p;
        List<string> cascade = [];
        lock (_lock)
        {
            if (!_plugins.Remove(name, out p))
            {
                if (!viaCascade) Console.WriteLine($"[plugin] 插件 {name} 未加载");
                return false;
            }
            // 收集依赖方（级联卸载：a 依赖我 → 先卸 a）
            cascade = [.. p.Dependents];
            foreach (var dep in p.Manifest.Depends)
                if (_plugins.TryGetValue(dep, out var dp))
                    dp.Dependents.Remove(name);
        }

        // 先卸载依赖方（它们引用了我的程序集实例）
        foreach (var dependent in cascade)
        {
            Console.WriteLine($"[plugin] {name} 被 {dependent} 依赖，级联卸载 {dependent}");
            await UnloadAsyncCore(dependent, viaCascade: true);
        }

        var displayName = p.Instance.Name;
        try
        {
            await p.Instance.OnUnloadAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[plugin] {displayName} OnUnloadAsync 异常: {ex.Message}");
        }
        p.Context.Dispose(); // 兜底退订全部消息/事件/命令
        RemoveCommandsOf(name);

        var alc = p.Alc;
        var alcRef = p.AlcRef;
        var shadowDir = p.ShadowDir;
        p.Instance = null!;

        alc.Unload();
        Console.WriteLine($"[plugin] 已卸载 {displayName}，等待程序集回收...");

        // 后台验证 ALC 真正被 GC 回收（有泄漏时会一直 IsAlive）
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 150; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                if (!alcRef.IsAlive)
                {
                    Console.WriteLine($"[plugin] {displayName} 程序集已回收 ({i * 0.2:F1}s)");
                    try { if (Directory.Exists(shadowDir)) Directory.Delete(shadowDir, true); } catch { }
                    return;
                }
                await Task.Delay(200);
            }
            Console.WriteLine($"[plugin] [warn] {displayName} 程序集 30s 仍未回收（可能存在残留引用/未停止的后台任务），不影响继续使用");
        });
        return true;
    }

    // 重载：级联（a 依赖 b：重载 b 时先卸 a 连带重载；A 借用的 B 类型必须换新）
    public async Task<bool> ReloadAsync(string name)
    {
        if (!IsSafePluginName(name)) { Console.WriteLine($"[plugin] 重载被拒绝：非法插件名 {name}"); return false; }
        if (PluginOpsBlocked("reload", name)) return false;

        // 收集依赖方链（卸载会级联），重载时一并恢复
        List<string> dependents;
        lock (_lock) dependents = [.. (_plugins.TryGetValue(name, out var p) ? p.Dependents : new HashSet<string>())];

        await UnloadAsync(name); // 内部级联卸载依赖方
        await Task.Delay(300); // 给 ALC 卸载一点时间
        if (!await LoadAsync(name)) return false;

        // 恢复依赖方（保持原依赖顺序无所谓，LoadAsync 会递归处理依赖）
        var restored = true;
        foreach (var d in dependents)
            restored &= await LoadAsync(d);
        return restored;
    }

    public bool IsLoaded(string name)
    {
        lock (_lock) return _plugins.ContainsKey(name);
    }

    // ===================== 插件设置 =====================

    /// <summary>设置项定义：优先插件类声明的 SettingDefs，其次 plugin.json 的 Settings（脚本插件走后者）</summary>
    public IReadOnlyList<PluginSettingDef> GetSettingDefs(string name)
    {
        if (!IsSafePluginName(name)) return [];
        lock (_lock)
            if (_plugins.TryGetValue(name, out var p))
                return p.Instance.SettingDefs.Count > 0 ? p.Instance.SettingDefs : p.Manifest.Settings;
        return PluginManifest.LoadFrom(Path.Combine(PluginRoot, name)).Settings;
    }

    /// <summary>当前设置值（插件未加载也返回持久化值）</summary>
    public Dictionary<string, object?> GetSettings(string name)
    {
        if (!IsSafePluginName(name)) return [];
        var dict = LoadPluginSettings(name);
        return dict.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>保存设置：仅接受 defs 声明的键，按类型规范化后写插件目录的 settings.json；
    /// 插件已加载时回调 OnSettingsChangedAsync 热应用</summary>
    public async Task<(bool Ok, string? Error)> UpdateSettingsAsync(string name, Dictionary<string, object?>? values)
    {
        if (!IsSafePluginName(name)) return (false, "非法插件名");
        try
        {
            var existing = LoadPluginSettings(name);
            LoadedPlugin? p = null;
            lock (_lock) _plugins.TryGetValue(name, out p);
            // 设置项来源：插件类声明优先，其次 plugin.json 的 Settings（脚本插件靠清单声明）
            var defs = p is not null && p.Instance.SettingDefs.Count > 0
                ? p.Instance.SettingDefs
                : PluginManifest.LoadFrom(Path.Combine(PluginRoot, name)).Settings;
            // 无任何声明就不接收写入：否则空集合会把已有 settings.json 覆盖掉
            if (defs.Count == 0)
                return (false, "该插件没有声明可配置项");

            var clean = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var def in defs)
            {
                if (values is not null && values.TryGetValue(def.Key, out var raw) && raw is not null)
                {
                    clean[def.Key] = NormalizeSetting(raw, def.Type);
                }
                else if (existing.TryGetValue(def.Key, out var prev))
                {
                    clean[def.Key] = prev; // 未提交的项保留旧值
                }
                else if (def.Default is not null)
                {
                    clean[def.Key] = NormalizeSetting(def.Default, def.Type);
                }
            }
            existing.Clear();
            foreach (var item in clean) existing[item.Key] = item.Value;
            SavePluginSettings(name, existing, defs);

            // 这些键在插件构造时读取，变更必须热重载才生效
            var reloadKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "ApiKey", "BaseUrl", "Model", "FallbackModels", "Personas" };
            var needsReload = values is not null && values.Keys.Any(k => reloadKeys.Contains(k));

            if (p is not null)
            {
                if (needsReload)
                {
                    Console.WriteLine($"[plugin] {name} 关键设置变更，自动热重载生效…");
                    var reloaded = await ReloadAsync(name);
                    Console.WriteLine($"[plugin] {name} 热重载 {(reloaded ? "完成" : "失败")}");
                }
                else
                {
                    await p.Instance.OnSettingsChangedAsync();
                    Console.WriteLine($"[plugin] {name} 设置已更新并热应用");
                }
            }
            else
            {
                Console.WriteLine($"[plugin] {name} 设置已保存（插件未加载，下次加载生效）");
            }
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // 按设置类型规范化为 JsonElement（WebUI 提交值可能是 JsonElement/基础类型）
    private static JsonElement NormalizeSetting(object raw, string? type)
    {
        var s = raw is JsonElement je ? je.ToString() : raw.ToString() ?? "";
        return (type ?? "text") switch
        {
            "number" when raw is JsonElement n && n.ValueKind == JsonValueKind.Number => n,
            "number" => double.TryParse(s, out var d) ? JsonSerializer.SerializeToElement(d) : JsonSerializer.SerializeToElement(0),
            "bool" when raw is JsonElement b && b.ValueKind is JsonValueKind.True or JsonValueKind.False => b,
            "bool" => JsonSerializer.SerializeToElement(s is "true" or "True" or "1" or "on"),
            _ => JsonSerializer.SerializeToElement(s)
        };
    }

    private string PluginSettingsPath(string name)
    {
        // 最后一道兜底：即使有入口漏了校验，这里也不接受能跑出 PluginRoot 的名字
        if (!IsSafePluginName(name)) throw new ArgumentException($"非法插件名: {name}");
        return Path.Combine(PluginRoot, name, "settings.json");
    }

    private Dictionary<string, JsonElement> LoadPluginSettings(string name)
    {
        if (!IsSafePluginName(name)) return [];
        if (_pluginSettings.TryGetValue(name, out var cached)) return cached;

        var path = PluginSettingsPath(name);
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                return _pluginSettings[name] = DecryptSettings(values ?? new(StringComparer.OrdinalIgnoreCase));
            }

            // 将旧 config.json 中的嵌套 PluginSettings 自动迁移一次。
            if (_settings.PluginSettings.TryGetValue(name, out var legacy))
            {
                SavePluginSettings(name, legacy);
                _settings.PluginSettings.Remove(name);
                _settings.Save();
                Console.WriteLine($"[plugin] 已迁移 {name} 设置到 {path}");
                return _pluginSettings[name] = legacy;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[plugin] 读取 {name} 设置失败: {ex.Message}");
        }
        return _pluginSettings[name] = new(StringComparer.OrdinalIgnoreCase);
    }

    private void SavePluginSettings(string name, Dictionary<string, JsonElement> values, IReadOnlyList<PluginSettingDef>? defs = null)
    {
        var path = PluginSettingsPath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        // 落盘才加密（内存与插件始终拿明文）：只处理 defs 声明为 password 的键，且已是密文的不再套一层
        var disk = values;
        if (defs is { Count: > 0 })
        {
            var secretKeys = defs.Where(d => string.Equals(d.Type, "password", StringComparison.OrdinalIgnoreCase))
                                 .Select(d => d.Key).ToList();
            if (secretKeys.Count > 0)
            {
                disk = new Dictionary<string, JsonElement>(values, StringComparer.OrdinalIgnoreCase);
                foreach (var k in secretKeys)
                    if (disk.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String)
                        disk[k] = JsonSerializer.SerializeToElement(SecretBox.Encrypt(v.GetString()));
            }
        }
        // 中文/特殊字符原样存储，不转 \uXXXX，便于人工编辑与 git diff
        File.WriteAllText(temp, JsonSerializer.Serialize(disk, BotJson.Indented));
        File.Move(temp, path, true);
    }

    /// <summary>
    /// 一次性迁移：把磁盘上仍是明文的机密设置（defs 里 type=password 的键）重新保存一遍，
    /// 从而被 SecretBox 加密。开机调用一次；已是密文的键不会被二次加密。
    /// </summary>
    public void EncryptExistingSecrets()
    {
        try
        {
            if (!SecretBox.IsReady) return;
            foreach (var dir in Directory.GetDirectories(PluginRoot))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith('.')) continue;
                if (!IsSafePluginName(name)) continue;
                if (!File.Exists(Path.Combine(dir, "settings.json"))) continue;

                var defs = GetSettingDefs(name);
                var secretKeys = defs.Where(d => string.Equals(d.Type, "password", StringComparison.OrdinalIgnoreCase))
                                     .Select(d => d.Key).ToList();
                if (secretKeys.Count == 0) continue;

                var values = LoadPluginSettings(name);
                var plain = secretKeys.Where(k => values.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String
                                                  && !SecretBox.IsEncrypted(v.GetString())).ToList();
                if (plain.Count == 0) continue;
                SavePluginSettings(name, values, defs);
                Console.WriteLine($"[secret] 已加密 {name} 的机密设置项: {string.Join(", ", plain)}");
            }
        }
        catch (Exception ex) { Console.WriteLine($"[secret] 迁移明文机密失败: {ex.Message}"); }
    }
    /// <summary>读取时把 enc:v1: 字符串解成明文（插件只应看到明文；非密文原样保留）</summary>
    private static Dictionary<string, JsonElement> DecryptSettings(Dictionary<string, JsonElement> src)
    {
        foreach (var key in src.Keys.ToList())
        {
            if (src[key].ValueKind != JsonValueKind.String) continue;
            var s = src[key].GetString();
            if (SecretBox.IsEncrypted(s))
                src[key] = JsonSerializer.SerializeToElement(SecretBox.Decrypt(s));
        }
        return src;
    }

    // 旧全局配置（模型/人格/前缀/Chat 设置）→ 插件设置 的一次性迁移（仅写缺失键，不覆盖用户已改值）
    public void MigrateLegacyHostSettings()
    {
        try
        {
            var agentPath = PluginSettingsPath("Agent");
            var agent = File.Exists(agentPath)
                ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(agentPath)) ?? new(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            var changed = false;

            // 旧 Chat 设置（插件改名前）→ Agent
            var oldChatPath = PluginSettingsPath("Chat");
            if (File.Exists(oldChatPath) && !File.Exists(agentPath))
            {
                foreach (var kv in JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(oldChatPath))
                             ?? new(StringComparer.OrdinalIgnoreCase))
                    agent[kv.Key] = kv.Value;
                changed = true;
                Console.WriteLine("[plugin] 已迁移 Chat 设置 → Agent");
            }

            // 全局模型/备用/人格 → Agent
            if (!string.IsNullOrWhiteSpace(_settings.Model) && PathExistsKey(agentPath, "Model") is false)
            {
                agent["ApiKey"] = JsonSerializer.SerializeToElement(_settings.ApiKey);
                agent["BaseUrl"] = JsonSerializer.SerializeToElement(_settings.BaseUrl);
                agent["Model"] = JsonSerializer.SerializeToElement(_settings.Model);
                if (_settings.FallbackModels is { Count: > 0 })
                    agent["FallbackModels"] = JsonSerializer.SerializeToElement(string.Join("\n",
                        _settings.FallbackModels.Select(f => $"{f.BaseUrl}|{f.Model}|{f.ApiKey}")));
                changed = true;
                Console.WriteLine("[plugin] 已迁移全局模型配置 → Agent 设置");
            }
            if (_settings.Personas is { Count: > 0 } && PathExistsKey(agentPath, "Personas") is false)
            {
                var active = _settings.Personas.FirstOrDefault(p => p.Enabled) ?? _settings.Personas[0];
                agent["Personas"] = JsonSerializer.SerializeToElement(new[]
                {
                    new { name = active.Name, enabled = true, instructions = active.Instructions }
                });
                changed = true;
                Console.WriteLine("[plugin] 已迁移人格 → Agent 设置（Personas）");
            }
            // 旧管线格式 FallbackModels（"url|model|key" 每行）→ JSON 数组
            if (agent.TryGetValue("FallbackModels", out var fbRaw) && fbRaw.ValueKind == JsonValueKind.String)
            {
                var lines = (fbRaw.GetString() ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length > 0)
                {
                    var arr = lines.Select(l =>
                    {
                        var p = l.Split('|');
                        return new { baseUrl = p[0].Trim(), model = p.ElementAtOrDefault(1)?.Trim() ?? "", apiKey = p.ElementAtOrDefault(2)?.Trim() ?? "" };
                    }).Where(x => x.model.Length > 0).ToList();
                    agent["FallbackModels"] = JsonSerializer.SerializeToElement(arr);
                    changed = true;
                    Console.WriteLine("[plugin] 备用模型格式已升级为结构化数组");
                }
            }
            // 旧单人格键 → Personas 数组
            if (agent.ContainsKey("PersonaInstructions") && !agent.ContainsKey("Personas"))
            {
                agent["Personas"] = JsonSerializer.SerializeToElement(new[]
                {
                    new { name = agent.TryGetValue("PersonaName", out var n) ? n.GetString() : "默认人格",
                          enabled = true,
                          instructions = agent["PersonaInstructions"].GetString() ?? "" }
                });
                agent.Remove("PersonaName");
                agent.Remove("PersonaInstructions");
                changed = true;
                Console.WriteLine("[plugin] 单人格键已升级为 Personas 数组");
            }
            if (changed)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(agentPath)!);
                File.WriteAllText(agentPath, JsonSerializer.Serialize(agent, BotJson.Indented));
            }

            // 命令前缀 → Admin
            if (!string.IsNullOrEmpty(_settings.CommandPrefix) && _settings.CommandPrefix != "!")
            {
                var adminPath = PluginSettingsPath("Admin");
                if (PathExistsKey(adminPath, "CommandPrefix") is false)
                {
                    var admin = File.Exists(adminPath)
                        ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(adminPath)) ?? new(StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                    admin["CommandPrefix"] = JsonSerializer.SerializeToElement(_settings.CommandPrefix);
                    Directory.CreateDirectory(Path.GetDirectoryName(adminPath)!);
                    File.WriteAllText(adminPath, JsonSerializer.Serialize(admin, BotJson.Indented));
                    Console.WriteLine("[plugin] 已迁移命令前缀 → Admin 设置");
                }
            }

            // 迁移全部完成：清掉内存中的旧全局模型/人格值（config.json 也已通过 ShouldSerialize* 不再写出）
            _settings.ClearLegacyModelSettings();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[plugin] 旧配置迁移失败（忽略，用默认值）: {ex.Message}");
        }
    }

    private static bool? PathExistsKey(string settingsPath, string key)
    {
        try
        {
            if (!File.Exists(settingsPath)) return false;
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(settingsPath))
                ?.ContainsKey(key);
        }
        catch { return null; }
    }

    // 插件快照（WebUI 用）：含已加载与磁盘上未加载的插件
    public List<PluginInfo> GetSnapshot()
    {
        var list = new List<PluginInfo>();
        var loadedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            foreach (var p in _plugins.Values)
            {
                var (src, model) = ProvenanceOf(p.Manifest, p.SourceDir);
                list.Add(new PluginInfo(p.Name, p.Instance.Version, p.Instance.Description, true, IsAutoload(p.Name), src, model));
                loadedNames.Add(p.Name);
            }
        }
        if (Directory.Exists(PluginRoot))
        {
            foreach (var dir in Directory.GetDirectories(PluginRoot))
            {
                var n = Path.GetFileName(dir);
                if (n.StartsWith('.')) continue;
                if (loadedNames.Contains(n)) continue;
                if (!IsPluginDir(dir, n)) continue;
                // 未加载时用 plugin.json 的展示信息兜底（脚本插件的版本/描述主要来自清单）
                var mf = PluginManifest.LoadFrom(dir);
                var (src, model) = ProvenanceOf(mf, dir);
                list.Add(new PluginInfo(n, mf.Version ?? "", mf.Description ?? "", false, IsAutoload(n), src, model));
            }
        }
        return list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // 是否随启动自动加载（未配置默认 true）
    public bool IsAutoload(string name) =>
        !_settings.PluginAutoload.TryGetValue(name, out var enabled) || enabled;

    /// <summary>来源判定：清单里的 Provenance 优先；缺失时看有没有生成日志（pb_build 生成过 → manual）；
    /// 两者都没有 → handmade（内置 / 手工放入 / zip 安装）</summary>
    private static (string Source, string? Model) ProvenanceOf(PluginManifest mf, string dir)
    {
        if (mf.Provenance?.By is { Length: > 0 } by)
            return (by, mf.Provenance.Model);
        return (File.Exists(Path.Combine(dir, "generation.log")) ? "manual" : "handmade", null);
    }

    public record PluginInfo(string Name, string Version, string Description, bool Loaded, bool AutoLoad,
        string Source, string? Model);

}
