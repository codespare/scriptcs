using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;

namespace ScriptCs.Engine.Roslyn
{
    public static class Extensions
    {
        public static string GetDefaultExtension(this OutputKind kind)
        {
            switch (kind) {
                case OutputKind.ConsoleApplication:
                case OutputKind.WindowsApplication:
                case OutputKind.WindowsRuntimeApplication:
                    return ".exe";

                case OutputKind.DynamicallyLinkedLibrary:
                    return ".dll";

                case OutputKind.NetModule:
                    return ".netmodule";

                case OutputKind.WindowsRuntimeMetadata:
                    return ".winmdobj";

                default:
                    return ".dll";
            }
        }

        public static string MakeSourceModuleName(this Compilation compilation)
        {
            return compilation.Options.ModuleName ??
                   (compilation.AssemblyName != null ? compilation.AssemblyName + compilation.Options.OutputKind.GetDefaultExtension() : UnspecifiedModuleAssemblyName);
        }

        public static string MakeSourceAssemblySimpleName(this Compilation compilation)
        {
            return compilation.AssemblyName ?? UnspecifiedModuleAssemblyName;
        }

        public static readonly string UnspecifiedModuleAssemblyName = "?";

        public static MetadataReference EmitToImageReference(
            this Compilation compilation,
            EmitOptions options = null,
            bool embedInteropTypes = false,
            ImmutableArray<string> aliases = default(ImmutableArray<string>)
            //,DiagnosticDescription[] expectedWarnings = null
            )
        {
            var image = compilation.EmitToArray(options);//, expectedWarnings : expectedWarnings);
            if (compilation.Options.OutputKind == OutputKind.NetModule) {
                return ModuleMetadata.CreateFromImage(image).GetReference(display : compilation.MakeSourceModuleName());
            }
            else {
                return AssemblyMetadata.CreateFromImage(image).GetReference(aliases : aliases, embedInteropTypes : embedInteropTypes, display : compilation.MakeSourceAssemblySimpleName());
            }
        }

        public static ImmutableArray<byte> EmitToArray(
            this Compilation compilation,
            EmitOptions options = null
            //,CompilationTestData testData = null,
            //DiagnosticDescription[] expectedWarnings = null
            )
        {
            var stream = new MemoryStream();

            var emitResult = compilation.Emit(
                peStream : stream,
                pdbStream : null,
                xmlDocumentationStream : null,
                win32Resources : null,
                manifestResources : null,
                options : options
                //,testData : testData,
                //cancellationToken : default(CancellationToken)
                );

            Debug.Assert(emitResult.Success, "Diagnostics:\r\n" + string.Join("\r\n, ", emitResult.Diagnostics.Select(d => d.ToString())));

            //if (expectedWarnings != null) {
            //    emitResult.Diagnostics.Verify(expectedWarnings);
            //}

            return stream.ToImmutable();
        }

        public static Stream EmitToStream(this Compilation compilation, EmitOptions options = null
            //, DiagnosticDescription[] expectedWarnings = null
            )
        {
            var stream = new MemoryStream();
            var emitResult = compilation.Emit(stream, options : options);
            Debug.Assert(emitResult.Success, "Diagnostics: " + string.Join(", ", emitResult.Diagnostics.Select(d => d.ToString())));

            //if (expectedWarnings != null) {
            //    emitResult.Diagnostics.Verify(expectedWarnings);
            //}

            stream.Position = 0;
            return stream;
        }
    }
}
