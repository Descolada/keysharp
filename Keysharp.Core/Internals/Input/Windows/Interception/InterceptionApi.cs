#if WINDOWS

using System.Runtime.InteropServices;

namespace Keysharp.Internals.Input.Windows.Interception
{
	// P/Invoke surface for the third-party Interception driver (https://github.com/oblitum/Interception).
	// Struct layouts and constants mirror the library's public interception.h. That header ships with the
	// driver installer rather than this repo, so these values were transcribed from the upstream project
	// rather than read from a local copy -- cross-check against the installed driver's interception.h
	// before relying on anything here in a release build.
	internal enum InterceptionKeyState : ushort
	{
		Down = 0x00,
		Up = 0x01,
		E0 = 0x02,
		E1 = 0x04,
		TermsrvSetLed = 0x08,
		TermsrvShadow = 0x10,
		TermsrvVkPacket = 0x20
	}

	[Flags]
	internal enum InterceptionFilterKeyState : ushort
	{
		None = 0x0000,
		All = 0xFFFF,
		Down = InterceptionKeyState.Up,               // filtering "down" means excluding the Up bit, per upstream.
		Up = (ushort)InterceptionKeyState.Up << 1,
		E0 = (ushort)InterceptionKeyState.E0 << 1,
		E1 = (ushort)InterceptionKeyState.E1 << 1
	}

	[Flags]
	internal enum InterceptionMouseState : ushort
	{
		LeftButtonDown = 0x001,
		LeftButtonUp = 0x002,
		RightButtonDown = 0x004,
		RightButtonUp = 0x008,
		MiddleButtonDown = 0x010,
		MiddleButtonUp = 0x020,
		Button4Down = 0x040,
		Button4Up = 0x080,
		Button5Down = 0x100,
		Button5Up = 0x200,
		Wheel = 0x400,
		HWheel = 0x800
	}

	[Flags]
	internal enum InterceptionFilterMouseState : ushort
	{
		None = 0x0000,
		All = 0xFFFF,
		Move = 0x1000
	}

	[Flags]
	internal enum InterceptionMouseFlag : ushort
	{
		MoveRelative = 0x000,
		MoveAbsolute = 0x001,
		VirtualDesktop = 0x002,
		AttributesChanged = 0x004,
		MoveNoCoalesce = 0x008,
		TermsrvSrcShadow = 0x100
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct InterceptionKeyStroke
	{
		internal ushort code;         // scan code
		internal ushort state;        // InterceptionKeyState flags
		internal uint information;    // opaque, round-tripped as-is; used to tag Keysharp-originated sends
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct InterceptionMouseStroke
	{
		internal ushort state;        // InterceptionMouseState flags
		internal ushort flags;        // InterceptionMouseFlag flags
		internal short rolling;       // wheel delta
		internal int x;
		internal int y;
		internal uint information;
	}

	// The C API passes InterceptionStroke as a union of the two stroke shapes above; the key/mouse fields
	// here alias the same 16 bytes so the struct marshals correctly regardless of which shape is populated.
	[StructLayout(LayoutKind.Explicit)]
	internal struct InterceptionStroke
	{
		[FieldOffset(0)] internal InterceptionKeyStroke key;
		[FieldOffset(0)] internal InterceptionMouseStroke mouse;
	}

	internal delegate int InterceptionPredicate(int device);

	internal static partial class InterceptionApi
	{
		private const string Lib = "interception.dll";

		[LibraryImport(Lib, EntryPoint = "interception_create_context")]
		internal static partial nint interception_create_context();

		[LibraryImport(Lib, EntryPoint = "interception_destroy_context")]
		internal static partial void interception_destroy_context(nint context);

		[LibraryImport(Lib, EntryPoint = "interception_get_precedence")]
		internal static partial int interception_get_precedence(nint context, int device);

		[LibraryImport(Lib, EntryPoint = "interception_set_precedence")]
		internal static partial void interception_set_precedence(nint context, int device, int precedence);

		[LibraryImport(Lib, EntryPoint = "interception_get_filter")]
		internal static partial ushort interception_get_filter(nint context, int device);

		// predicate may be null to clear/leave a class unfiltered, matching the upstream API. LibraryImport
		// marshals the delegate as a native function pointer automatically (same pattern WindowsAPI.cs uses
		// for SetWindowsHookEx's callback parameter) -- no explicit MarshalAs needed.
		[LibraryImport(Lib, EntryPoint = "interception_set_filter")]
		internal static partial void interception_set_filter(nint context, InterceptionPredicate predicate, ushort filter);

		[LibraryImport(Lib, EntryPoint = "interception_wait")]
		internal static partial int interception_wait(nint context);

		[LibraryImport(Lib, EntryPoint = "interception_wait_with_timeout")]
		internal static partial int interception_wait_with_timeout(nint context, ulong milliseconds);

		[LibraryImport(Lib, EntryPoint = "interception_send")]
		internal static partial int interception_send(nint context, int device, ref InterceptionStroke stroke, uint nstroke);

		[LibraryImport(Lib, EntryPoint = "interception_receive")]
		internal static partial int interception_receive(nint context, int device, ref InterceptionStroke stroke, uint nstroke);

		[LibraryImport(Lib, EntryPoint = "interception_get_hardware_id")]
		internal static partial uint interception_get_hardware_id(nint context, int device, nint hardwareIdBuffer, uint bufferSize);

		[LibraryImport(Lib, EntryPoint = "interception_is_invalid")]
		internal static partial int interception_is_invalid(int device);

		[LibraryImport(Lib, EntryPoint = "interception_is_keyboard")]
		internal static partial int interception_is_keyboard(int device);

		[LibraryImport(Lib, EntryPoint = "interception_is_mouse")]
		internal static partial int interception_is_mouse(int device);

		// Interception numbers up to 10 keyboard devices [1..10] followed by up to 10 mouse devices [11..20].
		internal const int MaxKeyboard = 10;
		internal const int MaxDevice = 20;
	}

	/// <summary>Cheap presence probe: is the driver installed and its service running right now.</summary>
	internal static class InterceptionDriver
	{
		internal static bool IsAvailable()
		{
			nint context = 0;

			try
			{
				context = InterceptionApi.interception_create_context();
				return context != 0;
			}
			catch (DllNotFoundException) { return false; }
			catch (EntryPointNotFoundException) { return false; }
			finally
			{
				if (context != 0)
					InterceptionApi.interception_destroy_context(context);
			}
		}
	}
}
#endif
