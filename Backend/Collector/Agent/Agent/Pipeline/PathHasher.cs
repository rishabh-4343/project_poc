using System.IO.Hashing;
using System.Text;

namespace Auditor.Agent.Pipeline;

// path_hash = xxHash64(seed 0, UTF-8 of lower(path), trailing '\' stripped), per contract section 2. DDC matches on this, so do not change it.
public static class PathHasher
{
    public static long Hash(string path)
    {
        var normalized = path.TrimEnd('\\').ToLowerInvariant();
        return unchecked((long)XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(normalized), 0));
    }
}
