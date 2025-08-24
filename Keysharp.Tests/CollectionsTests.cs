using Assert = NUnit.Framework.Legacy.ClassicAssert;
using Buffer = Keysharp.Core.Buffer;

namespace Keysharp.Tests
{
	public class CollectionsTests : TestRunner
	{
		[Test, Category("Collections"), NonParallelizable]
		public void Array()
		{
			var arr = new Keysharp.Core.Array(
				new object[] {
					10L,
					20L,
					30L
				}.Select(Primitive.From));
			var index = 0;

			foreach (var (i, v) in (IEnumerable<(object, object)>)arr)
			{
				if (index == 0)
				{
					Assert.AreEqual(i, (LongPrimitive)1L);
					Assert.AreEqual(v, (LongPrimitive)10L);
				}
				else if (index == 1)
				{
					Assert.AreEqual(i, (LongPrimitive)2L);
					Assert.AreEqual(v, (LongPrimitive)20L);
				}
				else if (index == 2)
				{
					Assert.AreEqual(i, (LongPrimitive)3L);
					Assert.AreEqual(v, (LongPrimitive)30L);
				}

				index++;
			}

			index = 0;

			foreach (var (i, _) in (IEnumerable<(object, object)>)arr)
			{
				if (index == 0)
					Assert.AreEqual(i, (LongPrimitive)1L);
				else if (index == 1)
					Assert.AreEqual(i, (LongPrimitive)2L);
				else if (index == 2)
					Assert.AreEqual(i, (LongPrimitive)3L);

				index++;
			}

			index = 0;

			foreach (var (_, v) in (IEnumerable<(object, object)>)arr)
			{
				if (index == 0)
					Assert.AreEqual(v, (LongPrimitive)10L);
				else if (index == 1)
					Assert.AreEqual(v, (LongPrimitive)20L);
				else if (index == 2)
					Assert.AreEqual(v, (LongPrimitive)30L);

				index++;
			}

			Assert.AreEqual(arr.ToString(), "[10, 20, 30]");
			Assert.IsTrue(TestScript("collections-array", true));
		}

		[Test, Category("Collections")]
		public void Map()
		{
			var arr = Keysharp.Core.Collections.Map(
						  new object[] {
							  "one", 1L,
							  "two", 2L,
							  "three", 3L
						  }.Select(Primitive.From).ToArray());

			foreach (var (k, v) in (IEnumerable<(object, object)>)arr)
			{
				if ((StringPrimitive)k == "one")
				{
					Assert.AreEqual(v, (LongPrimitive)1L);
				}
				else if ((StringPrimitive)k == "two")
				{
					Assert.AreEqual(v, (LongPrimitive)2L);
				}
				else if ((StringPrimitive)k == "three")
				{
					Assert.AreEqual(v, (LongPrimitive)3L);
				}
				else
					Assert.IsTrue(false);
			}

			foreach (var (_, v) in (IEnumerable<(object, object)>)arr)
			{
				if ((LongPrimitive)v == 1L)
				{
				}
				else if ((LongPrimitive)v == 2L)
				{
				}
				else if ((LongPrimitive)v == 3L)
				{
				}
				else
					Assert.IsTrue(false);
			}

			foreach (var (k, _) in (IEnumerable<(object, object)>)arr)
			{
				if ((StringPrimitive)k == "one")
				{
				}
				else if ((StringPrimitive)k == "two")
				{
				}
				else if ((StringPrimitive)k == "three")
				{
				}
				else
					Assert.IsTrue(false);
			}

			System.Array sa = new object[6];
			arr.CopyTo(sa, 0);
			Assert.AreEqual(sa.GetValue(0), (StringPrimitive)"one");
			Assert.AreEqual(sa.GetValue(1), (LongPrimitive)1L);
			Assert.AreEqual(sa.GetValue(2), (StringPrimitive)"three");
			Assert.AreEqual(sa.GetValue(3), (LongPrimitive)3L);
			Assert.AreEqual(sa.GetValue(4), (StringPrimitive)"two");
			Assert.AreEqual(sa.GetValue(5), (LongPrimitive)2L);
			//
			sa = new object[3];
			arr.CopyTo(sa, 0);
			Assert.AreEqual(sa.GetValue(0), (StringPrimitive)"one");
			Assert.AreEqual(sa.GetValue(1), (LongPrimitive)1L);
			Assert.AreEqual(sa.GetValue(2), (StringPrimitive)"three");
			//
			Assert.AreEqual(arr.ToString(), "{\"one\": 1, \"three\": 3, \"two\": 2}");
			Assert.IsTrue(TestScript("collections-map", true));
		}

		[Test, Category("Collections")]
		public void Buffer()
		{
			var buf = new Buffer(5, 10);
			Assert.AreEqual(5L, (long)buf.Size);

			for (var i = 1; i <= (long)buf.Size; i++)
			{
				long p = buf[i];
				Assert.AreEqual(10L, p);
			}

			buf.Size = 10;
			Assert.AreEqual(10L, (long)buf.Size);

			for (var i = 1; i <= 5; i++)//Ensure original values were copied. Subsequent values are undefined.
			{
				long p = buf[i];
				Assert.AreEqual(10L, p);
			}

			Assert.IsTrue(TestScript("collections-buffer", true));
		}
	}
}