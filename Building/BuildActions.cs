using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using DiceKingdoms.Game;

namespace DiceModders.ModCore;

public static partial class ModCore {
	/// <summary>
	/// Build actions of your own, which work exactly like the game's market purchase: the player's click is one of the game's planned build
	/// actions, your own screen applies it at once (preview), the game sends it to everybody with its own messages and every computer applies it
	/// when the build phase ends (the game waits for it). Use it for a choice that changes the simulation and must happen right away, in the
	/// same order on every computer (an offering, a bet, ...).
	/// </summary>
	/// <remarks>
	/// <para>The game rejects action kinds it does not know (its network reader throws), so a mod action travels as the game's REPAIR_BUILDING
	/// action on a building id with a marker: bit 30, plus the action's code in bits 24..29. Building ids are (y &lt;&lt; 16 | x) with x, y below
	/// 256, so they never use those bits. Every computer in the match needs the mod that registered the code (use it only while your lobby rule is
	/// on: ModCore uses rules only when everybody has the mod).</para>
	/// <para>Codes are fixed numbers 0..63 that each mod picks once: 0 Upkeep (Cathedral offering), 1 Casino (spin). Two mods with the same
	/// code: the second registration is refused and logged.</para>
	/// </remarks>
	public static class BuildActions {
		/// <summary>Runs your action on one island: <paramref name="data"/> is that island's build data (<c>data.Island</c>), the int is the building id the
		/// action was planned on. Return true when it happened (resources paid etc.), false to drop it (the game skips a failed action, as with any other).
		/// Same checks and same result on every computer: read only the game state.</summary>
		public delegate bool Handler(BuildData data, int buildingId);

		/// <summary>Registers your action under <paramref name="code"/> (0..63). False when the code is taken or out of range.</summary>
		public static bool Register(int code, string owner, Handler handler) {
			Core.Ensure();
			if (code < 0 || code > MaxCode || handler == null) { CoreLog.Error("build actions: code " + code + " (" + owner + ") is not 0.." + MaxCode); return false; }
			if (Handlers.ContainsKey(code)) { CoreLog.Error("build actions: code " + code + " is already used by " + Owners[code] + "; " + owner + " was refused"); return false; }
			Handlers[code] = handler;
			Owners[code] = owner ?? "?";
			Hook();
			CoreLog.Info("build actions: code " + code + " = " + Owners[code]);
			return true;
		}

		/// <summary>Plans your action on <paramref name="buildingId"/> for the island of <paramref name="data"/> (the local player's: <c>BuildInterface.Data</c>).
		/// Only during the build phase. Returns what your handler returned on this screen (the game drops a failed action).</summary>
		public static bool Plan(BuildData data, int code, int buildingId) {
			Core.Ensure();
			if (data == null || !Handlers.ContainsKey(code) || (buildingId & ~IdMask) != 0) return false;
			int id = buildingId | Marker | (code << CodeShift);
			var wrapped = System.Runtime.CompilerServices.Unsafe.As<int, Building.Id>(ref id);   // Building.Id is one int; its field is readonly
			return data.RepairBuilding(wrapped);
		}

		// ------------------------------------------------------------------ the hook

		private const int Marker = 0x40000000, CodeShift = 24, MaxCode = 63, IdMask = 0x00FFFFFF;
		private const int RepairAction = 0, ActionType = 0x0, ActionBuildingId = 0xC;   // BuildData.Action: type (int) 0x0, buildingId (int) 0xC
		private static readonly Dictionary<int, Handler> Handlers = new();
		private static readonly Dictionary<int, string> Owners = new();

		// BuildData.DoAction(ref Action) takes the action struct BY REFERENCE and the struct holds object references: a Harmony patch on it crashed
		// the game at the first building placed (2026-11-21). So this is a plain native hook that reads the action in place:
		//   bool DoAction(BuildData* this, BuildData.Action* action, MethodInfo* method)
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate byte DoActionFn(IntPtr self, IntPtr action, IntPtr method);
		private static DoActionFn _hook, _original;   // kept alive while the game runs
		private static object _detour;
		private static bool _hooked;

		private static void Hook() {
			if (_hooked) return;
			_hooked = true;
			try {
				var method = typeof(BuildData).GetMethod("DoAction");
				var field = Il2CppInterop.Common.Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
				IntPtr methodInfo = (IntPtr)field.GetValue(null);
				IntPtr code = Marshal.ReadIntPtr(methodInfo);   // Il2CppMethodInfo.methodPointer is its first field
				_hook = DoAction;
				_detour = BepInEx.Unity.IL2CPP.Hook.INativeDetour.CreateAndApply(code, _hook, out _original);
				CoreLog.Info("build actions: listening to the game's build actions");
			}
			catch (Exception e) { CoreLog.Error("build actions: hook failed, mod build actions will not work: " + e.Message); }
		}

		private static byte DoAction(IntPtr self, IntPtr action, IntPtr method) {
			try {
				if (action != IntPtr.Zero && Tools.Mem.ReadInt(action, ActionType) == RepairAction) {
					int id = Tools.Mem.ReadInt(action, ActionBuildingId);
					if ((id & Marker) != 0) {
						int code = (id >> CodeShift) & MaxCode;
						if (!Handlers.TryGetValue(code, out var handler)) return 0;   // a code this computer does not know: drop it
						return handler(new BuildData(self), id & IdMask) ? (byte)1 : (byte)0;
					}
				}
			}
			catch (Exception e) { CoreLog.Once("buildactions:run", "build actions: a mod action failed: " + e); return 0; }
			return _original(self, action, method);
		}
	}
}
