﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ICSharpCode.CodeConverter.Shared;
using ICSharpCode.CodeConverter.Util;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using ArgumentListSyntax = Microsoft.CodeAnalysis.VisualBasic.Syntax.ArgumentListSyntax;
using ArrayRankSpecifierSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.ArrayRankSpecifierSyntax;
using ArrayTypeSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.ArrayTypeSyntax;
using CSharpExtensions = Microsoft.CodeAnalysis.CSharp.CSharpExtensions;
using ExpressionSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionSyntax;
using ISymbolExtensions = ICSharpCode.CodeConverter.Util.ISymbolExtensions;
using SyntaxFactory = Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using SyntaxFacts = Microsoft.CodeAnalysis.CSharp.SyntaxFacts;
using SyntaxKind = Microsoft.CodeAnalysis.VisualBasic.SyntaxKind;
using TypeSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.TypeSyntax;
using VariableDeclaratorSyntax = Microsoft.CodeAnalysis.VisualBasic.Syntax.VariableDeclaratorSyntax;
using VisualBasicExtensions = Microsoft.CodeAnalysis.VisualBasic.VisualBasicExtensions;
using VBSyntax = Microsoft.CodeAnalysis.VisualBasic.Syntax;
using CSSyntax = Microsoft.CodeAnalysis.CSharp.Syntax;
using CSSyntaxKind = Microsoft.CodeAnalysis.CSharp.SyntaxKind;

namespace ICSharpCode.CodeConverter.CSharp
{
    internal class VbSyntaxNodeExtensions
    {
        public static ExpressionSyntax ParenthesizeIfPrecedenceCouldChange(Microsoft.CodeAnalysis.VisualBasic.VisualBasicSyntaxNode node, ExpressionSyntax expression)
        {
            return PrecedenceCouldChange(node) ? SyntaxFactory.ParenthesizedExpression(expression) : expression;
        }

        public static bool PrecedenceCouldChange(Microsoft.CodeAnalysis.VisualBasic.VisualBasicSyntaxNode node)
        {
            bool parentIsSameBinaryKind = node is VBSyntax.BinaryExpressionSyntax && node.Parent is VBSyntax.BinaryExpressionSyntax parent && parent.Kind() == node.Kind();
            bool parentIsReturn = node.Parent is VBSyntax.ReturnStatementSyntax;
            bool parentIsLambda = node.Parent is VBSyntax.LambdaExpressionSyntax;
            bool parentIsNonArgumentExpression = node.Parent is VBSyntax.ExpressionSyntax && !(node.Parent is VBSyntax.ArgumentSyntax);
            bool parentIsParenthesis = node.Parent is VBSyntax.ParenthesizedExpressionSyntax;

            // Could be a full C# precedence table - this is just a common case
            bool parentIsAndOr = node.Parent.IsKind(Microsoft.CodeAnalysis.VisualBasic.SyntaxKind.AndAlsoExpression, Microsoft.CodeAnalysis.VisualBasic.SyntaxKind.OrElseExpression);
            bool nodeIsRelationalOrEqual = node.IsKind(Microsoft.CodeAnalysis.VisualBasic.SyntaxKind.EqualsExpression, Microsoft.CodeAnalysis.VisualBasic.SyntaxKind.NotEqualsExpression,
                Microsoft.CodeAnalysis.VisualBasic.SyntaxKind.LessThanExpression, Microsoft.CodeAnalysis.VisualBasic.SyntaxKind.LessThanOrEqualExpression,
                Microsoft.CodeAnalysis.VisualBasic.SyntaxKind.GreaterThanExpression, Microsoft.CodeAnalysis.VisualBasic.SyntaxKind.GreaterThanOrEqualExpression);
            bool csharpPrecedenceSame = parentIsAndOr && nodeIsRelationalOrEqual;

            return parentIsNonArgumentExpression && !parentIsSameBinaryKind && !parentIsReturn && !parentIsLambda && !parentIsParenthesis && !csharpPrecedenceSame;
        }
    }

