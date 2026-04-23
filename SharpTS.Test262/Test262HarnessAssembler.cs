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
