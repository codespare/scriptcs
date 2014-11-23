using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Common.Logging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
//using Roslyn.Compilers;
//using Roslyn.Compilers.CSharp;
//using Roslyn.Scripting;
//using Roslyn.Scripting.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using ScriptCs.Contracts;
using System.Text.RegularExpressions;

namespace ScriptCs.Engine.Roslyn
{
    public class RoslynScriptEngine: IScriptEngine
    {
        private readonly IScriptHostFactory _scriptHostFactory;
        private IScriptHost _host;
        private Type _hostType;
        private CSharpCompilation _previousSubmission;
        private List<MetadataReference> _submissionReferences;
        private int _submissionCounter;
        private List<object> _submissionArguments;
        const string _submissionNamePrefix = "InteractiveSubmission";
        const long _submissionCounterCycle = TimeSpan.TicksPerDay * 365;

        public const string SessionKey = "Session";
        private const string InvalidNamespaceError = "error CS0246";

        public RoslynScriptEngine(IScriptHostFactory scriptHostFactory, ILog logger)
        {
            _scriptHostFactory = scriptHostFactory;
            Logger = logger;
            _host = null;
            _hostType = null;
            _previousSubmission = null;
            _submissionReferences = new List<MetadataReference>();
            _submissionCounter = 0;
            _submissionArguments = new List<object>(32);
        }

        protected ILog Logger { get; private set; }

        public string BaseDirectory
        {
            get;
            set;
        }

        public string CacheDirectory { get; set; }

        public string FileName { get; set; }

        private string GetSubmissionName()
        {
            var counter = (DateTime.UtcNow.Ticks % _submissionCounterCycle) / 1000L;
            string assemblyName = _submissionNamePrefix + counter.ToString("000-000-000-000");
            return assemblyName;
        }

        public ScriptResult Execute(string code, string[] scriptArgs, AssemblyReferences references, IEnumerable<string> namespaces, ScriptPackSession scriptPackSession)
        {
            Guard.AgainstNullArgument("scriptPackSession", scriptPackSession);
            Guard.AgainstNullArgument("references", references);

            Logger.Debug("Starting to create execution components");
            Logger.Debug("Creating script host");

            var executionReferences = new AssemblyReferences(references.PathReferences, references.Assemblies);
            executionReferences.PathReferences.UnionWith(scriptPackSession.References);

            SessionState<string[]> sessionState;

            var isFirstExecution = !scriptPackSession.State.ContainsKey(SessionKey);

            Debug.Assert(_submissionCounter > 0 || isFirstExecution);
            Debug.Assert(_submissionArguments.Count > 1 || isFirstExecution);

            if (isFirstExecution) {
                code = code.DefineTrace();
                _host = _scriptHostFactory.CreateScriptHost(new ScriptPackManager(scriptPackSession.Contexts), scriptArgs);
                Logger.Debug("Creating session");

                _hostType = _host.GetType();
                _submissionArguments.Add(_host);

                var allNamespaces = namespaces.Union(scriptPackSession.Namespaces).Distinct();

                executionReferences.Assemblies.Add(typeof(ScriptExecutor).Assembly);
                executionReferences.Assemblies.Add(_hostType.Assembly);

                sessionState = new SessionState<string[]> { References = executionReferences, Session = scriptArgs, Namespaces = new HashSet<string>(allNamespaces) };
                scriptPackSession.State[SessionKey] = sessionState;
            }
            else {
                Logger.Debug("Reusing existing session");
                sessionState = (SessionState<string[]>)scriptPackSession.State[SessionKey];

                if (sessionState.References == null) {
                    sessionState.References = new AssemblyReferences();
                }

                if (sessionState.Namespaces == null) {
                    sessionState.Namespaces = new HashSet<string>();
                }

                var newReferences = executionReferences.Except(sessionState.References);

                foreach (var reference in newReferences.PathReferences) {
                    Logger.DebugFormat("Adding reference to {0}", reference);
                    sessionState.References.PathReferences.Add(reference);
                }

                foreach (var assembly in newReferences.Assemblies) {
                    Logger.DebugFormat("Adding reference to {0}", assembly.FullName);
                    sessionState.References.Assemblies.Add(assembly);
                }

                var newNamespaces = namespaces.Except(sessionState.Namespaces);

                foreach (var @namespace in newNamespaces) {
                    Logger.DebugFormat("Importing namespace {0}", @namespace);
                    sessionState.Namespaces.Add(@namespace);
                }

                sessionState.Session = scriptArgs;
            }

            Logger.Debug("Starting execution");

            var result = Execute(code, sessionState);

            //if (result.InvalidNamespaces.Any())
            //{
            //    var pendingNamespacesField = sessionState.Session.GetType().GetField("pendingNamespaces", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            //    if (pendingNamespacesField != null)
            //    {
            //        var pendingNamespacesValue = (ReadOnlyArray<string>)pendingNamespacesField.GetValue(sessionState.Session);
            //        //no need to check this for null as ReadOnlyArray is a value type

            //        if (pendingNamespacesValue.Any())
            //        {
            //            var fixedNamespaces = pendingNamespacesValue.ToList();

            //            foreach (var @namespace in result.InvalidNamespaces)
            //            {
            //                sessionState.Namespaces.Remove(@namespace);
            //                fixedNamespaces.Remove(@namespace);
            //            }
            //            pendingNamespacesField.SetValue(sessionState.Session, ReadOnlyArray<string>.CreateFrom<string>(fixedNamespaces));
            //        }
            //    }
            //}

            Logger.Debug("Finished execution");
            return result;
        }

