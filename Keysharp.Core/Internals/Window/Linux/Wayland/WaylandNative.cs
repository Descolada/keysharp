using System.Runtime.InteropServices;

#if LINUX
namespace Keysharp.Internals.Window.Linux.Wayland
{
	internal static partial class WaylandNative
	{
		internal const string ClientLibrary = "libwayland-client.so.0";
		internal const uint DestroyFlag = 1;

		[LibraryImport(ClientLibrary, EntryPoint = "wl_display_connect", StringMarshalling = StringMarshalling.Utf8)]
		internal static partial nint DisplayConnect(string name);

		[LibraryImport(ClientLibrary, EntryPoint = "wl_display_disconnect")]
		internal static partial void DisplayDisconnect(nint display);

		[LibraryImport(ClientLibrary, EntryPoint = "wl_display_roundtrip")]
		internal static partial int DisplayRoundtrip(nint display);

		[LibraryImport(ClientLibrary, EntryPoint = "wl_proxy_add_listener")]
		internal static partial int ProxyAddListener(nint proxy, nint implementation, nint data);

		[LibraryImport(ClientLibrary, EntryPoint = "wl_proxy_destroy")]
		internal static partial void ProxyDestroy(nint proxy);

		[LibraryImport(ClientLibrary, EntryPoint = "wl_proxy_get_version")]
		internal static partial uint ProxyGetVersion(nint proxy);

		// wl_proxy_marshal_flags is variadic in C; [LibraryImport] does not support variadic marshaling,
		// so these overloads use [DllImport] with explicit fixed argument lists instead.
		[DllImport(ClientLibrary, EntryPoint = "wl_proxy_marshal_flags", CallingConvention = CallingConvention.Cdecl)]
		internal static extern nint MarshalConstructor(nint proxy, uint opcode, nint protocolInterface, uint version, uint flags, nint newId);

		[DllImport(ClientLibrary, EntryPoint = "wl_proxy_marshal_flags", CallingConvention = CallingConvention.Cdecl)]
		internal static extern nint MarshalRegistryBind(nint proxy, uint opcode, nint protocolInterface, uint version, uint flags,
			uint name, nint interfaceName, uint interfaceVersion, nint newId);

		[DllImport(ClientLibrary, EntryPoint = "wl_proxy_marshal_flags", CallingConvention = CallingConvention.Cdecl)]
		internal static extern void MarshalRequest(nint proxy, uint opcode, nint protocolInterface, uint version, uint flags);

		[DllImport(ClientLibrary, EntryPoint = "wl_proxy_marshal_flags", CallingConvention = CallingConvention.Cdecl)]
		internal static extern void MarshalObjectRequest(nint proxy, uint opcode, nint protocolInterface, uint version, uint flags, nint arg);

		// Stable wl_display and wl_registry opcodes (Wayland core protocol).
		private const uint WlDisplayGetRegistryOpcode = 1; // sync=0, get_registry=1
		private const uint WlRegistryBindOpcode = 0;

		internal static nint RegistryInterface => Export("wl_registry_interface");
		internal static nint SeatInterface => Export("wl_seat_interface");
		internal static nint OutputInterface => Export("wl_output_interface");

		internal static nint DisplayGetRegistry(nint display)
			=> MarshalConstructor(display, WlDisplayGetRegistryOpcode, RegistryInterface, ProxyGetVersion(display), 0, 0);

		internal static nint RegistryBind(nint registry, uint name, ProtocolInterface protocolInterface, uint version)
			=> MarshalRegistryBind(registry, WlRegistryBindOpcode, protocolInterface.Pointer, version, 0, name, protocolInterface.NamePointer, version, 0);

		// Used when binding a global whose interface struct is an exported symbol from libwayland itself
		// (e.g. wl_seat_interface). Pass the exported symbol as protocolInterface and the interface
		// name string separately; libwayland uses both to size and verify the new proxy.
		internal static nint RegistryBind(nint registry, uint name, nint protocolInterface, string protocolName, uint version)
		{
			var namePointer = Marshal.StringToCoTaskMemUTF8(protocolName);

			try
			{
				return MarshalRegistryBind(registry, 0, protocolInterface, version, 0, name, namePointer, version, 0);
			}
			finally
			{
				Marshal.FreeCoTaskMem(namePointer);
			}
		}

		private static nint libraryHandle;

		private static nint Export(string name)
		{
			if (libraryHandle == 0 && !NativeLibrary.TryLoad(ClientLibrary, out libraryHandle))
				return 0;

			return NativeLibrary.TryGetExport(libraryHandle, name, out var address) ? address : 0;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct WlArray
		{
			internal nuint Size;
			internal nuint Alloc;
			internal nint Data;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct WlInterface
		{
			internal nint Name;
			internal int Version;
			internal int MethodCount;
			internal nint Methods;
			internal int EventCount;
			internal nint Events;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct WlMessage
		{
			internal nint Name;
			internal nint Signature;
			internal nint Types;
		}

		internal sealed class ProtocolInterface : IDisposable
		{
			// All blocks use AllocCoTaskMem / StringToCoTaskMemUTF8 so Dispose can free them
			// uniformly. The static instances in Interfaces live for the process lifetime and are
			// never explicitly disposed; Dispose exists for non-static use and correctness.
			private readonly List<nint> owned = [];

			internal nint NamePointer { get; }
			internal nint Pointer { get; }

			internal ProtocolInterface(string name, int version, (string Name, string Signature, nint[] Types)[] methods,
				(string Name, string Signature, nint[] Types)[] events)
			{
				NamePointer = Own(Marshal.StringToCoTaskMemUTF8(name));
				var native = new WlInterface
				{
					Name = NamePointer,
					Version = version,
					MethodCount = methods.Length,
					Methods = BuildMessages(methods),
					EventCount = events.Length,
					Events = BuildMessages(events)
				};
				Pointer = Own(Marshal.AllocCoTaskMem(Marshal.SizeOf<WlInterface>()));
				Marshal.StructureToPtr(native, Pointer, false);
			}

			public void Dispose()
			{
				foreach (var ptr in owned)
					if (ptr != 0)
						Marshal.FreeCoTaskMem(ptr);

				owned.Clear();
			}

			private nint BuildMessages((string Name, string Signature, nint[] Types)[] messages)
			{
				if (messages.Length == 0)
					return 0;

				var size = Marshal.SizeOf<WlMessage>();
				var address = Own(Marshal.AllocCoTaskMem(size * messages.Length));

				for (var i = 0; i < messages.Length; i++)
				{
					var msg = messages[i];
					nint types = 0;

					if (msg.Types is { Length: > 0 })
					{
						types = Own(Marshal.AllocCoTaskMem(IntPtr.Size * msg.Types.Length));

						for (var j = 0; j < msg.Types.Length; j++)
							Marshal.WriteIntPtr(types, j * IntPtr.Size, msg.Types[j]);
					}

					Marshal.StructureToPtr(new WlMessage
					{
						Name = Own(Marshal.StringToCoTaskMemUTF8(msg.Name)),
						Signature = Own(Marshal.StringToCoTaskMemUTF8(msg.Signature)),
						Types = types
					}, address + (i * size), false);
				}

				return address;
			}

			private nint Own(nint address)
			{
				owned.Add(address);
				return address;
			}
		}
	}
}
#endif
