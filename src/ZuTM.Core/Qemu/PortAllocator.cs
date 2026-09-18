// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using System.Net;
using System.Net.Sockets;

namespace ZuTM.Core.Qemu;

/// <summary>Allocates free loopback TCP ports for QEMU endpoints (SPICE, QMP, serial, guest agent).</summary>
public static class PortAllocator
{
    /// <summary>Reserves one free port by binding and releasing it. Race-tolerant: QEMU binds within moments.</summary>
    public static int AllocateFreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    /// <summary>Allocates a complete endpoint set for one VM launch.</summary>
    public static QemuPortSet AllocateForLaunch(int serialCount, bool guestAgent = true)
    {
        var serialPorts = new Dictionary<int, int>();
        for (var i = 0; i < serialCount; i++)
        {
            serialPorts[i] = AllocateFreePort();
        }

        return new QemuPortSet
        {
            SpicePort = AllocateFreePort(),
            QmpPort = AllocateFreePort(),
            GuestAgentPort = guestAgent ? AllocateFreePort() : 0,
            SerialPorts = serialPorts,
        };
    }
}
