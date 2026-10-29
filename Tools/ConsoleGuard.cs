using System;
using System.Runtime.InteropServices;

namespace DiceModders.ModCore.Tools;

/// <summary>
/// Turns off "QuickEdit" on the BepInEx console window. With QuickEdit on, a click inside the console starts a text selection and Windows
/// pauses every program that writes to that console until Enter or Esc is pressed. The game logs through BepInEx, so it freezes.
/// ModCore runs this once at start (config <c>[General] DisableConsoleQuickEdit</c>); there is no window to fix when the console is disabled.
/// </summary>
internal static class ConsoleGuard {
	private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
	private const uint ShareRead = 1, ShareWrite = 2, OpenExisting = 3;
	private const uint EnableQuickEdit = 0x0040, EnableExtendedFlags = 0x0080;

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
	[DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleMode(IntPtr handle, out uint mode);
	[DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleMode(IntPtr handle, uint mode);
	[DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);

	public static void DisableQuickEdit() {
		try {
			IntPtr handle = CreateFile("CONIN$", GenericRead | GenericWrite, ShareRead | ShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
			if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return;
			try {
				if (!GetConsoleMode(handle, out uint mode) || (mode & EnableQuickEdit) == 0) return;
				if (SetConsoleMode(handle, (mode & ~EnableQuickEdit) | EnableExtendedFlags))
					CoreLog.Info("console QuickEdit switched off, so clicking the console window can no longer freeze the game");
			}
			finally { CloseHandle(handle); }
		}
		catch (Exception) { /* convenience only */ }
	}
}
