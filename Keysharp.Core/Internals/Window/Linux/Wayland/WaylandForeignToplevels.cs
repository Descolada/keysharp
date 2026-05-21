using System.Runtime.InteropServices;

#if LINUX
namespace Keysharp.Internals.Window.Linux.Wayland
{
	// Not thread-safe: all public calls (Enumerate, Active, Get, Activate, etc.) must come from
	// the same thread. Native callbacks run synchronously within DisplayRoundtrip, so the
	// toplevel dictionaries are only mutated and read on the one calling thread.
	internal sealed class WaylandForeignToplevels : IDisposable
	{
		private const string ExtListName = "ext_foreign_toplevel_list_v1";
		private const string SeatName = "wl_seat";
		private const string WlrManagerName = "zwlr_foreign_toplevel_manager_v1";

		private static readonly object sync = new();
		private static WaylandForeignToplevels current;

		private readonly Dictionary<nint, WaylandToplevel> toplevelsByHandle = [];
		private readonly Dictionary<nint, WaylandToplevel> toplevelsByProxy = [];
		private readonly List<GCHandle> listenerData = [];
		private readonly GCHandle selfHandle;
		private readonly nint display;
		private nint extList;
		private nint registry;
		private nint seat;
		private nint wlrManager;
		private long nextHandle = -1;

		private WaylandForeignToplevels(nint display)
		{
			this.display = display;
			selfHandle = GCHandle.Alloc(this);
		}

		internal static WaylandForeignToplevels Current
		{
			get
			{
				lock (sync)
					return current ??= TryCreate();
			}
		}

		internal bool CanList => wlrManager != 0 || extList != 0;
		internal bool CanManage => wlrManager != 0;

		internal WaylandToplevel Active
		{
			get
			{
				Refresh();
				return toplevelsByHandle.Values.FirstOrDefault(toplevel => !toplevel.Closed && toplevel.Activated);
			}
		}

		internal IReadOnlyList<WaylandToplevel> Enumerate()
		{
			Refresh();
			var protocol = CanManage ? WaylandForeignToplevelProtocol.Wlr : WaylandForeignToplevelProtocol.Ext;
			return toplevelsByHandle.Values
				.Where(toplevel => !toplevel.Closed && toplevel.Protocol == protocol)
				.ToList();
		}

		internal bool IsWindow(nint handle)
		{
			Refresh();
			return toplevelsByHandle.TryGetValue(handle, out var toplevel) && !toplevel.Closed;
		}

		internal WaylandToplevel Get(nint handle)
		{
			Refresh();
			return toplevelsByHandle.TryGetValue(handle, out var toplevel) && !toplevel.Closed ? toplevel : null;
		}

		internal bool Activate(WaylandToplevel toplevel)
		{
			if (!CanRequest(toplevel) || seat == 0)
				return false;

			WaylandNative.MarshalObjectRequest(toplevel.Proxy, 4, 0, WaylandNative.ProxyGetVersion(toplevel.Proxy), 0, seat);
			return RoundtripAfterRequest();
		}

		internal bool Close(WaylandToplevel toplevel)
		{
			if (!CanRequest(toplevel))
				return false;

			WaylandNative.MarshalRequest(toplevel.Proxy, 5, 0, WaylandNative.ProxyGetVersion(toplevel.Proxy), 0);
			return RoundtripAfterRequest();
		}

		internal bool SetState(WaylandToplevel toplevel, FormWindowState state)
		{
			if (!CanRequest(toplevel))
				return false;

			var opcode = state switch
			{
				FormWindowState.Maximized => 0u,
				FormWindowState.Minimized => 2u,
				_ when toplevel.Minimized => 3u,
				_ when toplevel.Maximized => 1u,
				_ => uint.MaxValue
			};

			if (opcode == uint.MaxValue)
				return true;

			WaylandNative.MarshalRequest(toplevel.Proxy, opcode, 0, WaylandNative.ProxyGetVersion(toplevel.Proxy), 0);
			return RoundtripAfterRequest();
		}

		public void Dispose()
		{
			lock (sync)
				if (ReferenceEquals(current, this))
					current = null;

			foreach (var handle in listenerData)
				if (handle.IsAllocated)
					handle.Free();

			if (selfHandle.IsAllocated)
				selfHandle.Free();

			foreach (var toplevel in toplevelsByProxy.Values)
				if (toplevel.Proxy != 0)
					WaylandNative.ProxyDestroy(toplevel.Proxy);

			if (wlrManager != 0)
				WaylandNative.ProxyDestroy(wlrManager);

			if (extList != 0)
				WaylandNative.ProxyDestroy(extList);

			if (seat != 0)
				WaylandNative.ProxyDestroy(seat);

			if (registry != 0)
				WaylandNative.ProxyDestroy(registry);

			if (display != 0)
				WaylandNative.DisplayDisconnect(display);
		}

		private bool CanRequest(WaylandToplevel toplevel)
			=> toplevel is { Closed: false, Protocol: WaylandForeignToplevelProtocol.Wlr } && toplevel.Proxy != 0 && CanManage;

