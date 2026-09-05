using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Microkernel.RpcGenerator.Generators
{
    [Generator]
    public class RpcSourceGenerator : ISourceGenerator
    {
        public void Initialize(GeneratorInitializationContext context)
        {
            context.RegisterForSyntaxNotifications(() => new RpcSyntaxReceiver());
        }

        public void Execute(GeneratorExecutionContext context)
        {
            var receiver = context.SyntaxReceiver as RpcSyntaxReceiver;
            var processedSymbols = new HashSet<string>();
            var targetInterfaces = new List<INamedTypeSymbol>();

            // 1. Gather candidate interfaces from current compilation
            if (receiver != null)
            {
                foreach (var ifaceDecl in receiver.CandidateInterfaces)
                {
                    var model = context.Compilation.GetSemanticModel(ifaceDecl.SyntaxTree);
                    if (model.GetDeclaredSymbol(ifaceDecl) is INamedTypeSymbol symbol && symbol.TypeKind == TypeKind.Interface)
                    {
                        if (processedSymbols.Add(symbol.ToDisplayString()))
                        {
                            targetInterfaces.Add(symbol);
                        }
                    }
                }
            }

            // 2. Gather candidate interfaces from referenced assemblies (e.g. Microkernel.Abstractions)
            if (context.Compilation.SourceModule != null)
            {
                foreach (var refAsm in context.Compilation.SourceModule.ReferencedAssemblySymbols)
                {
                    ScanNamespace(refAsm.GlobalNamespace, targetInterfaces, processedSymbols);
                }
            }

            foreach (var symbol in targetInterfaces)
            {
                bool isRpcContract = false;
                foreach (var attr in symbol.GetAttributes())
                {
                    if (attr.AttributeClass != null &&
                        (attr.AttributeClass.Name == "RpcContractAttribute" || attr.AttributeClass.Name == "RpcContract"))
                    {
                        isRpcContract = true;
                        break;
                    }
                }

                if (!isRpcContract)
                    continue;

                var methods = new List<(IMethodSymbol Method, uint MethodId)>();
                foreach (var member in symbol.GetMembers())
                {
                    if (member is IMethodSymbol method)
                    {
                        foreach (var attr in method.GetAttributes())
                        {
                            if (attr.AttributeClass != null &&
                                (attr.AttributeClass.Name == "RpcMethodAttribute" || attr.AttributeClass.Name == "RpcMethod"))
                            {
                                uint methodId = 0;
                                if (attr.ConstructorArguments.Length > 0 &&
                                    attr.ConstructorArguments[0].Value is uint idVal)
                                {
                                    methodId = idVal;
                                }
                                else if (attr.ConstructorArguments.Length > 0 &&
                                         attr.ConstructorArguments[0].Value is int intVal)
                                {
                                    methodId = (uint)intVal;
                                }

                                methods.Add((method, methodId));
                                break;
                            }
                        }
                    }
                }

                string clientSource = ClientProxyGenerator.Generate(symbol, methods);
                string dispatcherSource = ServerDispatcherGenerator.Generate(symbol, methods);

                context.AddSource(symbol.Name + "Client.g.cs", SourceText.From(clientSource, Encoding.UTF8));
                context.AddSource(symbol.Name + "Dispatcher.g.cs", SourceText.From(dispatcherSource, Encoding.UTF8));
            }
        }

        private static void ScanNamespace(INamespaceSymbol ns, List<INamedTypeSymbol> list, HashSet<string> processed)
        {
            if (ns == null) return;
            foreach (var type in ns.GetTypeMembers())
            {
                if (type.TypeKind == TypeKind.Interface)
                {
                    if (processed.Add(type.ToDisplayString()))
                    {
                        list.Add(type);
                    }
                }
            }
            foreach (var subNs in ns.GetNamespaceMembers())
            {
                ScanNamespace(subNs, list, processed);
            }
        }
    }

    internal class RpcSyntaxReceiver : ISyntaxReceiver
    {
        public List<InterfaceDeclarationSyntax> CandidateInterfaces { get; } = new List<InterfaceDeclarationSyntax>();

        public void OnVisitSyntaxNode(SyntaxNode syntaxNode)
        {
            if (syntaxNode is InterfaceDeclarationSyntax iface && iface.AttributeLists.Count > 0)
            {
                CandidateInterfaces.Add(iface);
            }
        }
    }
}