    internal class CommonConversions
    {
        private readonly SemanticModel _semanticModel;
        private readonly VisualBasicSyntaxVisitor<CSharpSyntaxNode> _nodesVisitor;
        public TypeConversionAnalyzer TypeConversionAnalyzer { get; }

        public CommonConversions(SemanticModel semanticModel, VisualBasicSyntaxVisitor<CSharpSyntaxNode> nodesVisitor,
            TypeConversionAnalyzer typeConversionAnalyzer)
        {
            TypeConversionAnalyzer = typeConversionAnalyzer;
            _semanticModel = semanticModel;
            _nodesVisitor = nodesVisitor;
        }

        public Dictionary<string, VariableDeclarationSyntax> SplitVariableDeclarations(
            VariableDeclaratorSyntax declarator, bool preferExplicitType = false)
        {
            var rawType = ConvertDeclaratorType(declarator, preferExplicitType);
            var initializer = ConvertInitializer(declarator);

            var newDecls = new Dictionary<string, VariableDeclarationSyntax>();

            foreach (var name in declarator.Names) {
                var (type, adjustedInitializer) = AdjustFromName(rawType, name, initializer);

                bool isField = declarator.Parent.IsKind(SyntaxKind.FieldDeclaration);
                EqualsValueClauseSyntax equalsValueClauseSyntax;
                if (adjustedInitializer != null) {
                    var vbInitializer = declarator.Initializer?.Value;
                    // Explicit conversions are never needed for AsClause, since the type is inferred from the RHS
                    var convertedInitializer = vbInitializer == null ? adjustedInitializer : TypeConversionAnalyzer.AddExplicitConversion(vbInitializer, adjustedInitializer);
                    equalsValueClauseSyntax = SyntaxFactory.EqualsValueClause(convertedInitializer);
                } else if (isField || _semanticModel.IsDefinitelyAssignedBeforeRead(declarator, name)) {
                    equalsValueClauseSyntax = null;
                } else {
                    // VB initializes variables to their default
                    equalsValueClauseSyntax = SyntaxFactory.EqualsValueClause(SyntaxFactory.DefaultExpression(type));
                }

                var v = SyntaxFactory.VariableDeclarator(ConvertIdentifier(name.Identifier), null, equalsValueClauseSyntax);
                string k = type.ToString();
                if (newDecls.TryGetValue(k, out var decl))
                    newDecls[k] = decl.AddVariables(v);
                else
                    newDecls[k] = SyntaxFactory.VariableDeclaration(type, SyntaxFactory.SingletonSeparatedList(v));
            }

            return newDecls;
        }

        private TypeSyntax ConvertDeclaratorType(VariableDeclaratorSyntax declarator, bool preferExplicitType)
        {
            var vbType = declarator.AsClause?.TypeSwitch(
                (SimpleAsClauseSyntax c) => c.Type,
                (AsNewClauseSyntax c) => c.NewExpression.Type(),
                _ => throw new NotImplementedException($"{_.GetType().FullName} not implemented!"));
            return (TypeSyntax)vbType?.Accept(_nodesVisitor) ?? GetTypeSyntax(declarator, preferExplicitType);
        }

        private TypeSyntax GetTypeSyntax(VariableDeclaratorSyntax declarator, bool preferExplicitType)
        {
            if (!preferExplicitType) return CreateVarTypeName();

            var typeInf = _semanticModel.GetTypeInfo(declarator.Initializer.Value);
            if (typeInf.ConvertedType == null) return CreateVarTypeName();

            return _semanticModel.GetCsTypeSyntax(typeInf.ConvertedType, declarator);
        }

        private static TypeSyntax CreateVarTypeName()
        {
            return SyntaxFactory.ParseTypeName("var");
        }