		private bool RoundtripAfterRequest()
		{
			Refresh();
			return true;
		}

		private void Refresh()
		{
			if (display != 0)
				_ = WaylandNative.DisplayRoundtrip(display);
		}

		private static WaylandForeignToplevels TryCreate()
		{
			if (!string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase))
				return null;

			var display = WaylandNative.DisplayConnect(null);
			if (display == 0)
				return null;

			var client = new WaylandForeignToplevels(display);
			client.registry = WaylandNative.DisplayGetRegistry(display);

			if (client.registry == 0 || WaylandNative.ProxyAddListener(client.registry, RegistryListener.Pointer, GCHandle.ToIntPtr(client.selfHandle)) != 0)
			{
				client.Dispose();
				return null;
			}

			client.Refresh();
			client.Refresh();

			if (!client.CanList)
			{
				client.Dispose();
				return null;
			}

			return client;
		}

		private void AddToplevel(nint proxy, WaylandForeignToplevelProtocol protocol)
		{
			if (proxy == 0 || toplevelsByProxy.ContainsKey(proxy))
				return;

			var toplevel = new WaylandToplevel
			{
				Handle = new nint(nextHandle--),
				Protocol = protocol,
				Proxy = proxy
			};
			toplevelsByHandle[toplevel.Handle] = toplevel;
			toplevelsByProxy[proxy] = toplevel;

			var data = GCHandle.Alloc(toplevel);
			listenerData.Add(data);
			var listener = protocol == WaylandForeignToplevelProtocol.Wlr ? WlrHandleListener.Pointer : ExtHandleListener.Pointer;
			_ = WaylandNative.ProxyAddListener(proxy, listener, GCHandle.ToIntPtr(data));
		}

		private void BindExt(uint name, uint version)
		{
			if (extList != 0)
				return;

			extList = WaylandNative.RegistryBind(registry, name, Interfaces.ExtList, Math.Min(version, 1u));
			_ = WaylandNative.ProxyAddListener(extList, ExtListListener.Pointer, GCHandle.ToIntPtr(selfHandle));
		}

		private void BindSeat(uint name, uint version)
		{
			if (seat == 0)
				seat = WaylandNative.RegistryBind(registry, name, WaylandNative.SeatInterface, SeatName, Math.Min(version, 8u));
		}

		private void BindWlr(uint name, uint version)
		{
			if (wlrManager != 0)
				return;

			wlrManager = WaylandNative.RegistryBind(registry, name, Interfaces.WlrManager, Math.Min(version, 3u));
			_ = WaylandNative.ProxyAddListener(wlrManager, WlrManagerListener.Pointer, GCHandle.ToIntPtr(selfHandle));
		}

		private static WaylandForeignToplevels Self(nint data) => (WaylandForeignToplevels)GCHandle.FromIntPtr(data).Target;
		private static WaylandToplevel Toplevel(nint data) => (WaylandToplevel)GCHandle.FromIntPtr(data).Target;
		private static string Utf8(nint value) => Marshal.PtrToStringUTF8(value) ?? string.Empty;

		private static class RegistryListener
		{
			private static readonly GlobalHandler onGlobal = Global;
			private static readonly GlobalRemoveHandler onGlobalRemove = GlobalRemove;
			internal static readonly nint Pointer = ListenerBlock.Create(onGlobal, onGlobalRemove);

			private static void Global(nint data, nint registry, uint name, nint protocolInterface, uint version)
			{
				var client = Self(data);
				var interfaceName = Utf8(protocolInterface);

				switch (interfaceName)
				{
					case WlrManagerName:
						client.BindWlr(name, version);
						break;
					case ExtListName:
						client.BindExt(name, version);
						break;
					case SeatName:
						client.BindSeat(name, version);
						break;
				}
			}

			private static void GlobalRemove(nint data, nint registry, uint name) { }

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			private delegate void GlobalHandler(nint data, nint registry, uint name, nint protocolInterface, uint version);
			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			private delegate void GlobalRemoveHandler(nint data, nint registry, uint name);
		}

