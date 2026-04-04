namespace Keysharp.Runtime
{
	[AttributeUsage(AttributeTargets.Assembly)]
	public sealed class AssemblyBuildVersionAttribute : Attribute
	{
		public string Version { get; }

		public AssemblyBuildVersionAttribute(string v) => Version = v;
	}

	[AttributeUsage(AttributeTargets.Field | AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Class, Inherited = false)]
	public sealed class Export : Attribute
	{
		public Export()
		{ }
	}

	[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Class | AttributeTargets.Interface, Inherited = false)]
	public sealed class PublicHiddenFromUser : Attribute
	{
		public PublicHiddenFromUser()
		{ }
	}

	[AttributeUsage(AttributeTargets.Parameter)]
	public sealed class ByRefAttribute : Attribute { }

	[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
	public sealed class UserDeclaredNameAttribute : Attribute
	{
		public string Name { get; }
		public UserDeclaredNameAttribute(string name) => Name = name;
	}

	public enum eScriptInstance
	{
		Force,
		Ignore,
		Prompt,
		Off
	}
}
