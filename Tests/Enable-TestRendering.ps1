# RimWorld 1.7 suspends its frame loop when its native window starts hidden.
# Give only this test process a drawable surface outside the virtual desktop.
param(
    [Parameter(Mandatory)][System.Diagnostics.Process]$GameProcess,
    [DateTime]$Deadline = [DateTime]::MaxValue
)

if (-not ('SearchlightTests.RenderWindow' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace SearchlightTests {
    public static class RenderWindow {
        private delegate bool WindowVisitor(IntPtr window, IntPtr data);
        [DllImport("user32")] private static extern bool EnumWindows(WindowVisitor visit, IntPtr data);
        [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr window, StringBuilder title, int capacity);
        [DllImport("user32")] private static extern int GetSystemMetrics(int metric);
        [DllImport("user32")] private static extern bool SetWindowPos(IntPtr window, IntPtr order,
            int x, int y, int width, int height, uint flags);

        public static bool Show(int pid, int width) {
            bool shown = false;
            EnumWindows((window, unused) => {
                uint owner;
                GetWindowThreadProcessId(window, out owner);
                if (owner != pid) return true;
                var title = new StringBuilder(128);
                GetWindowText(window, title, title.Capacity);
                if (title.ToString() != "RimWorld by Ludeon Studios") return true;
                // SM_XVIRTUALSCREEN; HWND_BOTTOM, SWP_NOACTIVATE | SWP_SHOWWINDOW.
                shown = SetWindowPos(window, new IntPtr(1), GetSystemMetrics(76) - 1600, 0, width, 900, 0x50);
                return false;
            }, IntPtr.Zero);
            return shown;
        }
    }
}
'@
}
$pollUntil = [DateTime]::UtcNow.AddSeconds(30)
if ($Deadline -lt $pollUntil) { $pollUntil = $Deadline }
while (!$GameProcess.HasExited -and [DateTime]::UtcNow -lt $pollUntil) {
    if ([SearchlightTests.RenderWindow]::Show($GameProcess.Id, 1440)) {
        $remaining = ($Deadline - [DateTime]::UtcNow).TotalMilliseconds
        Start-Sleep -Milliseconds ([Math]::Max(0.0, [Math]::Min(5000.0, $remaining)))
        if (!$GameProcess.HasExited -and [DateTime]::UtcNow -lt $Deadline) {
            [void][SearchlightTests.RenderWindow]::Show($GameProcess.Id, 1442)
        }
        break
    }
    $remaining = ($pollUntil - [DateTime]::UtcNow).TotalMilliseconds
    Start-Sleep -Milliseconds ([Math]::Max(0.0, [Math]::Min(250.0, $remaining)))
}
