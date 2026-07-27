using RimSearcher.Server;

namespace RimSearcher.Tests;

// 席位用的是全机命名内核对象，故每个测试各用一个随机指纹，避免撞上真在跑的服务器实例。
// 注意 TryBecomeHost 会置位静态的 IndexHost.IsHost —— 本文件不依赖它。
public class IndexHostTests
{
    private static string NewFingerprint() => $"rimsearcher-test-{Guid.NewGuid():N}";

    // 回归：席位曾用 Mutex。Mutex 的所有权绑定线程，而本进程是在若干 await 之后抢到席位、
    // 又在另一批 await 之后释放的（控制台应用无同步上下文，续体落在任意线程池线程），
    // 跨线程 ReleaseMutex 会抛 ApplicationException。
    [Fact]
    public void HostSlot_CanBeReleasedFromAnotherThread()
    {
        if (!IndexHost.IsSupported) return;

        var fingerprint = NewFingerprint();
        HostSlot? slot = null;

        var acquire = new Thread(() => slot = IndexHost.TryBecomeHost(fingerprint));
        acquire.Start();
        acquire.Join();

        Assert.NotNull(slot);

        Exception? failure = null;
        var release = new Thread(() =>
        {
            try { slot!.Dispose(); }
            catch (Exception ex) { failure = ex; }
        });
        release.Start();
        release.Join();

        Assert.Null(failure);
    }

    [Fact]
    public void HostSlot_IsExclusiveUntilReleased()
    {
        if (!IndexHost.IsSupported) return;

        var fingerprint = NewFingerprint();

        var first = IndexHost.TryBecomeHost(fingerprint);
        Assert.NotNull(first);

        // 席位已被占，第二个调用者必须落到 standalone
        Assert.Null(IndexHost.TryBecomeHost(fingerprint));

        first!.Dispose();

        // 让出之后可以再抢。旧实现在 abandoned 分支上会返回一个「自认为持有席位、实则没有」
        // 的 Mutex 对象，双宿主与关机时的 ApplicationException 都从这里来。
        var second = IndexHost.TryBecomeHost(fingerprint);
        Assert.NotNull(second);
        second!.Dispose();
    }

    [Fact]
    public void HostSlot_DisposeIsIdempotent()
    {
        if (!IndexHost.IsSupported) return;

        var slot = IndexHost.TryBecomeHost(NewFingerprint());
        Assert.NotNull(slot);

        slot!.Dispose();
        slot.Dispose();
    }

    [Fact]
    public void DifferentFingerprints_GetIndependentSlots()
    {
        if (!IndexHost.IsSupported) return;

        var first = IndexHost.TryBecomeHost(NewFingerprint());
        var second = IndexHost.TryBecomeHost(NewFingerprint());

        Assert.NotNull(first);
        Assert.NotNull(second);

        first!.Dispose();
        second!.Dispose();
    }

    [Fact]
    public void BuildPipeName_IsDeterministicAndFingerprintSensitive()
    {
        Assert.Equal(IndexHost.BuildPipeName("config-a"), IndexHost.BuildPipeName("config-a"));
        Assert.NotEqual(IndexHost.BuildPipeName("config-a"), IndexHost.BuildPipeName("config-b"));
    }

    // 同名的 Mutex 与 Semaphore 无法互相打开（OpenExisting 会抛 WaitHandleCannotBeOpenedException），
    // 所以新版席位名必须与旧版的 ".mutex" 区分开，否则新旧两代进程共存时会互相打死。
    [Fact]
    public void SlotName_DoesNotCollideWithLegacyMutexName()
    {
        var slotName = IndexHost.BuildSlotName("RimSearcher.host.v1.abcdef");

        Assert.EndsWith(".slot", slotName);
        Assert.DoesNotContain(".mutex", slotName);
    }
}
