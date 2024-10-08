﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Confuser.Core;
using Confuser.Core.Services;
using Confuser.Protections;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;
using SR = System.Reflection;

namespace KoiVM.Confuser.Internal {
	[SR.ObfuscationAttribute(Exclude = false, Feature = "+koi;")]
	public class MarkPhase : ProtectionPhase {
		public MarkPhase(Protection parent)
			: base(parent) {
		}

		public override ProtectionTargets Targets {
			get { return ProtectionTargets.Methods; }
		}

		public override string Name {
			get { return "Mark methods to virtualize"; }
		}

		protected override void Execute(ConfuserContext context, ProtectionParameters parameters) {
			var vr = context.Annotations.Get<Virtualizer>(context, Fish.VirtualizerKey);

			var marker = context.Registry.GetService<IMarkerService>();
			var refProxy = context.Registry.GetService<IReferenceProxyService>();
			var antiTamper = context.Registry.GetService<IAntiTamperService>();
			var compression = context.Registry.GetService<ICompressionService>();

			var methods = new HashSet<MethodDef>(parameters.Targets.OfType<MethodDef>());
			var refRepl = new Dictionary<IMemberRef, IMemberRef>();

			var oldType = context.CurrentModule.GlobalType;
			var newType = new TypeDefUser(oldType.Name);
			oldType.Name = "Koi";
			oldType.BaseType = context.CurrentModule.CorLibTypes.GetTypeRef("System", "Object");
			context.CurrentModule.Types.Insert(0, newType);

			var old_cctor = oldType.FindOrCreateStaticConstructor();
			var cctor = newType.FindOrCreateStaticConstructor();
			old_cctor.Name = "Koi";
			old_cctor.IsRuntimeSpecialName = false;
			old_cctor.IsSpecialName = false;
			old_cctor.Access = MethodAttributes.PrivateScope;
			cctor.Body = new CilBody(true, new List<Instruction> {
				Instruction.Create(OpCodes.Call, old_cctor),
				Instruction.Create(OpCodes.Ret)
			}, new List<ExceptionHandler>(), new List<Local>());

			marker.Mark(cctor, Parent);
			antiTamper.ExcludeMethod(context, cctor);

			for (int i = 0; i < oldType.Methods.Count; i++) {
				var nativeMethod = oldType.Methods[i];
				if (nativeMethod.IsNative) {
					var methodStub = new MethodDefUser(nativeMethod.Name, nativeMethod.MethodSig.Clone());
					methodStub.Attributes = MethodAttributes.Assembly | MethodAttributes.Static;
					methodStub.Body = new CilBody();
					methodStub.Body.Instructions.Add(new Instruction(OpCodes.Jmp, nativeMethod));
					methodStub.Body.Instructions.Add(new Instruction(OpCodes.Ret));

					oldType.Methods[i] = methodStub;
					newType.Methods.Add(nativeMethod);
					refRepl[nativeMethod] = methodStub;
					marker.Mark(methodStub, Parent);
					antiTamper.ExcludeMethod(context, methodStub);
				}
			}

			compression.TryGetRuntimeDecompressor(context.CurrentModule, def => {
				if (def is MethodDef)
					methods.Remove((MethodDef)def);
			});

			var toProcess = new Dictionary<ModuleDef, List<MethodDef>>();
			foreach (var entry in new Scanner(context.CurrentModule, methods).Scan().WithProgress(context.Logger)) {
				var isExport = entry.Item2;
				isExport |= context.Annotations.Get<object>(entry.Item1, Fish.ExportKey) != null;
				isExport |= refProxy.IsTargeted(context, entry.Item1);

				if (!isExport)
					antiTamper.ExcludeMethod(context, entry.Item1);
				vr.AddMethod(entry.Item1, isExport);
				toProcess.AddListEntry(entry.Item1.Module, entry.Item1);
				context.CheckCancellation();
			}

			context.CurrentModuleWriterOptions.WriterEvent += new Listener {
				ctx = context,
				vr = vr,
				methods = toProcess,
				refRepl = refRepl
			}.OnWriterEvent;
		}

		class Listener {
			public ConfuserContext ctx;
			public Virtualizer vr;
			public Dictionary<ModuleDef, List<MethodDef>> methods;
			public Dictionary<IMemberRef, IMemberRef> refRepl;
			IModuleWriterListener commitListener = null;

			public void OnWriterEvent(object sender, ModuleWriterEventArgs e) {
				var writer = (ModuleWriter)sender;
				if (commitListener != null)
					commitListener.OnWriterEvent(writer, e.Event);

				if (e.Event == ModuleWriterEvent.MDBeginWriteMethodBodies && methods.ContainsKey(writer.Module)) {
					ctx.Logger.Debug("Virtualizing methods...");

					vr.ProcessMethods(writer.Module, (num, total) => {
						ctx.Logger.Progress(num, total);
						ctx.CheckCancellation();
					});
					ctx.Logger.EndProgress();

					foreach (var repl in refRepl)
						vr.Runtime.Descriptor.Data.ReplaceReference(repl.Key, repl.Value);

					commitListener = vr.CommitModule(ctx.CurrentModule, (num, total) => {
						ctx.Logger.Progress(num, total);
						ctx.CheckCancellation();
					});
				}
				else if (commitListener != null && e.Event == ModuleWriterEvent.End && vr.ExportDbgInfo) {
					var mapName = Path.ChangeExtension(writer.Module.Name, "map");
					var mapPath = Path.GetFullPath(Path.Combine(ctx.OutputDirectory, mapName));
					Directory.CreateDirectory(ctx.OutputDirectory);
					File.WriteAllBytes(mapPath, vr.Runtime.DebugInfo);
				}
			}
		}
	}
}