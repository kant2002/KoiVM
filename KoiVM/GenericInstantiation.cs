using System;
using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace KoiVM {
	public class GenericInstantiation {
		readonly Dictionary<MethodSpec, MethodDef> instantiations =
			new Dictionary<MethodSpec, MethodDef>(MethodEqualityComparer.CompareDeclaringTypes);

		public event Func<MethodSpec, bool> ShouldInstantiate;

		public void EnsureInstantiation(MethodDef method, Action<MethodSpec, MethodDef> onInstantiated) {
			foreach (var instr in method.Body.Instructions) {
				if (instr.Operand is MethodSpec) {
					var spec = (MethodSpec)instr.Operand;
					if (ShouldInstantiate != null && !ShouldInstantiate(spec))
						continue;

					MethodDef instantiation;
					if (!Instantiate(spec, out instantiation))
						onInstantiated(spec, instantiation);
					instr.Operand = instantiation;
				}
			}
		}

		public bool Instantiate(MethodSpec methodSpec, out MethodDef def) {
			if (instantiations.TryGetValue(methodSpec, out def))
				return true;

			var genericArguments = new GenericArguments();
			genericArguments.PushMethodArgs(methodSpec.GenericInstMethodSig.GenericArguments);
			var originDef = methodSpec.Method.ResolveMethodDefThrow();

			var newSig = ResolveMethod(originDef.MethodSig, genericArguments);
			newSig.Generic = false;
			newSig.GenParamCount = 0;

			string newName = originDef.Name;
			foreach (var typeArg in methodSpec.GenericInstMethodSig.GenericArguments)
				newName += ";" + typeArg.TypeName;

			def = new MethodDefUser(newName, newSig, originDef.ImplAttributes, originDef.Attributes);
			var thisParam = originDef.HasThis ? originDef.Parameters[0].Type : null;
			def.DeclaringType2 = originDef.DeclaringType2;
			if (thisParam != null) {
				def.Parameters[0].Type = thisParam;
			}

			foreach (var declSec in originDef.DeclSecurities)
				def.DeclSecurities.Add(declSec);
			def.ImplMap = originDef.ImplMap;
			foreach (var ov in originDef.Overrides)
				def.Overrides.Add(ov);

			def.Body = new CilBody();
			def.Body.InitLocals = originDef.Body.InitLocals;
			def.Body.MaxStack = originDef.Body.MaxStack;
			foreach (var variable in originDef.Body.Variables) {
				var newVar = new Local(variable.Type);
				def.Body.Variables.Add(newVar);
			}

			var instrMap = new Dictionary<Instruction, Instruction>();
			foreach (var instr in originDef.Body.Instructions) {
				var newInstr = new Instruction(instr.OpCode, ResolveOperand(instr.Operand, genericArguments));
				def.Body.Instructions.Add(newInstr);
				instrMap[instr] = newInstr;
			}
			foreach (var instr in def.Body.Instructions) {
				if (instr.Operand is Instruction)
					instr.Operand = instrMap[(Instruction)instr.Operand];
				else if (instr.Operand is Instruction[]) {
					var targets = (Instruction[])((Instruction[])instr.Operand).Clone();
					for (int i = 0; i < targets.Length; i++)
						targets[i] = instrMap[targets[i]];
					instr.Operand = targets;
				}
			}
			def.Body.UpdateInstructionOffsets();

			foreach (var eh in originDef.Body.ExceptionHandlers) {
				var newEH = new ExceptionHandler(eh.HandlerType);
				newEH.TryStart = instrMap[eh.TryStart];
				newEH.HandlerStart = instrMap[eh.HandlerStart];
				if (eh.TryEnd != null)
					newEH.TryEnd = instrMap[eh.TryEnd];
				if (eh.HandlerEnd != null)
					newEH.HandlerEnd = instrMap[eh.HandlerEnd];
				if (eh.CatchType != null)
					newEH.CatchType = genericArguments.Resolve(newEH.CatchType.ToTypeSig()).ToTypeDefOrRef();
				else if (eh.FilterStart != null)
					newEH.FilterStart = instrMap[eh.FilterStart];

				def.Body.ExceptionHandlers.Add(newEH);
			}

			instantiations[methodSpec] = def;
			return false;
		}

		FieldSig ResolveField(FieldSig sig, GenericArguments genericArgs) {
			var newSig = sig.Clone();
			newSig.Type = genericArgs.ResolveType(newSig.Type);
			return newSig;
		}

		GenericInstMethodSig ResolveInst(GenericInstMethodSig sig, GenericArguments genericArgs) {
			var newSig = sig.Clone();
			for (int i = 0; i < newSig.GenericArguments.Count; i++)
				newSig.GenericArguments[i] = genericArgs.ResolveType(newSig.GenericArguments[i]);
			return newSig;
		}

		MethodSig ResolveMethod(MethodSig sig, GenericArguments genericArgs) {
			var newSig = sig.Clone();

			for (int i = 0; i < newSig.Params.Count; i++)
				newSig.Params[i] = genericArgs.ResolveType(newSig.Params[i]);

			if (newSig.ParamsAfterSentinel != null) {
				for (int i = 0; i < newSig.ParamsAfterSentinel.Count; i++)
					newSig.ParamsAfterSentinel[i] = genericArgs.ResolveType(newSig.ParamsAfterSentinel[i]);
			}

			newSig.RetType = genericArgs.ResolveType(newSig.RetType);
			return newSig;
		}

		object ResolveOperand(object operand, GenericArguments genericArgs) {
			if (operand is MemberRef) {
				var memberRef = (MemberRef)operand;
				if (memberRef.IsFieldRef) {
					var field = ResolveField(memberRef.FieldSig, genericArgs);
					memberRef = new MemberRefUser(memberRef.Module, memberRef.Name, field, memberRef.Class);
				}
				else {
					var method = ResolveMethod(memberRef.MethodSig, genericArgs);
					memberRef = new MemberRefUser(memberRef.Module, memberRef.Name, method, memberRef.Class);
				}
				return memberRef;
			}
			if (operand is TypeSpec) {
				var sig = ((TypeSpec)operand).TypeSig;
				return genericArgs.ResolveType(sig).ToTypeDefOrRef();
			}
			if (operand is MethodSpec) {
				var spec = (MethodSpec)operand;
				spec = new MethodSpecUser(spec.Method, ResolveInst(spec.GenericInstMethodSig, genericArgs));
				return spec;
			}
			return operand;
		}
    }
    readonly struct GenericArgumentsStack
    {
        readonly List<IList<TypeSig>> argsStack;
        readonly bool isTypeVar;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="isTypeVar"><c>true</c> if it's for generic types, <c>false</c> if generic methods</param>
        public GenericArgumentsStack(bool isTypeVar)
        {
            argsStack = new List<IList<TypeSig>>();
            this.isTypeVar = isTypeVar;
        }

        /// <summary>
        /// Pushes generic arguments
        /// </summary>
        /// <param name="args">The generic arguments</param>
        public void Push(IList<TypeSig> args) => argsStack.Add(args);

        /// <summary>
        /// Pops generic arguments
        /// </summary>
        /// <returns>The popped generic arguments</returns>
        public IList<TypeSig> Pop()
        {
            int index = argsStack.Count - 1;
            var result = argsStack[index];
            argsStack.RemoveAt(index);
            return result;
        }

        /// <summary>
        /// Resolves a generic argument
        /// </summary>
        /// <param name="number">Generic variable number</param>
        /// <returns>A <see cref="TypeSig"/> or <c>null</c> if none was found</returns>
        public TypeSig Resolve(uint number)
        {
            TypeSig result = null;
            for (int i = argsStack.Count - 1; i >= 0; i--)
            {
                var args = argsStack[i];
                if (number >= args.Count)
                    return null;
                var typeSig = args[(int)number];
                var gvar = typeSig as GenericSig;
                if (gvar is null || gvar.IsTypeVar != isTypeVar)
                    return typeSig;
                result = gvar;
                number = gvar.Number;
            }
            return result;
        }
    }

    /// <summary>
    /// Replaces generic type/method var with its generic argument
    /// </summary>
    sealed class GenericArguments
    {
        GenericArgumentsStack typeArgsStack = new GenericArgumentsStack(true);
        GenericArgumentsStack methodArgsStack = new GenericArgumentsStack(false);

        /// <summary>
        /// Pushes generic arguments
        /// </summary>
        /// <param name="typeArgs">The generic arguments</param>
        public void PushTypeArgs(IList<TypeSig> typeArgs) => typeArgsStack.Push(typeArgs);

        /// <summary>
        /// Pops generic arguments
        /// </summary>
        /// <returns>The popped generic arguments</returns>
        public IList<TypeSig> PopTypeArgs() => typeArgsStack.Pop();

        /// <summary>
        /// Pushes generic arguments
        /// </summary>
        /// <param name="methodArgs">The generic arguments</param>
        public void PushMethodArgs(IList<TypeSig> methodArgs) => methodArgsStack.Push(methodArgs);

        /// <summary>
        /// Pops generic arguments
        /// </summary>
        /// <returns>The popped generic arguments</returns>
        public IList<TypeSig> PopMethodArgs() => methodArgsStack.Pop();

        /// <summary>
        /// Replaces a generic type/method var with its generic argument (if any). If
        /// <paramref name="typeSig"/> isn't a generic type/method var or if it can't
        /// be resolved, it itself is returned. Else the resolved type is returned.
        /// </summary>
        /// <param name="typeSig">Type signature</param>
        /// <returns>New <see cref="TypeSig"/> which is never <c>null</c> unless
        /// <paramref name="typeSig"/> is <c>null</c></returns>
        public TypeSig Resolve(TypeSig typeSig)
        {
            if (typeSig is null)
                return null;

            var sig = typeSig;

            if (sig is GenericMVar genericMVar)
            {
                var newSig = methodArgsStack.Resolve(genericMVar.Number);
                if (newSig is null || newSig == sig)
                    return sig;
                return newSig;
            }

            if (sig is GenericVar genericVar)
            {
                var newSig = typeArgsStack.Resolve(genericVar.Number);
                if (newSig is null || newSig == sig)
                    return sig;
                return newSig;
            }

            return sig;
        }
    }
}