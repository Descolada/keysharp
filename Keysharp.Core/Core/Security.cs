namespace Keysharp.Core
{
	/// <summary>
	/// Public interface for security/cryptographic-related functions.
	/// </summary>
	public partial class Ks
	{
		/// <summary>
		/// Encrypt or decrypt data with the AES algorithm.
		/// </summary>
		/// <param name="value">The data to encrypt or decrypt.</param>
		/// <param name="key">The secret key.</param>
		/// <param name="decrypt"><code>true</code> to decrypt the given <paramref name="value"/>, otherwise encrypt.</param>
		/// <returns>The corresponding encrypted or decrypted data.</returns>
		public static Buffer AES(object value, object key, bool decrypt = false) => new (Crypt.Encrypt(value, key, decrypt, Aes.Create()));

		/// <summary>
		/// Calculates the CRC32 polynomial of an object.
		/// </summary>
		/// <param name="value">The object to check.</param>
		/// <returns>A checksum of <paramref name="value"/> as an integer.</returns>
		public static long CRC32(object value)
		{
			var raw = Crypt.ToByteArray(value);
			var alg = new CRC32();
			_ = alg.ComputeHash(raw);
			return alg.Value;
		}

		/// <summary>
		/// Calculates the MD5 hash of an object.
		/// </summary>
		/// <param name="value">The object to hash.</param>
		/// <returns>A 32-character hexadecimal number.</returns>
		public static string MD5(object value) => Crypt.Hash(value, System.Security.Cryptography.MD5.Create());

		/// <summary>
		/// Generates a secure (cryptographic) random number.
		/// </summary>
		/// <param name="min">The lower bound. If either parameter is a <see cref="double"/>, the result uses the floating-point path.</param>
		/// <param name="max">The upper bound. If both parameters are non-<see cref="double"/>, the integer path is used.</param>
		/// <returns>A random number between the specified range. Leave both parameters blank to allow the full numeric range.
		/// If <paramref name="min"/> and <paramref name="max"/> are both non-<see cref="double"/>, the result is an integer.</returns>
		/// <remarks>A cryptographic random number generator produces an output that is computationally infeasible to predict with a probability that is better than one half.
		/// <see cref="Random"/> uses a simpler algorithm which is much faster but less secure.</remarks>
		public static object SecureRandom(object min = null, object max = null)
		{
			if (min is double || max is double)
			{
				var minVal = min.Ad(double.MinValue);
				var maxVal = max.Ad(double.MaxValue);
				var diff = Math.Abs(minVal - maxVal);

				if (diff == 0 && !(minVal == 0 && maxVal == 0))
					return minVal;

				Span<byte> rnd = stackalloc byte[8];
				RandomNumberGenerator.Fill(rnd);
				var value = BitConverter.ToUInt64(rnd);
				var unit = value / (double)ulong.MaxValue;

				var rem = (minVal % 1.0) != 0 || (maxVal % 1.0) != 0;
				if (!rem)
				{
					var range = diff + 1.0;
					var val = Math.Floor(unit * range);
					return minVal + val;
				}

				return minVal + (unit * diff);
			}

			var minInt = min.Ai(int.MinValue);
			var maxInt = max.Ai(int.MaxValue);

			if (minInt == maxInt)
				return (long)minInt;

			if (minInt > maxInt)
			{
				(minInt, maxInt) = (maxInt, minInt);
			}

			var randomInt = RandomNumberGenerator.GetInt32(minInt, maxInt);
			return (long)randomInt;
		}

		/// <summary>
		/// Calculates the SHA1 hash of an object.
		/// </summary>
		/// <param name="value">The object to hash.</param>
		/// <returns>A 40-character hexadecimal number.</returns>
		public static string SHA1(object value) => Crypt.Hash(value, System.Security.Cryptography.SHA1.Create());

		/// <summary>
		/// Calculates the SHA256 hash of an object.
		/// </summary>
		/// <param name="value">The object to hash.</param>
		/// <returns>A 64-character hexadecimal number.</returns>
		public static string SHA256(object value) => Crypt.Hash(value, System.Security.Cryptography.SHA256.Create());

		/// <summary>
		/// Calculates the SHA384 hash of an object.
		/// </summary>
		/// <param name="value">The object to hash.</param>
		/// <returns>A 96-character hexadecimal number.</returns>
		public static string SHA384(object value) => Crypt.Hash(value, System.Security.Cryptography.SHA384.Create());

		/// <summary>
		/// Calculates the SHA512 hash of an object.
		/// </summary>
		/// <param name="value">The object to hash.</param>
		/// <returns>A 128-character hexadecimal number.</returns>
		public static string SHA512(object value) => Crypt.Hash(value, System.Security.Cryptography.SHA512.Create());
	}
}
