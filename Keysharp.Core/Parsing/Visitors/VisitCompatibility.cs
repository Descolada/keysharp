using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using static Keysharp.Parsing.Parser;
using static Keysharp.Parsing.Antlr.MainParser;

namespace Keysharp.Parsing
{
	internal partial class VisitMain : MainParserBaseVisitor<SyntaxNode>
	{
		private sealed class CompatibilityFrame
		{
			internal string SourceName;
			internal Semver.SemVersion Mode;
		}

		private readonly Stack<CompatibilityFrame> compatibilityFrames = new();
		private bool moduleStartupCompatibilityLocked;
		private bool InIncludeCompatibilitySource => compatibilityFrames.Count > 1;

		private Semver.SemVersion CurrentSourceCompatibilityMode =>
			compatibilityFrames.Count != 0
				? compatibilityFrames.Peek().Mode
				: parser.currentModule?.CompatibilityVersion ?? Script.DefaultCompatibilityVersion;

		private Semver.SemVersion CurrentCompatibilityMode =>
			InIncludeCompatibilitySource
				? compatibilityFrames.Peek().Mode
				: parser.functionDepth > 0
					? parser.currentFunc?.CompatibilityVersion ?? CurrentSourceCompatibilityMode
					: CurrentSourceCompatibilityMode;

		private static readonly Semver.SemVersion[] CompatibilityModeCandidates =
			[
				new(2, 0, 0),
				new(2, 1, 0),
			];

		private static Semver.SemVersion LatestCompatibilityMode => CompatibilityModeCandidates[^1];

		private void ApplyCompatibilityModeDirective(Semver.SemVersion mode)
		{
			if (InIncludeCompatibilitySource)
			{
				compatibilityFrames.Peek().Mode = mode;
			}
			else if (parser.functionDepth > 0)
			{
				parser.currentFunc.CompatibilityVersion = mode;
				parser.currentFunc.EmitCompatibilityAttribute = mode.CompareSortOrderTo(parser.currentModule.CompatibilityVersion) != 0;
				return;
			}
			else if (compatibilityFrames.Count != 0)
			{
				compatibilityFrames.Peek().Mode = mode;
			}

			if (parser.functionDepth > 0)
				return;

			if (moduleStartupCompatibilityLocked)
				return;

			parser.currentModule.CompatibilityVersion = mode;

			if (parser.autoExecFunc != null)
				parser.autoExecFunc.CompatibilityVersion = mode;

			moduleStartupCompatibilityLocked = true;
		}

		private static Semver.SemVersion GetCompatibilityModeFromRequirement(string requirement)
		{
			foreach (var candidate in CompatibilityModeCandidates)
				if (CompatibilityVersions.RequirementAllowsCompatibilityLine(requirement, candidate))
					return candidate;

			return LatestCompatibilityMode;
		}
	}
}
