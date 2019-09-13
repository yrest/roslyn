﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.AddImports;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Simplification;

namespace Microsoft.CodeAnalysis.AddDebuggerDisplay
{
    internal abstract class AbstractAddDebuggerDisplayCodeRefactoringProvider<TTypeDeclarationSyntax, TMethodDeclarationSyntax> : CodeRefactoringProvider
        where TTypeDeclarationSyntax : SyntaxNode
        where TMethodDeclarationSyntax : SyntaxNode
    {
        private static readonly SyntaxAnnotation s_trackingAnnotation = new SyntaxAnnotation();

        public override async Task ComputeRefactoringsAsync(CodeRefactoringContext context)
        {
            var type = await context.TryGetRelevantNodeAsync<TTypeDeclarationSyntax>().ConfigureAwait(false);

            if (type != null)
            {
                if (!DeclaresToStringOverride(type)) return;
            }
            else
            {
                var method = await context.TryGetRelevantNodeAsync<TMethodDeclarationSyntax>().ConfigureAwait(false);
                if (!IsToStringOverride(method)) return;

                type = method.FirstAncestorOrSelf<TTypeDeclarationSyntax>();
            }

            if (HasDebuggerDisplayAttribute(type)) return;

            context.RegisterRefactoring(new MyCodeAction(
                FeaturesResources.Add_DebuggerDisplay,
                cancellationToken => ApplyAsync(context.Document, type, cancellationToken)));
        }

        private async Task<Document> ApplyAsync(Document document, TTypeDeclarationSyntax type, CancellationToken cancellationToken)
        {
            var syntaxRoot = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var compilation = await document.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);

            var generator = SyntaxGenerator.GetGenerator(document);

            var newAttribute = generator
                .Attribute("System.Diagnostics.DebuggerDisplayAttribute", compilation, generator.LiteralExpression("{ToString(),nq}"))
                .WithAdditionalAnnotations(Simplifier.Annotation);

            syntaxRoot = syntaxRoot.ReplaceNode(type, generator.AddAttributes(type, newAttribute).WithAdditionalAnnotations(s_trackingAnnotation));

            syntaxRoot = await EnsureNamespaceImportAsync(
                document,
                generator,
                syntaxRoot,
                contextLocation: syntaxRoot.GetAnnotatedNodes(s_trackingAnnotation).Single(),
                "System.Diagnostics",
                cancellationToken).ConfigureAwait(false);

            return document.WithSyntaxRoot(syntaxRoot);
        }

        private static async Task<SyntaxNode> EnsureNamespaceImportAsync(
            Document document,
            SyntaxGenerator generator,
            SyntaxNode syntaxRoot,
            SyntaxNode contextLocation,
            string namespaceName,
            CancellationToken cancellationToken)
        {
            var compilation = await document.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);

            var importsService = document.Project.LanguageServices.GetRequiredService<IAddImportsService>();
            var newImport = generator.NamespaceImportDeclaration(namespaceName);

            if (importsService.HasExistingImport(compilation, syntaxRoot, contextLocation, newImport))
            {
                return syntaxRoot;
            }

            var optionSet = await document.GetOptionsAsync(cancellationToken).ConfigureAwait(false);
            var placeSystemNamespaceFirst = optionSet.GetOption(GenerationOptions.PlaceSystemNamespaceFirst, document.Project.Language);

            return importsService.AddImport(
                compilation,
                syntaxRoot,
                contextLocation,
                newImport,
                placeSystemNamespaceFirst,
                cancellationToken);
        }

        protected abstract bool HasDebuggerDisplayAttribute(TTypeDeclarationSyntax typeDeclaration);

        protected abstract bool IsToStringOverride(TMethodDeclarationSyntax methodDeclaration);

        protected abstract bool DeclaresToStringOverride(TTypeDeclarationSyntax typeDeclaration);

        protected bool IsDebuggerDisplayAttributeIdentifier(SyntaxToken name)
        {
            return name.ValueText == "DebuggerDisplay" || name.ValueText == "DebuggerDisplayAttribute";
        }

        private sealed class MyCodeAction : CodeAction.DocumentChangeAction
        {
            public MyCodeAction(string title, Func<CancellationToken, Task<Document>> createChangedDocument)
                : base(title, createChangedDocument)
            {
            }
        }
    }
}
