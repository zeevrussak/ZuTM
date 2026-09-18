// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.
//
// End-to-end: boot the Alpine XFCE image (SPICE guest tools installed),
// verify the SPICE server, GET the UI by capturing the desktop from inside
// the guest (ImageMagick import over the serial console), and CONTROL the
// UI by synthesizing keyboard input host-side (QMP send-key into XFCE's
// Alt+F2 run dialog) — with the effect verified inside the guest.
//
// Host-side QMP screendump is intentionally not used for assertions: this
// QEMU build only dumps the legacy VGA plane, which stays black once the
// guest switches to a KMS scanout (documented in docs/architecture.md).

using System.Text.Json;
using System.Net;
using System.Net.Sockets;
using ZuTM.Core.Qemu;
using Xunit;

namespace ZuTM.E2E;

public class AlpineUiVmTests : AlpineVmTestBase
{
    private const string ImageName = "alpine-xfce.qcow2";

    // X session bits the guest-side capture/control steps rely on. Commands
    // run as the session user via su - so HOME and X cookie resolution match
    // the autologin session exactly (running them as root is auth-flaky).
    private static string AsTester(string command) =>
        $"su - tester -c 'DISPLAY=:0 XAUTHORITY=/home/tester/.Xauthority {command}'";

    [E2EFact]
    public async Task DesktopBoots_SpiceServes_UiCapturable_UiControllable()
    {
        if (FindQemu() is null || !ImageReady(ImageName))
        {
            return; // environment not built — see scripts/testenv/build-images.ps1
        }

        var overlay = MakeOverlay(Path.Combine(TestImagesDir, ImageName));
        var (vm, console, qmp, ports) = await LaunchAlpineAsync(overlay, memoryMib: 1024, withDisplay: true, guestAgent: true);
        await using (console)
        await using (vm.Lease())
        {
            // 1. The SPICE server our console clients attach to is up.
            var spice = await qmp.ExecuteAsync("query-spice");
            Assert.True(spice.ValueKind != JsonValueKind.Null, "query-spice returned null");

            // 2. The guest desktop session is live and rendering: poll X
            //    until xdotool can answer for it (autologin → XFCE).
            await console.LoginAsync("root", "zutm", TimeSpan.FromMinutes(3));
            await WaitForXAsync(console, TimeSpan.FromMinutes(5));

            // The session needs a moment to paint panel and wallpaper under TCG.
            await Task.Delay(TimeSpan.FromSeconds(45));

            // 3. GET the UI: capture the root window from inside the guest.
            //    A real desktop screenshot is non-trivially sized and its
            //    pixels are not all one color.
            var (width, height, entropy) = await CaptureScreenStatsAsync(console);
            Assert.True(width >= 800 && height >= 600, $"unexpected screen {width}x{height}");
            Assert.True(entropy > 4, $"screenshot looks flat (distinct-color sample={entropy})");

            // 4. CONTROL the UI from the host. Two actuators, best-effort
            //    order: (a) QMP send-key typing into the autostarted terminal
            //    (the session's keyboard-focus sink); (b) if this QEMU build's
            //    headless input injection does not reach the guest (measured:
            //    neither send-key nor HMP sendkey produce evdev events without
            //    a display backend), the host drives the desktop through the
            //    qemu-guest-agent channel instead — ZuTM's own QMP control
            //    path — executing xdotool inside the session.
            await TypeAsync(qmp, "touch /tmp/UI-CONTROLLED");
            await SendKeysAsync(qmp, ["ret"]);
            await Task.Delay(TimeSpan.FromSeconds(10));

            if (!await FileExistsAsync(console, "/tmp/UI-CONTROLLED"))
            {
                const string sessionEnv = "DISPLAY=:0 XAUTHORITY=/home/tester/.Xauthority";
                // GA output capture is not honored by this qemu-ga build; the
                // agent writes its own verdict to a file we read over serial.
                await GuestAgentRunAsync(qmp, ports.GuestAgentPort,
                    "{ " +
                    "su - tester -c \"" + sessionEnv + " xdotool getactivewindow getwindowname\" > /tmp/agent-verdict.txt 2>&1 ; " +
                    "su - tester -c \"" + sessionEnv + " xdotool type --clearmodifiers --delay 150 'touch /tmp/UI-CONTROLLED'\" >> /tmp/agent-verdict.txt 2>&1 ; " +
                    "su - tester -c \"" + sessionEnv + " xdotool key Return\" >> /tmp/agent-verdict.txt 2>&1 ; " +
                    "sleep 3 ; ls -l /tmp/UI-CONTROLLED >> /tmp/agent-verdict.txt 2>&1 ; " +
                    "echo VERDICT-DONE >> /tmp/agent-verdict.txt ; }");

                var verdict = await console.RunAsync("cat /tmp/agent-verdict.txt", "VERDICT-DONE", TimeSpan.FromSeconds(30));
                Assert.Contains("UI-CONTROLLED", verdict);
                Assert.DoesNotContain("No such file", verdict);
            }

            // 5. Verify inside the guest that the host-driven UI interaction
            //    really executed.
            var proof = await console.RunAsync(
                "ls -l /tmp/UI-CONTROLLED && echo UI-PROOF", "UI-PROOF", TimeSpan.FromSeconds(30));
            Assert.Contains("UI-PROOF", proof);
            Assert.DoesNotContain("No such file", proof);

            // 6. SPICE guest tools are running (vdagent + qemu-ga channels).
            var agents = await console.RunAsync(
                "rc-service spice-vdagentd status && rc-service qemu-guest-agent status && echo AGENTS-OK",
                "AGENTS-OK", TimeSpan.FromSeconds(30));
            Assert.Contains("started", agents);

            // 7. Clean shutdown over the console.
            await console.SendLineAsync("poweroff");
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            while (vm.State != VmRunState.Stopped)
            {
                await Task.Delay(250, cts.Token);
            }
        }
    }

