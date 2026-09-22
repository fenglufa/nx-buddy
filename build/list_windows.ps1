param([int]$TargetPid)
Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class WinEnum {
  delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int c);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  public static List<string> Titles(int pid) {
    var found = new List<string>();
    EnumWindows((h, l) => {
      uint p; GetWindowThreadProcessId(h, out p);
      if ((int)p == pid && IsWindowVisible(h)) {
        var sb = new StringBuilder(512);
        GetWindowTextW(h, sb, 512);
        if (sb.Length > 0) found.Add(sb.ToString());
      }
      return true;
    }, IntPtr.Zero);
    return found;
  }
}
"@
[WinEnum]::Titles($TargetPid) -join "`n"
