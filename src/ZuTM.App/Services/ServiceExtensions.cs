// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using ZuTM.Core.Qemu;

namespace ZuTM.App.Services;

public static class ServiceExtensions
{
    public static int RunningCount(this VmLibraryService library) =>
        library.VirtualMachines.Count(vm => vm.Status == VmStatus.Running);
}