        private ExpressionSyntax ConvertInitializer(VariableDeclaratorSyntax declarator)
        {
            return (ExpressionSyntax)declarator.AsClause?.TypeSwitch(
                       (SimpleAsClauseSyntax _) => declarator.Initializer?.Value,
                       (AsNewClauseSyntax c) => c.NewExpression
                   )?.Accept(_nodesVisitor) ?? (ExpressionSyntax)declarator.Initializer?.Value.Accept(_nodesVisitor);
        }

        private (TypeSyntax, ExpressionSyntax) AdjustFromName(TypeSyntax rawType,
            ModifiedIdentifierSyntax name, ExpressionSyntax initializer)
        {
            var type = rawType;
            if (!SyntaxTokenExtensions.IsKind(name.Nullable, SyntaxKind.None))
            {
                if (type is ArrayTypeSyntax)
                {
                    type = ((ArrayTypeSyntax) type).WithElementType(
                        SyntaxFactory.NullableType(((ArrayTypeSyntax) type).ElementType));
                    initializer = null;
                }
                else
                    type = SyntaxFactory.NullableType(type);
            }

            var rankSpecifiers = ConvertArrayRankSpecifierSyntaxes(name.ArrayRankSpecifiers, name.ArrayBounds, false);
            if (rankSpecifiers.Count > 0)
            {
                var rankSpecifiersWithSizes = ConvertArrayRankSpecifierSyntaxes(name.ArrayRankSpecifiers,
                    name.ArrayBounds);
                if (!rankSpecifiersWithSizes.SelectMany(ars => ars.Sizes).OfType<OmittedArraySizeExpressionSyntax>().Any())
                {
                    initializer =
                        SyntaxFactory.ArrayCreationExpression(
                            SyntaxFactory.ArrayType(type, rankSpecifiersWithSizes));
                }

                type = SyntaxFactory.ArrayType(type, rankSpecifiers);
            }

            return (type, initializer);
        }

        public ExpressionSyntax Literal(object o, string textForUser = null) => GetLiteralExpression(o, textForUser);

        internal ExpressionSyntax GetLiteralExpression(object value, string textForUser = null)
        {
            if (value is string valueTextForCompiler) {
                textForUser = GetQuotedStringTextForUser(textForUser, valueTextForCompiler);
                return SyntaxFactory.LiteralExpression(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StringLiteralExpression,
                    SyntaxFactory.Literal(textForUser, valueTextForCompiler));
            }

            if (value == null)
                return SyntaxFactory.LiteralExpression(Microsoft.CodeAnalysis.CSharp.SyntaxKind.NullLiteralExpression);
            if (value is bool)
                return SyntaxFactory.LiteralExpression((bool)value ? Microsoft.CodeAnalysis.CSharp.SyntaxKind.TrueLiteralExpression : Microsoft.CodeAnalysis.CSharp.SyntaxKind.FalseLiteralExpression);

            textForUser = textForUser != null ? ConvertNumericLiteralValueText(textForUser, value) : value.ToString();

            if (value is byte)
                return SyntaxFactory.LiteralExpression(Microsoft.CodeAnalysis.CSharp.SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(textForUser, (byte)value));
            if (value is sbyte)
                return SyntaxFactory.LiteralExpression(Microsoft.CodeAnalysis.CSharp.SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(textForUser, (sbyte)value));
            if (value is short)
                return SyntaxFactory.LiteralExpression(Microsoft.CodeAnalysis.CSharp.SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(textForUser, (short)value));
            if (value is ushort)
                return SyntaxFactory.LiteralExpression(Microsoft.CodeAnalysis.CSharp.SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(textForUser, (ushort)value));
            if (value is int)
                return SyntaxFactory.LiteralExpression(Microsoft.CodeAnalysis.CSharp.SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(textForUser, (int)value));
            if (value is uint)
                return SyntaxFactory.LiteralExpression(Microsoft.CodeAnalysis.CSharp.SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(textForUser, (uint)value));
            if (value is long)
                return SyntaxFactory.LiteralExpression(Microsoft.CodeAnalysis.CSharp.SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(textForUser, (long)value));
            if (value is ulong)
                return SyntaxFactory.LiteralExpression(Microsoft.CodeAnalysis.CSharp.SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(textForUser, (ulong)value));

            if (value is float)
                return SyntaxFactory.LiteralExpression(Microsoft.CodeAnalysis.CSharp.SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(textForUser, (float)value));
            if (value is double) {
                // Important to use value text, otherwise "10.0" gets coerced to and integer literal of 10 which can change semantics
                return SyntaxFactory.LiteralExpression(Microsoft.CodeAnalysis.CSharp.SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(textForUser, (double)value));
            }
            if (value is decimal) {
                return SyntaxFactory.LiteralExpression(Microsoft.CodeAnalysis.CSharp.SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(textForUser, (decimal)value));
            }

            if (value is char)
                return SyntaxFactory.LiteralExpression(Microsoft.CodeAnalysis.CSharp.SyntaxKind.CharacterLiteralExpression, SyntaxFactory.Literal((char)value));

            if (value is DateTime dt) {
                var valueToOutput = dt.Date.Equals(dt) ? dt.ToString("yyyy-MM-dd") : dt.ToString("yyyy-MM-dd HH:mm:ss");
                return SyntaxFactory.ParseExpression("DateTime.Parse(\"" + valueToOutput + "\")");
            }


            throw new ArgumentOutOfRangeException(nameof(value), value, null);
        }

