﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MiniJavaCompiler.Support.SymbolTable;
using MiniJavaCompiler.Support.AbstractSyntaxTree;
using System.Reflection;
using System.Reflection.Emit;
using MiniJavaCompiler.Support.SymbolTable.Symbols;
using MiniJavaCompiler.Support.SymbolTable.Types;
using MiniJavaCompiler.Support;

namespace MiniJavaCompiler.BackEnd
{
    public partial class CodeGenerator
    {
        private class InstructionGenerator : INodeVisitor
        {
            private CodeGenerator _parent;
            private TypeBuilder _currentType;
            private MethodBuilder _currentMethod;

            private static Dictionary<MiniJavaInfo.Operator, OpCode> operators =
                new Dictionary<MiniJavaInfo.Operator, OpCode>()
            {
                { MiniJavaInfo.Operator.Add, OpCodes.Add },
                { MiniJavaInfo.Operator.Sub, OpCodes.Sub },
                { MiniJavaInfo.Operator.Div, OpCodes.Div },
                { MiniJavaInfo.Operator.Mul, OpCodes.Mul },
                { MiniJavaInfo.Operator.Lt, OpCodes.Clt },
                { MiniJavaInfo.Operator.Gt, OpCodes.Cgt },
                { MiniJavaInfo.Operator.And, OpCodes.And },
                { MiniJavaInfo.Operator.Or, OpCodes.Or },
                { MiniJavaInfo.Operator.Eq, OpCodes.Ceq },
                { MiniJavaInfo.Operator.Mod, OpCodes.Rem }
            };

            public InstructionGenerator(CodeGenerator parent)
            {
                _parent = parent;
            }

            public void GenerateInstructions()
            {
                _parent._astRoot.Accept(this);
            }

            public void Visit(Program node) { }

            public void Visit(ClassDeclaration node)
            {
                TypeBuilder thisType = _parent._types[node.Name];
                _currentType = thisType;
            }

            public void Visit(VariableDeclaration node) { }

            public void Visit(MethodDeclaration node)
            {
                var sym = _parent._symbolTable.Scopes[node].ResolveMethod(node.Name);
                _currentMethod = _parent._methods[sym];
            }

            public void Visit(PrintStatement node)
            {
                MethodInfo printMethod = typeof(System.Console).GetMethod(
                    "WriteLine", new Type[] { typeof(Int32) });
                _currentMethod.GetILGenerator().Emit(OpCodes.Call, printMethod);
            }

            public void Visit(ReturnStatement node)
            {
                _currentMethod.GetILGenerator().Emit(OpCodes.Ret);
            }

            public void Visit(BlockStatement node)
            {
                var il = _currentMethod.GetILGenerator();
                if (node.Label.HasValue)
                {
                    il.MarkLabel(node.Label.Value);
                }
            }

            public void Visit(AssertStatement node)
            {   // TODO: test assertions
                MethodInfo assertMethod = typeof(System.Diagnostics.Debug).GetMethod(
                    "Assert", new Type[] { typeof(bool) });
                _currentMethod.GetILGenerator().Emit(OpCodes.Call, assertMethod);
            }

            public void Visit(AssignmentStatement node)
            {   // The left hand side of an assignment must be either
                // a variable reference or an array indexing expression.
                var il = _currentMethod.GetILGenerator();
                if (node.LeftHandSide is VariableReferenceExpression)
                {
                    var reference = (VariableReferenceExpression)node.LeftHandSide;
                    var variable = _parent._symbolTable.Scopes[reference].ResolveVariable(reference.Name);
                    var decl = (VariableDeclaration)_parent._symbolTable.Declarations[variable];
                    switch (decl.VariableKind)
                    {
                        case VariableDeclaration.Kind.Class:
                            il.Emit(OpCodes.Stfld, _parent._fields[variable]);
                            break;
                        case VariableDeclaration.Kind.Local:
                            il.Emit(OpCodes.Stloc, decl.LocalIndex);
                            break;
                        case VariableDeclaration.Kind.Formal:
                            il.Emit(OpCodes.Starg, GetParameterIndex(decl, _currentMethod));
                            break;
                    }
                }
                else
                {   // The address to store to should be on the top of the stack just
                    // under the object being stored.
                    var rhsType = node.RightHandSide.Type;
                    if (MiniJavaInfo.IsBuiltInType(rhsType.Name))
                    {
                        il.Emit(OpCodes.Stelem_I4);
                    }
                    else
                    {
                        il.Emit(OpCodes.Stelem_Ref);
                    }
                }
            }

            public void VisitAfterCondition(IfStatement node)
            {
                var il = _currentMethod.GetILGenerator();
                node.ExitLabel = il.DefineLabel();
                if (node.ElseBranch != null)
                {
                    node.ElseBranch.Label = il.DefineLabel();
                    il.Emit(OpCodes.Brfalse, node.ElseBranch.Label.Value);
                }
                else
                {
                    il.Emit(OpCodes.Brfalse, node.ExitLabel);
                }
            }

