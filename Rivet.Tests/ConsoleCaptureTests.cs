namespace Rivet.Tests;

/// <summary>
/// The console capture sees only what its own flow writes: test classes run in parallel, and a
/// warning another test prints mustn't land in this test's capture.
/// </summary>
public sealed class ConsoleCaptureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Capture_Excludes_Writes_From_Another_Flow(bool stdOut)
    {
        var captured = Capture(
            stdOut,
            () =>
            {
                Write(stdOut, "own-flow-line");
                // UnsafeStart flows no ExecutionContext, as a test in another class has none of ours.
                var other = new Thread(() => Write(stdOut, "other-flow-line"));
                other.UnsafeStart();
                other.Join();
            }
        );

        Assert.Contains("own-flow-line", captured);
        Assert.DoesNotContain("other-flow-line", captured);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Capture_Follows_Its_Flow_Across_An_Await(bool stdOut)
    {
        var captured = Capture(stdOut, () => WriteAfterAwait(stdOut).GetAwaiter().GetResult());

        Assert.Contains("after-await-line", captured);
    }

    [Fact]
    public void A_Nested_Capture_Takes_Its_Own_Lines_And_Restores_The_Outer_One()
    {
        var inner = "";
        var outer = CompilationHelper.CaptureStdErr(() =>
        {
            inner = CompilationHelper.CaptureStdErr(() => Console.Error.WriteLine("inner-line"));
            Console.Error.WriteLine("outer-line");
        });

        Assert.Contains("inner-line", inner);
        Assert.DoesNotContain("outer-line", inner);
        Assert.Contains("outer-line", outer);
        Assert.DoesNotContain("inner-line", outer);
    }

    private static async Task WriteAfterAwait(bool stdOut)
    {
        await Task.Delay(1).ConfigureAwait(false);
        Write(stdOut, "after-await-line");
    }

    private static string Capture(bool stdOut, Action action) =>
        stdOut ? CompilationHelper.CaptureStdOut(action) : CompilationHelper.CaptureStdErr(action);

    private static void Write(bool stdOut, string line) =>
        (stdOut ? Console.Out : Console.Error).WriteLine(line);
}