        protected CSharpCompilation CompileSubmission(string code, SessionState<string[]> session)
        {
            var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp6, DocumentationMode.None, SourceCodeKind.Interactive);
            var syntaxTree = CSharpSyntaxTree.ParseText(code, parseOptions);
            if (!SyntaxFactory.IsCompleteSubmission(syntaxTree)) {
                return null;
            }

            var assemblyName = GetSubmissionName();
            var references = GetMetadataReferences(session.References);
            var compilationOptions = new CSharpCompilationOptions(
                outputKind : OutputKind.DynamicallyLinkedLibrary,
                usings : session.Namespaces,
                optimizationLevel : OptimizationLevel.Debug,
                checkOverflow : false,
                allowUnsafe : true,
                generalDiagnosticOption : ReportDiagnostic.Error,
                warningLevel : 0,
                specificDiagnosticOptions : new KeyValuePair<string, ReportDiagnostic>[] { },
                concurrentBuild : false
                );

            var submission = CSharpCompilation.CreateSubmission(
                assemblyName,
                syntaxTree,
                references,
                compilationOptions,
                previousSubmission : _previousSubmission,
                //returnType : null,
                hostObjectType : _hostType
                );

            return submission;
        }

        protected virtual ScriptResult Execute(string code, SessionState<string[]> session)
        {
            Guard.AgainstNullArgument("session", session);

            //if (string.IsNullOrWhiteSpace(FileName))
            //{
            //    return ScriptResult.Incomplete;
            //}

            try
            {
                var submission = CompileSubmission(code, session);
                if (submission == null) {
                    return ScriptResult.Incomplete;
                }

                try
                {
                    using (MemoryStream peStream = new MemoryStream()) {
                        var emitOptions = new EmitOptions(
                            runtimeMetadataVersion : "v4.0.30319",
                            tolerateErrors: true
                            );
                        Logger.Debug("Emitting submission " + submission.AssemblyName + " to memory.");
                        var emitResult = submission.Emit(peStream, options: emitOptions);

                        //Trace.WriteLine("Emitting submission " + submission.AssemblyName + " to disk.");
                        //var emitResult = submission.Emit(submission.AssemblyName + ".dll", submission.AssemblyName + ".pdb", submission.AssemblyName + ".xml");

                        if (emitResult.Success || emitResult.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error) == 0) {
                            var peBytes = peStream.ToArray();
                            Assembly assembly = null;
                            _previousSubmission = submission;
                            _submissionCounter++;
                            if (peBytes.Length > 0) // submission such as 'using namespace;' are valid but do not emit anything
	                        {
                                Logger.Debug("Loading emitted assembly " + submission.AssemblyName + " from memory.");
                                assembly = Assembly.Load(peBytes);
                                //Trace.WriteLine("Loading emitted assembly " + submission.AssemblyName + " from disk.");
                                //var assembly = Assembly.LoadFile(Path.Combine(Environment.CurrentDirectory, submission.AssemblyName + ".dll"));

                                //var submissionReference = MetadataReference.CreateFromImage(peBytes);
                                //var submissionReference = MetadataReference.CreateFromFile(submission.AssemblyName + ".dll");
                                var submissionReference = AssemblyMetadata.CreateFromImage(peBytes).GetReference( display : submission.MakeSourceAssemblySimpleName());
                                _submissionReferences.Add(submissionReference);   
                            }
                            if (assembly != null) { Logger.Debug("Looking up Script class."); }
                            var script = assembly?.GetType("Script");
                            if (script != null) { Logger.Debug("Looking up Script class <Factory> method."); }
                            var scriptMethod = script?.GetMethod("<Factory>");
                            if (scriptMethod != null) { Logger.Debug("Building delegate for Script class <Factory> method."); }
                            var scriptCallback = scriptMethod?.CreateDelegate(typeof(ScriptCallback)) as ScriptCallback;

                            object scriptResult = null;
                            if (scriptCallback != null)
	                        {
                                var submissionArguments = new object[_submissionArguments.Count + 1];
                                for (int i = 0; i < _submissionArguments.Count; i++)
			                    {
			                        submissionArguments[i] = _submissionArguments[i];
			                    }

                                Logger.Debug("Invoking delegate for Script class <Factory> method.");
                                scriptResult = scriptCallback(submissionArguments);

                                _submissionArguments.Add(submissionArguments[submissionArguments.Length - 1]);
	                        }

                            return (scriptResult != null) ? new ScriptResult(returnValue : scriptResult) : ScriptResult.Empty;
                        }
                        else {
                            var errors = emitResult
                                .Diagnostics
                                .Where(d => d.Severity == DiagnosticSeverity.Error)
                                .Select(d => new ApplicationException(d.ToString()));
                            foreach (var error in errors)
	                        {
		                        Trace.TraceError(error.ToString());
	                        }
                            throw new AggregateException(errors);
                        }
                    }
                }
                catch (AggregateException ex)
                {
                    Logger.Error(ex.ToString());
                    return new ScriptResult(executionException: ex.InnerException);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex.ToString());
                    return new ScriptResult(executionException: ex);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex.ToString());
                if (ex.Message.StartsWith(InvalidNamespaceError))
                {
                    var offendingNamespace = Regex.Match(ex.Message, @"\'([^']*)\'").Groups[1].Value;
                    return new ScriptResult(compilationException: ex, invalidNamespaces: new string[1] {offendingNamespace});
                }
           
