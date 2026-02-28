#if !WINDOWS
namespace Keysharp.Core.Unix
{
	internal static class SystemInformation
	{
		internal static Size SmallIconSize => new Size(16, 16);
		internal static Size IconSize => new Size(32, 32);
	}
}
#endif
