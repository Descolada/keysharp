﻿namespace Keysharp.Core
{
	/// <summary>
	/// Encapsulates a block of memory for use with advanced techniques such as DllCall, structures, StrPut and raw file I/O.<br/>
	/// Buffer objects are typically created by calling <see cref="Collections.Buffer"/>,<br/>
	/// but can also be returned by <see cref="Files.FileRead"/> with the "RAW" option.
	/// </summary>
	public class Buffer : KeysharpObject, IDisposable, IPointable
	{
		/// <summary>
		/// Whether the object has been disposed or not.
		/// </summary>
		private bool disposed = false;

		/// <summary>
		/// The size of the buffer in bytes.
		/// </summary>
		private long size;

		/// <summary>
		/// Gets the pointer to the memory.
		/// </summary>
		private NativeMemoryHandle _ptr;
		public LongPrimitive Ptr {
			get => new LongPrimitive(_ptr.DangerousGetHandle(), _ptr);
			private set => _ptr = new NativeMemoryHandle(value);
		}

		/// <summary>
		/// Gets or sets the size of the buffer.<br/>
		/// If value is greater than the existing size, a new buffer is created with length == value and<br/>
		/// the existing data in the old buffer is copied to the beginning of the new buffer.<br/>
		/// The old buffer is then deleted.
		/// </summary>
		public LongPrimitive Size
		{
			get => size;

			set
			{
				var val = value.Al();

				if (val > size)
				{
					//var newptr = Marshal.AllocCoTaskMem((int)val);
					var newptr = Marshal.AllocHGlobal((int)val);

					if (_ptr != null)
					{
						unsafe
						{
							var src = (byte*)_ptr.DangerousGetHandle();
							var dst = (byte*)newptr.ToPointer();
							System.Buffer.MemoryCopy(src, dst, val, size);
						}
						var old = _ptr;
						_ptr = new NativeMemoryHandle(newptr);
						old.Dispose();
						//Marshal.FreeCoTaskMem(old);
					}
					else
						_ptr = new NativeMemoryHandle(newptr);
				}

				size = val;
			}
		}


		/// <summary>
		/// Calls <see cref="__New"/> to initialize a new instance of the <see cref="Buffer"/> class.
		/// </summary>
		/// <param name="args">The data to initially store in the buffer</param>
		public Buffer(params object[] args) : base(args) { }

		public static object Call(object @this, object byteCount = null, object fillByte = null)
		{
			Type t = @this.GetType();
			return Activator.CreateInstance(t, [byteCount, fillByte]);
			//new Buffer(byteCount, fillByte);
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="Buffer"/> class.
		/// </summary>
		/// <param name="obj">The optional data to initialize the <see cref="Buffer"/> with. This can be:<br/>
		///     empty: Ptr remains null.<br/>
		///     byte[]: Copied one byte at a time to the pointer.<br/>
		///     <see cref="Array"/>: Convert each element to a byte and copy one at a time to the pointer.<br/>
		///     Integer[, Integer]: Sets length to the first value and optionally sets each byte to the second value.
		/// </param>
		/// <returns>Empty string, unused.</returns>
		public override unsafe object __New(params object[] obj)
		{
			if (obj == null || obj.Length == 0)
			{
				Size = 0;
			}
			else
			{
				var obj0 = obj[0];

				if (obj0 is byte[] bytearray)//This will sometimes be passed internally within the library.
				{
					Size = bytearray.Length;//Performs the allocation.

					if (size > 0)
						Marshal.Copy(bytearray, 0, (nint)_ptr.DangerousGetHandle(), Math.Min((int)size, bytearray.Length));
				}
				else if (obj0 is Array array)
				{
					var ct = array.array.Count;
					Size = ct;
					var bp = (nint)Ptr;

					for (var i = 0; i < ct; i++)
						Unsafe.Write((void*)nint.Add(bp, i), (byte)Script.ForceLong(array.array[i]));//Access the underlying array[] directly for performance.
				}
				else//This will be called by the user.
				{
					var bytecount = obj0.Al(0);
					var fill = obj.Length > 1 ? obj[1].Al(long.MinValue) : long.MinValue;
					Size = bytecount;

					if (bytecount > 0)
					{
						byte val = fill != long.MinValue ? (byte)(fill & 255) : (byte)0;
						Unsafe.InitBlockUnaligned((void*)_ptr.DangerousGetHandle(), val, (uint)bytecount);
					}
				}
			}

			return DefaultObject;
		}

		/// <summary>
		/// Dispose the object and set a flag so it doesn't get disposed twice.
		/// </summary>
		/// <param name="disposing">If true, disposing already, so skip, else dispose.</param>
		public virtual void Dispose(bool disposing)
		{
			if (!disposed)
			{
				_ptr.Dispose();
				//Marshal.FreeCoTaskMem(Ptr);
				Size = 0;
				disposed = true;
			}
		}

		/// <summary>
		/// The implementation for <see cref="IDisposable.Dispose"/> which just calls Dispose(true).
		/// </summary>
		void IDisposable.Dispose()
		{
			Dispose(true);
		}

		/// <summary>
		/// Converts the contents of the buffer to a hex string.
		/// </summary>
		/// 
		public StringPrimitive ToHex() => BitConverter.ToString(ToByteArray()).Replace("-", string.Empty);

		/// <summary>
		/// Converts the contents of the buffer to a base64 string.
		/// </summary>
		public StringPrimitive ToBase64() => Convert.ToBase64String(ToByteArray());

		/// <summary>
		/// Converts the contents of the buffer to a byte array.
		/// </summary>
		public byte[] ToByteArray()
		{
			int size = (int)Size;
			byte[] dataArray = new byte[size];
			Marshal.Copy((nint)_ptr.DangerousGetHandle(), dataArray, 0, size);
			return dataArray;
		}

		/// <summary>
		/// Indexer which retrieves or sets the value of an array element.
		/// </summary>
		/// <param name="index">The index to get a byte from.</param>
		/// <returns>The value at the index.</returns>
		/// <exception cref="IndexError">An <see cref="IndexError"/> exception is thrown if index is zero or out of range.</exception>
		public LongPrimitive this[object index]
		{
			get
			{
				int i = index.Ai();
				if (i > 0 && i <= size)
				{
					unsafe
					{
						var ptr = (byte*)_ptr.DangerousGetHandle();
						return ptr[i - 1];
					}
				}
				else
					return (long)Errors.IndexErrorOccurred($"Invalid index of {index} for buffer of size {Size}.", DefaultErrorLong);
			}
		}
	}

	sealed class NativeMemoryHandle : SafeHandleZeroOrMinusOneIsInvalid
	{
		public NativeMemoryHandle() : base(ownsHandle: true) { }

		public NativeMemoryHandle(IntPtr p, bool owns = true) : base(owns)
		{
			SetHandle(p);
		}

		protected override bool ReleaseHandle()
		{
			Marshal.FreeHGlobal(handle);
			return true;
		}
	}
}