                return new ScriptResult(compilationException: ex);
            }
        }

        delegate object ScriptCallback(object[] o);

        private IEnumerable<MetadataReference> GetMetadataReferences(AssemblyReferences assemblyReferences)
        {
            var netFrameworkDirectoryPath = RuntimeEnvironment.GetRuntimeDirectory();
            var gacPaths = assemblyReferences.PathReferences
                .Concat(new[] { "mscorlib", "Microsoft.CSharp" })
                .Select(p => Path.Combine(netFrameworkDirectoryPath, p.EndsWith(".dll") ? p : p + ".dll"))
                .Where(p => File.Exists(p))
                .Distinct();

            var gacAssemblies = gacPaths.Select(p => Assembly.LoadFile(p));

            return assemblyReferences.Assemblies.Select(a => MetadataReference.CreateFromAssembly(a))
                .Concat(gacAssemblies.Select(a => MetadataReference.CreateFromAssembly(a)))
                .Concat(_submissionReferences);
        }

        private void AddReferencedCompilations(IEnumerable<Compilation> referencedCompilations, List<MetadataReference> references)
        {
            if (referencedCompilations != null) {
                foreach (var referencedCompilation in referencedCompilations) {
                    references.Add(referencedCompilation.EmitToImageReference());
                }
            }
        }
    }
}