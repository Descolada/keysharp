using System.ComponentModel.Design.Serialization;

namespace Keysharp.Core.Common.ObjectBase
{
    public class Class(params object[] args) : KeysharpObject(args)
    {
		public static object staticCall(object @this, params object[] args)
        {
            if (args.Length == 0) 
                return new Class();

            string name = args[0] as string;
            Any baseClass = name == null ? args[0] as Any : args.Length > 1 ? args[1] as Any : null;
            int skip = 2;
            var vars = TheScript.Vars;
            if (name == null)
            {
                name = "";
                skip--;
            }
            if (baseClass == null)
            {
                baseClass = vars.Statics[typeof(KeysharpObject)];
                skip--;
            }
            args = args[skip..^0];

            var actualType = baseClass.GetType();
			Any staticInst = (Any)RuntimeHelpers.GetUninitializedObject(actualType);
			staticInst.type = typeof(Class); staticInst.InitializePrivates();

			if (Script.GetPropertyValueOrNull(baseClass, "Prototype") is not Any userProto)
				return Errors.ErrorOccurred("The base class must have a prototype");


			staticInst.SetBaseInternal(baseClass);
            staticInst.type = baseClass.type;

            var proto = new Prototype(actualType);
            proto.InitializePrivates();
            proto.SetBaseInternal(userProto);

            if (userProto.op != null)
            {
                proto.EnsureOwnProps();
                foreach (var (key, value) in userProto.op)
                    proto.DefinePropInternal(key, new OwnPropsDesc(proto, value.Value, value.Get, value.Set, value.Call));
            }
			proto.DefinePropInternal("__Class", new OwnPropsDesc(proto, name));
			staticInst.DefinePropInternal("Prototype", new OwnPropsDesc(staticInst, proto));

			_ = Script.InvokeMeta(staticInst, "__Init");
			_ = Script.InvokeMeta(staticInst, "__New", args);

			return staticInst;
        }

        internal object Call(params object[] args)
        {
			var proto = (this.op["Prototype"].Value ?? Script.GetPropertyValueOrNull(this, "Prototype")) as Any;
			
			var kso = FastCtor.Call(proto.type, null) as Any;
			kso.type = proto.type;

			kso.SetBaseInternal(proto);

			Script.InvokeMeta(kso, "__Init");
			Script.InvokeMeta(kso, "__New", args);
            return kso;
		}
	}

    public class Prototype : KeysharpObject
    {
        public Prototype(params object[] args) : base(args) 
        {
            isPrototype = true;
            type = typeof(Prototype);
        }
        internal Prototype(Type t) : base()
        {
            isPrototype = true;
            type = t;
        }
	}
}
