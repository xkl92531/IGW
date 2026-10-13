using System.Reflection;
using IGW.Abstractions;
using Xunit;

namespace IGW.Tests;

public class ProtocolOperationTimingTests
{
    [Fact]
    public async Task Disposed_scope_is_not_updated_by_later_calls_without_a_scope()
    {
        var scope = Begin();
        await Measure(() => Task.FromResult(1));
        scope.Dispose();
        await Measure(() => Task.FromResult(2));
        Assert.Equal(1, Field<int>(Snapshot(scope), "Requests"));
    }

    private static Type TimingType
    {
        get
        {
            var type = typeof(StatusCode).Assembly.GetType("IGW.Abstractions.ProtocolOperationTiming");
            Assert.NotNull(type);
            return type;
        }
    }
    private static IDisposable Begin() => (IDisposable)TimingType.GetMethod("Begin")!.Invoke(null, null)!;
    private static Task<int> Measure(Func<Task<int>> action) => (Task<int>)TimingType.GetMethods()
        .Single(method => method.Name == "MeasureAsync" && method.GetParameters().Length == 1)
        .MakeGenericMethod(typeof(int)).Invoke(null, [action])!;
    private static object Snapshot(IDisposable scope) => TimingType.GetMethod("Snapshot")!.Invoke(scope, null)!;
    private static T Field<T>(object value, string name) => (T)value.GetType().GetProperty(name)!.GetValue(value)!;

    [Fact]
    public async Task Records_each_actual_attempt_and_preserves_failure_and_cancellation()
    {
        using var scope = Begin();
        Assert.Equal(42, await Measure(() => Task.FromResult(42)));
        await Assert.ThrowsAsync<IOException>(() => Measure(() => Task.FromException<int>(new IOException("failed"))));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Measure(() => Task.FromCanceled<int>(new CancellationToken(true))));
        var snapshot = Snapshot(scope);
        Assert.Equal(3, Field<int>(snapshot, "Requests"));
        Assert.Equal(1, Field<int>(snapshot, "FailedRequests"));
        Assert.Equal(1, Field<int>(snapshot, "CancelledRequests"));
        Assert.True(Field<double>(snapshot, "RequestTotalMs") >= Field<double>(snapshot, "RequestMaxMs"));
    }

    [Fact]
    public async Task Concurrent_child_calls_accumulate_in_one_operation_and_nested_scopes_restore_parent()
    {
        using var parent = Begin();
        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() => Measure(async () => { await Task.Yield(); return 1; }))));
        using (var child = Begin())
        {
            await Measure(() => Task.FromResult(1));
            Assert.Equal(1, Field<int>(Snapshot(child), "Requests"));
        }
        await Measure(() => Task.FromResult(1));
        Assert.Equal(65, Field<int>(Snapshot(parent), "Requests"));
    }

    [Fact]
    public async Task Independent_async_operations_do_not_share_metrics_and_queue_is_not_a_request()
    {
        async Task<object> Run(int count)
        {
            using var scope = Begin();
            TimingType.GetMethod("AddQueue")!.Invoke(null, [12.5]);
            for (int i = 0; i < count; i++) await Measure(async () => { await Task.Yield(); return 1; });
            return Snapshot(scope);
        }
        var snapshots = await Task.WhenAll(Run(2), Run(5));
        Assert.Equal(new[] { 2, 5 }, snapshots.Select(value => Field<int>(value, "Requests")));
        Assert.All(snapshots, value => Assert.Equal(12.5, Field<double>(value, "QueueMs")));
    }
}