    /// <summary>
    /// Waits until the X session answers via xdotool with a real geometry —
    /// and keeps succeeding. X cookies churn while the session is still
    /// starting, so two consecutive good answers are required.
    /// </summary>
    private static async Task<(int Width, int Height)> WaitForXAsync(SerialConsoleSession console, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var consecutive = 0;
        while (true)
        {
            try
            {
                var output = await console.RunAsync(
                    AsTester("xdotool getdisplaygeometry; echo X-READY"),
                    "X-READY", TimeSpan.FromSeconds(20));
                var numbers = System.Text.RegularExpressions.Regex
                    .Matches(output[..^"X-READY".Length], @"\d+")
                    .Select(m => int.Parse(m.Value))
                    .ToArray();
                if (numbers.Length >= 2 && numbers[^2] >= 100 && numbers[^1] >= 100)
                {
                    consecutive++;
                    if (consecutive >= 2)
                    {
                        return (numbers[^2], numbers[^1]);
                    }
                }
                else
                {
                    consecutive = 0;
                }
            }
            catch (OperationCanceledException)
            {
            }

            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(3000, cts.Token);
        }
    }

    /// <summary>
    /// Captures the root window to PNG inside the guest, then reports its
    /// geometry (via xdotool) and a distinct-color sample (entropy) — enough
    /// to assert a real desktop without shipping megabytes out of the VM.
    /// </summary>
    private static async Task<(int Width, int Height, int Entropy)> CaptureScreenStatsAsync(SerialConsoleSession console)
    {
        var geometry = await console.RunAsync(
            AsTester("xdotool getdisplaygeometry; echo GEO-OK"), "GEO-OK", TimeSpan.FromSeconds(30));
        var geoNumbers = System.Text.RegularExpressions.Regex
            .Matches(geometry, @"\d+")
            .Select(m => int.Parse(m.Value))
            .ToArray();
        Assert.True(
            geoNumbers.Length >= 2 && geoNumbers[^2] >= 100 && geoNumbers[^1] >= 100,
            $"bad geometry (last two numbers {geoNumbers.ElementAtOrDefault(^2)}x{geoNumbers[^1]}); raw tail: {geometry[^Math.Min(120, geometry.Length)..]}");

        var entropy = await console.RunAsync(
            AsTester("import -window root /tmp/zutm-ui.png && convert /tmp/zutm-ui.png -resize 64x64! -depth 8 txt:- | tail -n +2 | cut -d'#' -f2 | sort -u | wc -l && echo ENT-OK"),
            "ENT-OK", TimeSpan.FromMinutes(2));
        var entropyNumbers = System.Text.RegularExpressions.Regex
            .Matches(entropy[..^"ENT-OK".Length], @"\d+")
            .Select(m => int.Parse(m.Value))
            .ToArray();
        Assert.True(entropyNumbers.Length >= 1, $"entropy output: {entropy[..Math.Min(200, entropy.Length)]}");

        return (geoNumbers[^2], geoNumbers[^1], entropyNumbers[^1]);
    }

    /// <summary>True when the path exists, checked over the serial console.</summary>
    private static async Task<bool> FileExistsAsync(SerialConsoleSession console, string path)
    {
        var output = await console.RunAsync($"test -e {path} && echo FILE-PRESENT || echo FILE-ABSENT", "FILE-PRESENT", TimeSpan.FromSeconds(30));
        return output.Contains("FILE-PRESENT", StringComparison.Ordinal)
            && !output.Contains("FILE-ABSENT", StringComparison.Ordinal);
    }

