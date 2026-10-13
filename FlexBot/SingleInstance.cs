using System.Security.Cryptography;
using System.Text;

namespace FlexBot;

/// <summary>
/// 单实例保护：同一份程序（按 exe 所在目录区分）只允许一个宿主在跑。
///
/// 为什么：两个宿主同时连同一个 OneBot 实现，会**重复回复**同一条消息（AI 回复尤其明显），
/// 还会互抢日志文件与端口；而"多开"往往是拖了两个快捷方式或者上次没退干净造成的。
///
/// 实现：命名互斥量（Windows 是内核对象，Unix 由 .NET 用 /tmp 下的文件模拟），
/// 名字按 exe 目录取哈希 ⇒ **不同目录的副本（如沙箱、另一份部署）互不影响**。
/// 进程被强杀时操作系统会释放，不会留下"僵尸锁"。
///
/// 需要故意多开（例如并行测试）时加 <c>--allow-multiple</c>。
/// </summary>
static class SingleInstance
{
    private static Mutex? _mutex;

    private static string LockName =>
        "FlexBot.Host." + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))))[..16];

    /// <summary>尝试取得单实例所有权；返回 false 表示已有实例在跑（ownerHint 是它的 WebUI 地址，可能为空）</summary>
    public static bool TryAcquire(out string ownerHint)
    {
        ownerHint = "";
        try
        {
            _mutex = new Mutex(initiallyOwned: true, LockName, out var createdNew);
            if (createdNew) return true;

            try { _mutex.Dispose(); } catch { }
            _mutex = null;
            ownerHint = ReadOwnerHint();
            return false;
        }
        catch (Exception ex)
        {
            // 单实例只是便利与防重复，检查本身失败不能拦住启动
            Console.WriteLine($"[boot] 单实例检查失败（忽略）: {ex.Message}");
            return true;
        }
    }

    /// <summary>把当前实例的 WebUI 地址写到 memory/webui.url，供被拒绝的后来者提示用户</summary>
    public static void WriteOwnerHint(string memoryDir, string url)
    {
        try
        {
            Directory.CreateDirectory(memoryDir);
            File.WriteAllText(Path.Combine(memoryDir, "webui.url"), url + Environment.NewLine);
        }
        catch { }
    }

    private static string ReadOwnerHint()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "memory", "webui.url");
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        }
        catch { return ""; }
    }
}