            public void VisitAfterThenBranch(IfStatement node)
            {
                _currentMethod.GetILGenerator().Emit(OpCodes.Br, node.ExitLabel);
            }

            public void Exit(IfStatement node)
            {
                _currentMethod.GetILGenerator().MarkLabel(node.ExitLabel);
            }

            public void Visit(WhileStatement node)
            {
                var il = _currentMethod.GetILGenerator();
                Label test = il.DefineLabel();
                node.ConditionLabel = test;
                node.LoopBody.Label = il.DefineLabel();
                il.Emit(OpCodes.Br, test); // unconditional branch to loop test
            }

            public void VisitAfterBody(WhileStatement node)
            {
                _currentMethod.GetILGenerator().MarkLabel(node.ConditionLabel);
            }

            public void Exit(WhileStatement node)
            {
                _currentMethod.GetILGenerator().Emit(OpCodes.Brtrue, node.LoopBody.Label.Value);
            }

            public void Visit(MethodInvocation node)
            {
                if (node.MethodOwner.Type is ArrayType)
                {
                    _currentMethod.GetILGenerator().Emit(OpCodes.Ldlen);
                }
                else
                {   // TODO: check call parameters
                    var methodScope = _parent._symbolTable.ResolveTypeName(node.MethodOwner.Type.Name).Scope;
                    var calledMethod = _parent._methods[methodScope.ResolveMethod(node.MethodName)];
                    _currentMethod.GetILGenerator().Emit(OpCodes.Call, calledMethod);
                }
            }

            public void Visit(InstanceCreationExpression node)
            {
                Type type = _parent.BuildType(node.CreatedTypeName, false);
                var il = _currentMethod.GetILGenerator();
                if (node.IsArrayCreation)
                {   // arraysize is on top of the stack
                    il.Emit(OpCodes.Newarr, type);
                }
                else
                {
                    il.Emit(OpCodes.Newobj, _parent._constructors[type]);
                }
            }

            public void Visit(UnaryOperatorExpression node)
            {
                _currentMethod.GetILGenerator().Emit(OpCodes.Ldc_I4_0);
                _currentMethod.GetILGenerator().Emit(OpCodes.Ceq);
            }

            public void Visit(BinaryOperatorExpression node)
            {
                _currentMethod.GetILGenerator().Emit(operators[node.Operator]);
            }

            public void Visit(BooleanLiteralExpression node)
            {
                _currentMethod.GetILGenerator().Emit(OpCodes.Ldc_I4, node.Value ? 1 : 0);
            }

            public void Visit(ThisExpression node)
            {
                _currentMethod.GetILGenerator().Emit(OpCodes.Ldarg_0);
            }

            public void Visit(ArrayIndexingExpression node)
            {
                if (node.UsedAsAddress) return; // no need to load anything, index is already on the stack?
                var il = _currentMethod.GetILGenerator();
                if (MiniJavaInfo.IsBuiltInType(node.Type.Name))
                {
                    il.Emit(OpCodes.Ldelem_I4);
                }
                else
                {
                    il.Emit(OpCodes.Ldelem_Ref);
                }
            }


            public void Visit(VariableReferenceExpression node)
            {
                var il = _currentMethod.GetILGenerator();
                var variable = _parent._symbolTable.Scopes[node].ResolveVariable(node.Name);
                var definition = (VariableDeclaration)_parent._symbolTable.Declarations[variable];

                if (node.UsedAsAddress)
                {
                    if (definition.VariableKind == VariableDeclaration.Kind.Class)
                    {   // Load a "this" reference.
                        il.Emit(OpCodes.Ldarg_0);
                    }
                    return;
                }

                switch (definition.VariableKind)
                {
                    case VariableDeclaration.Kind.Class:
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Ldfld, _parent._fields[variable]);
                        break;
                    case VariableDeclaration.Kind.Formal:
                        il.Emit(OpCodes.Ldarg, GetParameterIndex(definition, _currentMethod));
                        break;
                    case VariableDeclaration.Kind.Local:
                        il.Emit(OpCodes.Ldloc, definition.LocalIndex);
                        break;
                }
            }

            public void Visit(IntegerLiteralExpression node)
            {
                _currentMethod.GetILGenerator().Emit(OpCodes.Ldc_I4, node.IntValue);
            }

            public void Exit(ClassDeclaration node)
            {
                _currentType = null;
            }

            public void Exit(MethodDeclaration node)
            {
                // Emit the return statement for a void method.
                if (!(node.MethodBody.Last() is ReturnStatement))
                {
                    _currentMethod.GetILGenerator().Emit(OpCodes.Ret);
                }
                _currentMethod = null;
            }

            public void Exit(BlockStatement node) { }
        }
    }
}
