using dnlib.DotNet;
using dnlib.DotNet.Writer;

namespace KoiVM.RT {
    public interface IModuleWriterListener {
        void CommitRuntime(ModuleDef targetModule);
        void OnWriterEvent(ModuleWriterBase writer, ModuleWriterEvent evt);
    }
}