        internal string GetQuotedStringTextForUser(string textForUser, string valueTextForCompiler)
        {
            var sourceUnquotedTextForUser = Unquote(textForUser);
            var worthBeingAVerbatimString = IsWorthBeingAVerbatimString(valueTextForCompiler);
            var destQuotedTextForUser =
                $"\"{EscapeQuotes(sourceUnquotedTextForUser, valueTextForCompiler, worthBeingAVerbatimString)}\"";
            
            return worthBeingAVerbatimString ? "@" + destQuotedTextForUser : destQuotedTextForUser;

        }

        internal string EscapeQuotes(string unquotedTextForUser, string valueTextForCompiler, bool isVerbatimString)
        {
            if (isVerbatimString) {
                return valueTextForCompiler.Replace("\"", "\"\"");
            } else {
                return unquotedTextForUser.Replace("\"\"", "\\\"");
            }
        }

        private static string Unquote(string quotedText)
        {
            int firstQuoteIndex = quotedText.IndexOf("\"");
            int lastQuoteIndex = quotedText.LastIndexOf("\"");
            var unquoted = quotedText.Substring(firstQuoteIndex + 1, lastQuoteIndex - firstQuoteIndex - 1);
            return unquoted;
        }

        public bool IsWorthBeingAVerbatimString(string s1)
        {
            return s1.IndexOfAny(new[] {'\r', '\n', '\\'}) > -1;
        }

        /// <summary>
        ///  https://docs.microsoft.com/en-us/dotnet/visual-basic/programming-guide/language-features/data-types/type-characters
        //   https://stackoverflow.com/a/166762/1128762
        /// </summary>
        private string ConvertNumericLiteralValueText(string textForUser, object value)
        {
            var replacements = new Dictionary<string, string> {
                {"C", ""},
                {"I", ""},
                {"%", ""},
                {"UI", "U"},
                {"S", ""},
                {"US", ""},
                {"UL", "UL"},
                {"D", "M"},
                {"@", "M"},
                {"R", "D"},
                {"#", "D"},
                {"F", "F"}, // Normalizes casing
                {"!", "F"},
                {"L", "L"}, // Normalizes casing
                {"&", "L"},
            };
            // Be careful not to replace only the "S" in "US" for example
            var longestMatchingReplacement = replacements.Where(t => textForUser.EndsWith(t.Key, StringComparison.OrdinalIgnoreCase))
                .GroupBy(t => t.Key.Length).OrderByDescending(g => g.Key).FirstOrDefault()?.SingleOrDefault();

            if (longestMatchingReplacement != null) {
                textForUser = textForUser.ReplaceEnd(longestMatchingReplacement.Value);
            }

            if (textForUser.Length <= 2 || !textForUser.StartsWith("&")) return textForUser;

            if (textForUser.StartsWith("&H", StringComparison.OrdinalIgnoreCase))
            {
                return "0x" + textForUser.Substring(2).Replace("M", "D"); // Undo any accidental replacements that assumed this was a decimal
            }

            if (textForUser.StartsWith("&B", StringComparison.OrdinalIgnoreCase))
            {
                return "0b" + textForUser.Substring(2);
            }

            // Octal or something unknown that can't be represented with C# literals
            return value.ToString();
        }