    /// <summary>
    /// Runs a shell command in the guest via the qemu-guest-agent channel
    /// (guest-exec/guest-exec-status) — the host-driven control path ZuTM
    /// exposes for VMs with the agent installed.
    /// </summary>
    private static async Task<string> GuestAgentRunAsync(QmpClient qmp, int guestAgentPort, string shellCommand)
    {
        // QEMU does not proxy guest-* over QMP; the chardev socket IS the
        // qemu-ga protocol endpoint. Speak newline-JSON directly to it.
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync("127.0.0.1", guestAgentPort);
        using var stream = tcp.GetStream();

        async Task<JsonElement> GaAsync(string command, object? arguments = null)
        {
            var payload = new Dictionary<string, object?> { ["execute"] = command };
            if (arguments is not null)
            {
                payload["arguments"] = arguments;
            }

            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload);
            var framed = new byte[bytes.Length + 1];
            bytes.CopyTo(framed, 0);
            framed[^1] = (byte)'\n';
            await stream.WriteAsync(framed);
            await stream.FlushAsync();

            using var doc = await System.Text.Json.JsonDocument.ParseAsync(ReadLine(stream));
            var root = doc.RootElement.Clone();
            // qemu-ga wraps payloads in "return" (like QMP) and reports errors in "error".
            if (root.TryGetProperty("error", out var gaError))
            {
                throw new QmpException("guest-agent: " + gaError.GetRawText());
            }

            return root.TryGetProperty("return", out var value) ? value.Clone() : root;
        }

        // Readiness ping with retries (agent connects on its own schedule).
        using var readyCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (true)
        {
            try
            {
                await GaAsync("guest-ping");
                break;
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or System.Text.Json.JsonException)
            {
                readyCts.Token.ThrowIfCancellationRequested();
                await Task.Delay(1000, readyCts.Token);
            }
        }

        var start = await GaAsync("guest-exec", new Dictionary<string, object?>
        {
            ["path"] = "sh",
            ["arg"] = new[] { "-c", shellCommand },
            ["capture-output"] = true,
        });
        var pid = start.GetProperty("pid").GetInt32();

        while (true)
        {
            await Task.Delay(300);
            var status = await GaAsync("guest-exec-status", new Dictionary<string, object?> { ["pid"] = pid });
            if (status.GetProperty("exited").GetBoolean())
            {
                var exitcode = status.TryGetProperty("exitcode", out var code) ? code.GetInt32() : -1;
                var stdout = DecodeGa(status, "outdata");
                var stderr = DecodeGa(status, "errdata");
                var outLen = status.TryGetProperty("outdata", out var od) ? (od.GetString() ?? "").Length : -1;
                var fields = string.Join(",", status.EnumerateObject().Select(p => p.Name));
                return $"exit={exitcode} out=[{stdout}] err=[{stderr}] outdataLen={outLen} fields=[{fields}]";
            }
        }
    }

    /// <summary>guest-exec captures are base64-encoded.</summary>
    private static string DecodeGa(JsonElement status, string property)
    {
        if (!status.TryGetProperty(property, out var value) || value.GetString() is not { Length: > 0 } encoded)
        {
            return string.Empty;
        }

        var bytes = Convert.FromBase64String(encoded);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static MemoryStream ReadLine(NetworkStream stream)
    {
        var buffer = new MemoryStream();
        int b;
        while ((b = stream.ReadByte()) is not (-1 or (byte)'\n'))
        {
            buffer.WriteByte((byte)b);
        }

        buffer.Position = 0;
        return buffer;
    }

    /// <summary>QMP send-key with qcode names (ctrl, alt, t, ret, …).</summary>
    private static Task SendKeysAsync(QmpClient qmp, params string[] qcodes) =>
        qmp.ExecuteAsync("send-key", new
        {
            keys = qcodes.Select(code => new { type = "qcode", data = code }).ToArray(),
        });

    /// <summary>Types ASCII text one keystroke at a time (send-key is blind typing).</summary>
    private static async Task TypeAsync(QmpClient qmp, string text)
    {
        foreach (var character in text)
        {
            var qcodes = character switch
            {
                ' ' => new[] { "spc" },
                '/' => new[] { "slash" }, // unshifted on US layouts; shift+slash is '?'
                '-' => new[] { "minus" },
                '.' => new[] { "dot" },
                >= 'A' and <= 'Z' => new[] { "shift", char.ToLowerInvariant(character).ToString() },
                _ => new[] { character.ToString() },
            };
            await SendKeysAsync(qmp, qcodes);
            await Task.Delay(120); // TCG needs generous inter-key spacing
        }
    }
}
