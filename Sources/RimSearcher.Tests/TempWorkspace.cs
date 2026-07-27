namespace RimSearcher.Tests;

// 索引器、历史库、同步服务都直接操作文件系统，没有可替换的抽象层，
// 所以测试用真实临时目录而不是内存替身。
internal sealed class TempWorkspace : IDisposable
{
    public TempWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "rimsearcher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Dir(params string[] segments)
    {
        var path = Path.Combine([Root, .. segments]);
        Directory.CreateDirectory(path);
        return path;
    }

    public string WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(Root, relativePath);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch { /* 临时目录清理失败不该让测试变红 */ }
    }
}