        public SyntaxToken ConvertIdentifier(SyntaxToken id, bool isAttribute = false)
        {
            string text = id.ValueText;
            
            if (id.SyntaxTree == _semanticModel.SyntaxTree) {
                var symbol = _semanticModel.GetSymbolInfo(id.Parent).Symbol;
                if (symbol != null && !String.IsNullOrWhiteSpace(symbol.Name)) {
                    if (text.Equals(symbol.Name, StringComparison.OrdinalIgnoreCase)) {
                        text = symbol.Name;
                    }

                    if (symbol.IsConstructor() && isAttribute) {
                        text = symbol.ContainingType.Name;
                        if (text.EndsWith("Attribute", StringComparison.OrdinalIgnoreCase))
                            text = text.Remove(text.Length - "Attribute".Length);
                    } else if (symbol.IsKind(SymbolKind.Parameter) && symbol.ContainingSymbol.IsAccessorPropertySet() && ((symbol.IsImplicitlyDeclared && symbol.Name == "Value") || symbol.ContainingSymbol.GetParameters().FirstOrDefault(x => !x.IsImplicitlyDeclared) == symbol)) {
                        // The case above is basically that if the symbol is a parameter, and the corresponding definition is a property set definition 
                        // AND the first explicitly declared parameter is this symbol, we need to replace it with value.
                        text = "value";
                    } else if (text.StartsWith("_", StringComparison.OrdinalIgnoreCase) && symbol is IFieldSymbol propertyFieldSymbol && propertyFieldSymbol.AssociatedSymbol?.IsKind(SymbolKind.Property) == true) {
                        text = propertyFieldSymbol.AssociatedSymbol.Name;
                    } else if (text.EndsWith("Event", StringComparison.OrdinalIgnoreCase) && symbol is IFieldSymbol eventFieldSymbol && eventFieldSymbol.AssociatedSymbol?.IsKind(SymbolKind.Event) == true) {
                        text = eventFieldSymbol.AssociatedSymbol.Name;
                    }
                }
            }

            return CsEscapedIdentifier(text);
        }

        private static SyntaxToken CsEscapedIdentifier(string text)
        {
            if (SyntaxFacts.GetKeywordKind(text) != Microsoft.CodeAnalysis.CSharp.SyntaxKind.None) text = "@" + text;
            return SyntaxFactory.Identifier(text);
        }

        public SyntaxTokenList ConvertModifiers(SyntaxNode node, IEnumerable<SyntaxToken> modifiers,
            TokenContext context = TokenContext.Global, bool isVariableOrConst = false, bool isConstructor = false)
        {
            ISymbol declaredSymbol = _semanticModel.GetDeclaredSymbol(node);
            var declaredAccessibility = declaredSymbol.DeclaredAccessibility;

            var contextsWithIdenticalDefaults = new[] { TokenContext.Global, TokenContext.Local, TokenContext.InterfaceOrModule, TokenContext.MemberInInterface };
            bool isPartial = declaredSymbol.IsPartialClassDefinition() || declaredSymbol.IsPartialMethodDefinition() || declaredSymbol.IsPartialMethodImplementation();
            bool implicitVisibility = contextsWithIdenticalDefaults.Contains(context) || isVariableOrConst || isConstructor;
            if (implicitVisibility && !isPartial) declaredAccessibility = Accessibility.NotApplicable;
            return SyntaxFactory.TokenList(ConvertModifiersCore(declaredAccessibility, modifiers, context).Where(t => CSharpExtensions.Kind(t) != Microsoft.CodeAnalysis.CSharp.SyntaxKind.None));
        }

