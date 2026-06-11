using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Tests for AbortController/AbortSignal functionality. Runs against both interpreter and compiler.
/// </summary>
public class AbortControllerTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AbortController_CreateAndAccessSignal(ExecutionMode mode)
    {
        var source = @"
            const controller = new AbortController();
            console.log(controller.signal.aborted);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("false\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AbortController_AbortSetsAborted(ExecutionMode mode)
    {
        var source = @"
            const controller = new AbortController();
            controller.abort();
            console.log(controller.signal.aborted);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AbortController_AbortWithReason(ExecutionMode mode)
    {
        var source = @"
            const controller = new AbortController();
            controller.abort(""custom reason"");
            console.log(controller.signal.reason);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("custom reason\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AbortController_AbortWithDefaultReason(ExecutionMode mode)
    {
        var source = @"
            const controller = new AbortController();
            controller.abort();
            const reason: string = controller.signal.reason as string;
            console.log(reason.includes(""AbortError""));
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AbortSignal_AddEventListener(ExecutionMode mode)
    {
        var source = @"
            const controller = new AbortController();
            let called = false;
            controller.signal.addEventListener(""abort"", () => {
                called = true;
            });
            controller.abort();
            console.log(called);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AbortSignal_MultipleListeners(ExecutionMode mode)
    {
        var source = @"
            const controller = new AbortController();
            let count = 0;
            controller.signal.addEventListener(""abort"", () => { count++; });
            controller.signal.addEventListener(""abort"", () => { count++; });
            controller.signal.addEventListener(""abort"", () => { count++; });
            controller.abort();
            console.log(count);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("3\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AbortSignal_RemoveEventListener(ExecutionMode mode)
    {
        var source = @"
            const controller = new AbortController();
            let called = false;
            const listener = () => { called = true; };
            controller.signal.addEventListener(""abort"", listener);
            controller.signal.removeEventListener(""abort"", listener);
            controller.abort();
            console.log(called);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("false\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AbortSignal_OnAbortProperty(ExecutionMode mode)
    {
        var source = @"
            const controller = new AbortController();
            let called = false;
            controller.signal.onabort = () => { called = true; };
            controller.abort();
            console.log(called);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AbortSignal_ThrowIfAborted_NotAborted(ExecutionMode mode)
    {
        var source = @"
            const controller = new AbortController();
            try {
                controller.signal.throwIfAborted();
                console.log(""no throw"");
            } catch (e) {
                console.log(""threw"");
            }
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("no throw\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AbortSignal_ThrowIfAborted_Aborted(ExecutionMode mode)
    {
        var source = @"
            const controller = new AbortController();
            controller.abort();
            try {
                controller.signal.throwIfAborted();
                console.log(""no throw"");
            } catch (e) {
                console.log(""threw"");
            }
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("threw\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AbortSignal_StaticAbort(ExecutionMode mode)
    {
        var source = @"
            const signal = AbortSignal.abort();
            console.log(signal.aborted);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AbortSignal_StaticAbortWithReason(ExecutionMode mode)
    {
        var source = @"
            const signal = AbortSignal.abort(""my reason"");
            console.log(signal.aborted);
            console.log(signal.reason);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\nmy reason\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AbortSignal_StaticTimeout(ExecutionMode mode)
    {
        var source = @"
            const signal = AbortSignal.timeout(10000);
            console.log(signal.aborted);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("false\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AbortSignal_Any_AbortsWhenAnyAborts(ExecutionMode mode)
    {
        var source = @"
            const c1 = new AbortController();
            const c2 = new AbortController();
            const combined = AbortSignal.any([c1.signal, c2.signal]);
            console.log(combined.aborted);
            c1.abort(""first aborted"");
            console.log(combined.aborted);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("false\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AbortSignal_Any_AlreadyAborted(ExecutionMode mode)
    {
        var source = @"
            const c1 = new AbortController();
            c1.abort(""already done"");
            const combined = AbortSignal.any([c1.signal]);
            console.log(combined.aborted);
            console.log(combined.reason);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\nalready done\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AbortController_AbortIdempotent(ExecutionMode mode)
    {
        var source = @"
            const controller = new AbortController();
            let count = 0;
            controller.signal.addEventListener(""abort"", () => { count++; });
            controller.abort();
            controller.abort();
            console.log(count);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("1\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AbortSignal_InstanceOf_BrandChecks(ExecutionMode mode)
    {
        // #246: interpreter returned false for real signals (no brand linkage
        // on the AbortSignal sentinel); compiled returned true for ANY plain
        // object (Dictionary IsAssignableFrom fallback).
        var source = @"
            const s: any = AbortSignal.abort('x');
            console.log(s instanceof (AbortSignal as any));
            console.log(({} as any) instanceof (AbortSignal as any));
            const c = new AbortController();
            console.log(c.signal instanceof (AbortSignal as any));
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\nfalse\ntrue\n", output);
    }
}
