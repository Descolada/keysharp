namespace Keysharp.Scripting
{
	internal partial class Parser
	{
		internal enum SymbolKind
		{
			Function,
			Class
		}

		internal enum SymbolEmitKind
		{
			None,
			GlobalFuncObj,
			ClassType,
			ClassStaticVar
		}

		internal readonly struct SymbolKey : IEquatable<SymbolKey>
		{
			public readonly SymbolKind Kind;
			public readonly string ScopeLower;
			public readonly string SemanticLower;

			public SymbolKey(SymbolKind kind, string scopeName, string semanticLower)
			{
				Kind = kind;
				ScopeLower = scopeName?.ToLowerInvariant() ?? string.Empty;
				SemanticLower = semanticLower ?? string.Empty;
			}

			public bool Equals(SymbolKey other)
				=> Kind == other.Kind
				&& string.Equals(ScopeLower, other.ScopeLower, StringComparison.Ordinal)
				&& string.Equals(SemanticLower, other.SemanticLower, StringComparison.Ordinal);

			public override bool Equals(object obj) => obj is SymbolKey other && Equals(other);

			public override int GetHashCode() => HashCode.Combine((int)Kind, ScopeLower, SemanticLower);
		}

		internal sealed class SymbolInfo
		{
			public SymbolKey Key { get; }
			public SymbolKind Kind { get; }
			public SymbolEmitKind EmitKind { get; }
			public string DeclaredName { get; }
			public string SemanticLower { get; }
			public string CSharpName { get; }

			public SymbolInfo(SymbolKey key, SymbolKind kind, SymbolEmitKind emitKind, string declaredName, string semanticLower, string csharpName)
			{
				Key = key;
				Kind = kind;
				EmitKind = emitKind;
				DeclaredName = declaredName;
				SemanticLower = semanticLower;
				CSharpName = csharpName;
			}

			public static SymbolInfo CreateFunction(string scopeName, string semanticLower, string declaredName, string csharpName)
			{
				var key = new SymbolKey(SymbolKind.Function, scopeName, semanticLower);
				return new SymbolInfo(key, SymbolKind.Function, SymbolEmitKind.GlobalFuncObj, declaredName, semanticLower, csharpName);
			}

			public static SymbolInfo CreateClass(string scopeName, string semanticLower, string declaredName)
			{
				var key = new SymbolKey(SymbolKind.Class, scopeName, semanticLower);
				return new SymbolInfo(key, SymbolKind.Class, SymbolEmitKind.ClassType, declaredName, semanticLower, declaredName);
			}

			public static SymbolInfo CreateClassStaticVar(string scopeName, string semanticLower, string typeName, string csharpName)
			{
				var key = new SymbolKey(SymbolKind.Class, scopeName, semanticLower);
				return new SymbolInfo(key, SymbolKind.Class, SymbolEmitKind.ClassStaticVar, typeName, semanticLower, csharpName);
			}
		}

		internal sealed class SymbolTable
		{
			private readonly Dictionary<SymbolKey, SymbolInfo> symbols = new();
			private readonly List<SymbolInfo> ordered = new();

			internal void Clear()
			{
				symbols.Clear();
				ordered.Clear();
			}

			internal bool TryAdd(SymbolInfo info)
			{
				if (symbols.ContainsKey(info.Key))
					return false;
				symbols.Add(info.Key, info);
				ordered.Add(info);
				return true;
			}

			internal bool TryGet(SymbolKey key, out SymbolInfo info) => symbols.TryGetValue(key, out info);

			internal IEnumerable<SymbolInfo> All => ordered;
		}
	}
}