        private SyntaxToken? ConvertModifier(SyntaxToken m, TokenContext context = TokenContext.Global)
        {
            SyntaxKind vbSyntaxKind = VisualBasicExtensions.Kind(m);
            switch (vbSyntaxKind) {
                case SyntaxKind.DateKeyword:
                    return SyntaxFactory.Identifier("DateTime");
            }
            var token = vbSyntaxKind.ConvertToken(context);
            return token == Microsoft.CodeAnalysis.CSharp.SyntaxKind.None ? null : new SyntaxToken?(SyntaxFactory.Token(token));
        }

        private IEnumerable<SyntaxToken> ConvertModifiersCore(Accessibility declaredAccessibility,
            IEnumerable<SyntaxToken> modifiers, TokenContext context)
        {
            var remainingModifiers = modifiers.ToList();
            if (declaredAccessibility != Accessibility.NotApplicable) {
                remainingModifiers = remainingModifiers.Where(m => !m.IsVbVisibility(false, false)).ToList();
                foreach (var visibilitySyntaxKind in CsSyntaxAccessibilityKindForContext(declaredAccessibility, context)) {
                    yield return SyntaxFactory.Token(visibilitySyntaxKind);
                }
            }

            foreach (var token in remainingModifiers.Where(m => !IgnoreInContext(m, context)).OrderBy(m => SyntaxTokenExtensions.IsKind(m, SyntaxKind.PartialKeyword))) {
                var m = ConvertModifier(token, context);
                if (m.HasValue) yield return m.Value;
            }
            if (context == TokenContext.MemberInModule &&
                    !remainingModifiers.Any(a => VisualBasicExtensions.Kind(a) == SyntaxKind.ConstKeyword ))
                yield return SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword);
        }

        private IEnumerable<CSSyntaxKind> CsSyntaxAccessibilityKindForContext(Accessibility declaredAccessibility,
            TokenContext context)
        {
            return CsSyntaxAccessibilityKind(declaredAccessibility);

        }

        private IEnumerable<CSSyntaxKind> CsSyntaxAccessibilityKind(Accessibility declaredAccessibility)
        {
            switch (declaredAccessibility) {
                case Accessibility.Private:
                    return new[] { CSSyntaxKind.PrivateKeyword };
                case Accessibility.Protected:
                    return new[] { CSSyntaxKind.ProtectedKeyword };
                case Accessibility.Internal:
                    return new[] { CSSyntaxKind.InternalKeyword };
                case Accessibility.ProtectedOrInternal:
                    return new[] { CSSyntaxKind.ProtectedKeyword, CSSyntaxKind.InternalKeyword };
                case Accessibility.Public:
                    return new[] { CSSyntaxKind.PublicKeyword };
                case Accessibility.ProtectedAndInternal:
                case Accessibility.NotApplicable:
                default:
                    throw new ArgumentOutOfRangeException(nameof(declaredAccessibility), declaredAccessibility, null);
            }
        }

        private bool IgnoreInContext(SyntaxToken m, TokenContext context)
        {
            switch (VisualBasicExtensions.Kind(m)) {
                case SyntaxKind.OptionalKeyword:
                case SyntaxKind.ByValKeyword:
                case SyntaxKind.IteratorKeyword:
                case SyntaxKind.DimKeyword:
                    return true;
                default:
                    return false;
            }
        }

