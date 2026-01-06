namespace Keysharp.Core.Common.Images
{
	public sealed class GdiHandleHolder : Any, IDisposable
	{
		private readonly bool disposeHandle = true;
#if !WINDOWS
		private static readonly ConcurrentDictionary<nint, GdiHandleHolder> handleCache = new ();
#endif
		internal readonly Image Image;
		private readonly nint handle;
		private bool disposed;


		internal GdiHandleHolder(nint h, bool d)
		{
			handle = h;
			disposeHandle = d;
			if (h == 0) 
				_ = Errors.ErrorOccurred("Invalid HBITMAP provided");
		}

		internal GdiHandleHolder(Image img, bool d)
		{
			disposeHandle = d;
			if (img == null) 
				_ = Errors.ErrorOccurred("Invalid Image object provided");
#if WINDOWS
			if (img is Bitmap bmp)
			{
				handle = bmp.GetHbitmap();
				img.Dispose();
			} 
			else 
			{
				img.Dispose();
				_ = Errors.ErrorOccurred("Currently non-bitmap formats aren't supported");
			}
#else
			Image = img;
			handle = ((Gdk.Pixbuf)img.ControlObject).Handle;
			handleCache[handle] = this;
#endif
		}

		internal static bool TryGet(nint handle, out GdiHandleHolder holder)
		{
#if WINDOWS
			holder = null;
			return false;
#else
			return handleCache.TryGetValue(handle, out holder);
#endif
		}

		internal static void Dispose(nint handle)
		{
#if WINDOWS
			_ = WindowsAPI.DeleteObject(handle);
#else
			if (TryGet(handle, out var holder) && holder is IDisposable id)
				id.Dispose();
#endif
		}

		void IDisposable.Dispose()
		{
			if (disposed)
				return;

			disposed = true;
#if WINDOWS

			if (disposeHandle && handle != 0)
				_ = WindowsAPI.DeleteObject(handle);//Windows specific, figure out how to do this, or if it's even needed on other platforms.//TODO

#else
			if (disposeHandle && Image != null)
				Image.Dispose();
			if (handle != 0)
				_ = handleCache.TryRemove(handle, out _);
#endif
		}

		internal nint Handle => handle;
		public object Ptr => (long)handle;

		public static implicit operator long(GdiHandleHolder holder) => holder.handle.ToInt64();

		public override string ToString() => handle.ToInt64().ToString();
	}
}