		private static class WlrManagerListener
		{
			private static readonly CreatedHandler onCreated = Created;
			private static readonly FinishedHandler onFinished = Finished;
			internal static readonly nint Pointer = ListenerBlock.Create(onCreated, onFinished);
			private static void Created(nint data, nint manager, nint handle) => Self(data).AddToplevel(handle, WaylandForeignToplevelProtocol.Wlr);
			private static void Finished(nint data, nint manager) { }
			[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CreatedHandler(nint data, nint manager, nint handle);
			[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FinishedHandler(nint data, nint manager);
		}

		private static class ExtListListener
		{
			private static readonly CreatedHandler onCreated = Created;
			private static readonly FinishedHandler onFinished = Finished;
			internal static readonly nint Pointer = ListenerBlock.Create(onCreated, onFinished);
			private static void Created(nint data, nint list, nint handle) => Self(data).AddToplevel(handle, WaylandForeignToplevelProtocol.Ext);
			private static void Finished(nint data, nint list) { }
			[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CreatedHandler(nint data, nint list, nint handle);
			[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FinishedHandler(nint data, nint list);
		}

		private static class WlrHandleListener
		{
			private static readonly StringHandler onTitle = (data, _, value) => Toplevel(data).Title = Utf8(value);
			private static readonly StringHandler onAppId = (data, _, value) => Toplevel(data).AppId = Utf8(value);
			private static readonly ObjectHandler onOutputEnter = IgnoreObject;
			private static readonly ObjectHandler onOutputLeave = IgnoreObject;
			private static readonly StateHandler onState = State;
			private static readonly VoidHandler onDone = Ignore;
			private static readonly VoidHandler onClosed = (data, _) => Toplevel(data).Closed = true;
			private static readonly ObjectHandler onParent = IgnoreObject;
			internal static readonly nint Pointer = ListenerBlock.Create(onTitle, onAppId, onOutputEnter, onOutputLeave, onState, onDone, onClosed, onParent);

			private static void State(nint data, nint handle, nint array)
			{
				var stateArray = Marshal.PtrToStructure<WaylandNative.WlArray>(array);
				var state = 0u;

				for (nuint offset = 0; stateArray.Data != 0 && offset + sizeof(uint) <= stateArray.Size; offset += sizeof(uint))
				{
					var entry = (uint)Marshal.ReadInt32(stateArray.Data, (int)offset);
					if (entry < 32)
						state |= 1u << (int)entry;
				}

				Toplevel(data).State = state;
			}

			private static void Ignore(nint data, nint handle) { }
			private static void IgnoreObject(nint data, nint handle, nint value) { }
			[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void StringHandler(nint data, nint handle, nint value);
			[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ObjectHandler(nint data, nint handle, nint value);
			[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void StateHandler(nint data, nint handle, nint array);
			[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void VoidHandler(nint data, nint handle);
		}

		private static class ExtHandleListener
		{
			private static readonly VoidHandler onClosed = (data, _) => Toplevel(data).Closed = true;
			private static readonly VoidHandler onDone = Ignore;
			private static readonly StringHandler onTitle = (data, _, value) => Toplevel(data).Title = Utf8(value);
			private static readonly StringHandler onAppId = (data, _, value) => Toplevel(data).AppId = Utf8(value);
			private static readonly StringHandler onIdentifier = (data, _, value) => Toplevel(data).Identifier = Utf8(value);
			internal static readonly nint Pointer = ListenerBlock.Create(onClosed, onDone, onTitle, onAppId, onIdentifier);
			private static void Ignore(nint data, nint handle) { }
			[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void VoidHandler(nint data, nint handle);
			[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void StringHandler(nint data, nint handle, nint value);
		}

		private static class ListenerBlock
		{
			// Allocated once per listener class at static-init time; intentionally never freed —
			// these function-pointer blocks must outlive all native callbacks (i.e. process lifetime).
			internal static nint Create(params Delegate[] delegates)
			{
				var block = Marshal.AllocHGlobal(delegates.Length * IntPtr.Size);

				for (var i = 0; i < delegates.Length; i++)
					Marshal.WriteIntPtr(block, i * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(delegates[i]));

				return block;
			}
		}

		private static class Interfaces
		{
			internal static readonly WaylandNative.ProtocolInterface WlrHandle = new(WlrHandleName, 3,
				[
					("set_maximized", "", []), ("unset_maximized", "", []), ("set_minimized", "", []), ("unset_minimized", "", []),
					("activate", "o", [WaylandNative.SeatInterface]), ("close", "", []),
					("set_rectangle", "oiiii", [0]), ("destroy", "", []), ("set_fullscreen", "?o", [WaylandNative.OutputInterface]),
					("unset_fullscreen", "", [])
				],
				[
					("title", "s", []), ("app_id", "s", []), ("output_enter", "o", [WaylandNative.OutputInterface]),
					("output_leave", "o", [WaylandNative.OutputInterface]), ("state", "a", []), ("done", "", []),
					("closed", "", []), ("parent", "3?o", [0])
				]);

			internal static readonly WaylandNative.ProtocolInterface WlrManager = new(WlrManagerName, 3,
				[("stop", "", [])],
				[("toplevel", "n", [WlrHandle.Pointer]), ("finished", "", [])]);

			internal static readonly WaylandNative.ProtocolInterface ExtHandle = new(ExtHandleName, 1,
				[("destroy", "", [])],
				[("closed", "", []), ("done", "", []), ("title", "s", []), ("app_id", "s", []), ("identifier", "s", [])]);

			internal static readonly WaylandNative.ProtocolInterface ExtList = new(ExtListName, 1,
				[("stop", "", []), ("destroy", "", [])],
				[("toplevel", "n", [ExtHandle.Pointer]), ("finished", "", [])]);

			private const string ExtHandleName = "ext_foreign_toplevel_handle_v1";
			private const string WlrHandleName = "zwlr_foreign_toplevel_handle_v1";
		}
	}
}
#endif
