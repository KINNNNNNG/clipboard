using Clipboard.Windows.Views;
using Xunit;

namespace Clipboard.Windows.Tests.Views;

public sealed class UiOperationRunnerTests
{
    [Fact]
    public async Task Failure_is_contained_and_reports_only_the_supplied_message()
    {
        string? reported = null;
        var runner = new UiOperationRunner(message => reported = message);

        bool succeeded = await runner.RunAsync(
            () => Task.FromException(new InvalidOperationException("sensitive clipboard body")),
            "操作失败，请重试。");

        Assert.False(succeeded);
        Assert.Equal("操作失败，请重试。", reported);
        Assert.DoesNotContain("sensitive", reported, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Successful_operation_does_not_report_an_error()
    {
        int reports = 0;
        var runner = new UiOperationRunner(_ => reports++);

        bool succeeded = await runner.RunAsync(() => Task.CompletedTask, "失败");

        Assert.True(succeeded);
        Assert.Equal(0, reports);
    }

    [Fact]
    public async Task Cancellation_is_contained_without_showing_an_error()
    {
        int reports = 0;
        var runner = new UiOperationRunner(_ => reports++);

        bool succeeded = await runner.RunAsync(
            () => Task.FromCanceled(new CancellationToken(canceled: true)),
            "失败");

        Assert.False(succeeded);
        Assert.Equal(0, reports);
    }
}
