using System.CodeDom;

namespace Keysharp.Scripting
{
	public class CodeLine : CodeLinePragma
	{
		public string Code { get; set; }

		public CodeLine(string fileName, int lineNumber, string code)
			: base(fileName, lineNumber) => Code = code;

		public override string ToString() => string.Format("{0}: {1}", LineNumber, Code);
	}
}