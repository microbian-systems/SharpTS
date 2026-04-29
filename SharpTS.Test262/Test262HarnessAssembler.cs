using System.Text;

namespace SharpTS.Test262;

/// <summary>
/// Concatenates the Test262 harness prelude with a test body.
///
/// For non-<c>raw</c> tests: prepend <c>sta.js</c>, <c>assert.js</c>, then each
/// file listed in <c>includes</c>, then the test body. For <c>raw</c> tests:
/// emit the body verbatim (the test carries its own setup).
/// </summary>
public sealed class Test262HarnessAssembler
{
    private readonly string _harnessDir;

    public Test262HarnessAssembler(string test262Root)
    {
        _harnessDir = Test262Paths.HarnessDir(test262Root);
    }

    /// <summary>
    /// Returns (assembledSource, harnessCharCount) where harnessCharCount is
    /// the number of characters prepended ahead of the test body. Runners use
    /// this boundary to bucket pre-body exceptions as <see cref="Test262Outcome.HarnessError"/>.
    /// </summary>
    public (string Source, int HarnessLength) Assemble(string testBody, Test262Metadata metadata)
    {
        if (metadata.IsRaw)
        {
            return (testBody, 0);
        }

        var sb = new StringBuilder();
        AppendHarnessFile(sb, "sta.js");
        AppendHarnessFile(sb, "assert.js");
        // SharpTS patch: sta.js's `Test262Error` doesn't set `this.name`. The
        // compiled-mode test runner classifies thrown JS values by reading
        // `name` off the exception's `__tsValue` to distinguish Fail (assert
        // failure) from RuntimeError. Compiled-mode also relies on prototype
        // walks for `instance.constructor`. Rather than rebind Test262Error
        // (which doesn't propagate to subsequent `new Test262Error()` calls
        // in compiled mode because function-decl identity is cached), we
        // inject the metadata onto the prototype directly. Instances inherit
        // `.name` and `.constructor` via the prototype-chain walk that
        // assert.throws and the runner classifier both rely on.
        sb.Append("Test262Error.prototype.name = \"Test262Error\";\n");
        sb.Append("Test262Error.prototype.constructor = Test262Error;\n");
        foreach (var include in metadata.Includes)
        {
            AppendHarnessFile(sb, include);
        }
        var harnessLength = sb.Length;
        sb.Append(testBody);
        return (sb.ToString(), harnessLength);
    }

    private void AppendHarnessFile(StringBuilder sb, string name)
    {
        var path = Path.Combine(_harnessDir, name);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Test262 harness file missing: {name}", path);
        sb.Append(File.ReadAllText(path));
        if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
    }
}
