using System.Reflection;
using RimSearcher.Server;

namespace RimSearcher.Tests;

// 版本号曾经有两处：UpdateChecker 里硬编码 "2.7"，而 csproj 没设 <Version>，程序集元数据停在
// 默认的 1.0.0.0。同一个二进制自报 2.7、exe 属性页写 1.0.0.0，用户报 bug 时两边对不上号。
// 现在唯一来源是 Sources/Directory.Build.props 的 <Version>；这些测试就是那条「改一处忘另一处」
// 的绊线——两侧一旦脱钩立刻红，而不是等到下次发版才被用户发现。
public class VersionTests
{
    private static Assembly ServerAssembly => typeof(UpdateChecker).Assembly;

    [Fact]
    public void CurrentVersion_ComesFromTheAssemblyMetadata()
    {
        var informational = ServerAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        Assert.NotNull(informational);

        // 构建元数据（"+<commit>"）与预发布段是运行时故意切掉的，比较时同样切一次
        var expected = informational.InformationalVersion.Split('-', '+')[0];

        Assert.Equal(expected, UpdateChecker.CurrentVersion);
    }

    // AssemblyVersion / FileVersion 也由同一个 <Version> 推导，只是被规范化成四段（2.7 → 2.7.0.0）。
    // 钉住主次号：只要有人只改了 UpdateChecker 或只改了 props，这里就会对不上。
    [Fact]
    public void AssemblyVersion_AgreesWithTheReportedVersion()
    {
        var assemblyVersion = ServerAssembly.GetName().Version;
        Assert.NotNull(assemblyVersion);

        Assert.Equal($"{assemblyVersion.Major}.{assemblyVersion.Minor}", UpdateChecker.CurrentVersion);
    }

    // UpdateChecker.IsNewer 是 Split('.') 逐段 int.Parse，异常被它自己 catch 掉。
    // 于是 CurrentVersion 里混进任何非数字段都不会报错，只会让更新提示永久静默——
    // 这条测试替那个被吞掉的异常说话。
    [Fact]
    public void CurrentVersion_StaysComparableByIsNewer()
    {
        var parts = UpdateChecker.CurrentVersion.Split('.');

        Assert.NotEmpty(parts);
        Assert.All(parts, part => Assert.True(
            int.TryParse(part, out _),
            $"版本段 '{part}' 不是整数，IsNewer 会静默失效：{UpdateChecker.CurrentVersion}"));
    }
}
