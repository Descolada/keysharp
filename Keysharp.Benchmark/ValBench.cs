using System.Globalization;
using System.Runtime.CompilerServices;
using Keysharp.Scripting;
using static Keysharp.Scripting.Script;
using Array = Keysharp.Core.Array;

namespace Keysharp.Benchmark
{
	public class ValBench : BaseTest
	{
		object ola = 1L, olb = 2L;
		Val vla = 1L, vlb = 2L;
		object oda = 3.0, odb = 4.0;
		Val vda = 3.0, vdb = 4.0;
		object osa = "1", osb = "2";
		Val vsa = "1", vsb = "2";

		KsValue ala = 1L, alb = 2L;
		KsValue asa = "1", asb = "2";
		KsValue ada = 3.0, adb = 4.0;

		[Params(50000L)]
		public long Size { get; set; }
		[Benchmark(Baseline = true)]
		public void NativeCastAddLong()
		{
			var total = 0L;
			for (var i = 0; i < Size; i++)
				total += (long)ola + (long)olb;
		}

		[Benchmark]
		public void OperateAddLong()
		{
			object total = 0L;
			for (var i = 0; i < Size; i++)
				total = Operate(Operator.Add, total, Operate(Operator.Add, ola, olb));
		}

		[Benchmark]
		public void ValAddLong()
		{
			Val total = 0L;
			for (var i = 0; i < Size; i++)
				total = Val.Add(total, Val.Add(vla, vlb));
		}

		[Benchmark]
		public void KsValueAddLong()
		{
			KsValue total = 0L;
			for (var i = 0; i < Size; i++)
				total = KsValue.Add(total, KsValue.Add(ala, alb));
		}

		[Benchmark]
		public void NativeGetLong()
		{
			long total = 0L;
			for (var i = 0; i < Size; i++)
				total += (long)ola + (long)olb;
		}

		[Benchmark]
		public void OperateGetLong()
		{
			long total = 0L;
			for (var i = 0; i < Size; i++)
				total += ola.Al() + olb.Al();
		}

		[Benchmark]
		public void ValGetLong()
		{
			long total = 0L;
			for (var i = 0; i < Size; i++)
				total += vla.Al() + vlb.Al();
		}

		[Benchmark]
		public void KsValueGetLong()
		{
			long total = 0L;
			for (var i = 0; i < Size; i++)
				total += ala.Al() + alb.Al();
		}

		[Benchmark]
		public void OperateAddLongDouble()
		{
			object total = 0L;
			for (var i = 0; i < Size; i++)
				total = Operate(Operator.Add, total, Operate(Operator.Add, ola, odb));
		}

		[Benchmark]
		public void ValAddLongDouble()
		{
			Val total = 0L;
			for (var i = 0; i < Size; i++)
				total = Val.Add(total, Val.Add(vla, vdb));
		}


		[Benchmark]
		public void KsValueAddLongDouble()
		{
			KsValue total = 0L;
			for (var i = 0; i < Size; i++)
				total = KsValue.Add(total, KsValue.Add(ala, adb));
		}

		[Benchmark]
		public void OperateAddString()
		{
			object total = 0L;
			for (var i = 0; i < Size; i++)
				total = Operate(Operator.Add, total, Operate(Operator.Add, osa, osb));
		}

		[Benchmark]
		public void ValAddString()
		{
			Val total = 0L;
			for (var i = 0; i < Size; i++)
				total = Val.Add(total, Val.Add(vsa, vsb));
		}


		[Benchmark]
		public void KsValueAddString()
		{
			KsValue total = 0L;
			for (var i = 0; i < Size; i++)
				total = KsValue.Add(total, KsValue.Add(asa, asb));
		}

		[Benchmark]
		public void OperateConcatString()
		{
		object total = 0L;
		for (var i = 0; i < Size; i++)
			total = Operate(Operator.Concat, osa, osb);
		}

		[Benchmark]
		public void ValConcatString()
		{
		Val total = 0L;
		for (var i = 0; i < Size; i++)
			total = Val.Concat(vsa, vsb);
		}

		[Benchmark]
		public void KsValueConcatString()
		{
			KsValue total = 0L;
			for (var i = 0; i < Size; i++)
				total = KsValue.Concat(asa, asb);
		}

		[Benchmark]
		public void ObjectLongIdentityEquality()
		{
		object total = 0L;
		for (var i = 0; i < Size; i++)
			total = Operate(Operator.IdentityEquality, ola, olb);
		}

		[Benchmark]
		public void ValLongIdentityEquality()
		{
		Val total = 0L;
		for (var i = 0; i < Size; i++)
			total = Val.IdentityEquality(vla, vlb);
		}

		[Benchmark]
		public void KsValueLongIdentityEquality()
		{
			KsValue total = 0L;
			for (var i = 0; i < Size; i++)
				total = KsValue.IdentityEquality(ala, alb);
		}

		[Benchmark]
		public void ObjectStringValueEquality()
		{
		object total = 0L;
		for (var i = 0; i < Size; i++)
			total = Operate(Operator.ValueEquality, osa, osb);
		}

		[Benchmark]
		public void ValStringValueEquality()
		{
		Val total = 0L;
		for (var i = 0; i < Size; i++)
			total = Val.ValueEquality(vsa, vsb);
		}

		[Benchmark]
		public void KsValueStringValueEquality()
		{
			KsValue total = 0L;
			for (var i = 0; i < Size; i++)
				total = KsValue.ValueEquality(asa, asb);
		}

		[GlobalSetup]
		public void Setup()
		{
		}
	}
}