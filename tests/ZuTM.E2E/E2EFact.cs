// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using Xunit;

namespace ZuTM.E2E;

/// <summary>
/// End-to-end tests opt in via ZUTM_E2E=1: they touch real disks, sockets,
/// and (when fetched) a real QEMU binary — slower than unit tests and
/// environment-sensitive, so CI runs them in a dedicated job.
/// </summary>
public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ZUTM_E2E") != "1")
        {
            Skip = "Set ZUTM_E2E=1 to enable end-to-end tests.";
        }
    }
}