        public bool IsConversionOperator(SyntaxToken token)
        {
            bool isConvOp= token.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ExplicitKeyword, Microsoft.CodeAnalysis.CSharp.SyntaxKind.ImplicitKeyword)
                           ||token.IsKind(SyntaxKind.NarrowingKeyword, SyntaxKind.WideningKeyword);
            return isConvOp;
        }

        internal SyntaxList<ArrayRankSpecifierSyntax> ConvertArrayRankSpecifierSyntaxes(
            SyntaxList<VBSyntax.ArrayRankSpecifierSyntax> arrayRankSpecifierSyntaxs,
            ArgumentListSyntax nodeArrayBounds, bool withSizes = true)
        {
            var bounds = SyntaxFactory.List(arrayRankSpecifierSyntaxs.Select(r => (ArrayRankSpecifierSyntax)r.Accept(_nodesVisitor)));

            if (nodeArrayBounds != null) {
                var sizesSpecified = nodeArrayBounds.Arguments.Any(a => !a.IsOmitted);
                var rank = nodeArrayBounds.Arguments.Count;
                if (!sizesSpecified) rank += 1;

                var convertedArrayBounds = withSizes && sizesSpecified ? ConvertArrayBounds(nodeArrayBounds)
                    : Enumerable.Repeat(SyntaxFactory.OmittedArraySizeExpression(), rank);
                var arrayRankSpecifierSyntax = SyntaxFactory.ArrayRankSpecifier(
                    SyntaxFactory.SeparatedList(
                        convertedArrayBounds));
                bounds = bounds.Insert(0, arrayRankSpecifierSyntax);
            }

            return bounds;
        }

        public IEnumerable<ExpressionSyntax> ConvertArrayBounds(ArgumentListSyntax argumentListSyntax)
        {
            return argumentListSyntax.Arguments.Select(a => {
                VBSyntax.ExpressionSyntax upperBoundExpression = a is SimpleArgumentSyntax sas ? sas.Expression
                    : a is RangeArgumentSyntax ras ? ras.UpperBound
                    : throw new ArgumentOutOfRangeException(nameof(a), a, null);

                return IncreaseArrayUpperBoundExpression(upperBoundExpression);
            });
        }

        private ExpressionSyntax IncreaseArrayUpperBoundExpression(VBSyntax.ExpressionSyntax expr)
        {
            var constant = _semanticModel.GetConstantValue(expr);
            if (constant.HasValue && constant.Value is int)
                return SyntaxFactory.LiteralExpression(Microsoft.CodeAnalysis.CSharp.SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal((int)constant.Value + 1));

            return SyntaxFactory.BinaryExpression(
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.SubtractExpression,
                (ExpressionSyntax)expr.Accept(_nodesVisitor), SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PlusToken), SyntaxFactory.LiteralExpression(Microsoft.CodeAnalysis.CSharp.SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(1)));
        }

        public static AttributeArgumentListSyntax CreateAttributeArgumentList(params AttributeArgumentSyntax[] attributeArgumentSyntaxs)
        {
            return SyntaxFactory.AttributeArgumentList(SyntaxFactory.SeparatedList(attributeArgumentSyntaxs));
        }

        public static VariableDeclarationSyntax CreateVariableDeclarationAndAssignment(string variableName,
            ExpressionSyntax initValue, TypeSyntax explicitType = null)
        {
            var variableDeclaratorSyntax = SyntaxFactory.VariableDeclarator(
                SyntaxFactory.Identifier(variableName), null,
                SyntaxFactory.EqualsValueClause(initValue));
            var variableDeclarationSyntax = SyntaxFactory.VariableDeclaration(
                explicitType ?? SyntaxFactory.IdentifierName("var"),
                SyntaxFactory.SingletonSeparatedList(variableDeclaratorSyntax));
            return variableDeclarationSyntax;
        }
    }
}