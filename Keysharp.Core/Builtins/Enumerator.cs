namespace Keysharp.Builtins
{
	public class Enumerator : FuncObj, IEnumerator<object>, IEnumerator<(object, object)>, IDisposable
	{
		private static MethodPropertyHolder callMethod;

		private readonly Func<bool> moveNext;
		private readonly Func<object> currentValue;
		private readonly Func<(object, object)> currentPair;
		private readonly Action reset;
		private readonly Action dispose;
		private readonly IFuncObj callback;

		/// <summary>
		/// The source object being enumerated.
		/// </summary>
		public object Source { get; }

		/// <summary>
		/// The number of items to return for each iteration. Allowed values are 1 and 2:
		/// 1: return just the value in the first position
		/// 2: return the index in the first position and the value in the second.
		/// </summary>
		public long Count { get; }

		public virtual object Current => currentValue != null ? currentValue() : currentPair != null ? currentPair().Item1 : null;

		(object, object) IEnumerator<(object, object)>.Current => GetCurrentPair();

		object IEnumerator.Current => Current;

		public Enumerator(params object[] args) : base(args)
		{
		}

		protected Enumerator(object source, int count)
			: base(CallMethod(), null)
		{
			if (Base == null)
				InitializeBase(typeof(Enumerator));

			Source = source;
			Count = Math.Max(1, count);
			Inst = this;
			HasFinalizer = false;
		}

		internal Enumerator(
			object source,
			int count,
			Func<bool> moveNext,
			Func<object> currentValue,
			Func<(object, object)> currentPair,
			Action reset,
			Action dispose = null)
			: this(source, count)
		{
			this.moveNext = moveNext;
			this.currentValue = currentValue;
			this.currentPair = currentPair;
			this.reset = reset;
			this.dispose = dispose;
		}

		internal Enumerator(object source, int count, IFuncObj callback)
			: this(source, count)
		{
			this.callback = callback;
		}

		private static MethodPropertyHolder CallMethod() => callMethod ??= Reflections.FindAndCacheMethod(typeof(Enumerator), nameof(Call), 1);

		public virtual bool MoveNext() => moveNext != null && moveNext();

		protected virtual (object, object) GetCurrentPair() => currentPair != null ? currentPair() : currentValue != null ? (currentValue(), null) : (null, null);

		void IEnumerator.Reset() => reset?.Invoke();

		void IDisposable.Dispose() => dispose?.Invoke();

		public override object Call(params object[] args)
		{
			try
			{
				if (callback != null)
					return callback.Call(args);

				if (!MoveNext())
				{
					((IDisposable)this).Dispose();
					return false;
				}

				if (args == null || args.Length == 0)
					return true;

				if (args.Length == 1)
				{
					Script.SetPropertyValue(args[0], "__Value", Current);
				}
				else
				{
					var pair = GetCurrentPair();
					Script.SetPropertyValue(args[0], "__Value", pair.Item1);
					Script.SetPropertyValue(args[1], "__Value", pair.Item2);
				}

				return true;
			}
			catch (Exception e)
			{
				throw new Error(e.Message);
			}
		}
	}
}
