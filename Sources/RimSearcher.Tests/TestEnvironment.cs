using System.Runtime.CompilerServices;
using RimSearcher.Output;

namespace RimSearcher.Tests;

/// <summary>
/// 测试进程不继承开发机的 run-log 环境变量。
///
/// 变量有值时 CLI 判定「有 hook 在读 run-log」,教学句不进 stdout —— 从外壳继承到它的机器上,
/// 逐字节比对的输出快照会整片失败,而失败的原因不在被测代码里。要测 run-log 的用例
/// (<see cref="RunLogTests"/>)起子进程并显式设置它,不受这里影响。
/// </summary>
internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void ClearInheritedRunLog() => Environment.SetEnvironmentVariable(RunLog.EnvVar, null);
}
