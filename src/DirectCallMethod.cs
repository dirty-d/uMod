﻿extern alias References;

using References::Mono.Cecil;
using References::Mono.Cecil.Cil;
using References::Mono.Cecil.Rocks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using uMod.Plugins;

namespace uMod
{
    public class DirectCallMethod
    {
        public class Node
        {
            public char Char;
            public string Name;
            public Dictionary<char, Node> Edges = new Dictionary<char, Node>();
            public Node Parent;
            public Instruction FirstInstruction;
        }

        private ModuleDefinition module;
        private TypeDefinition type;
        private MethodDefinition method;
        private MethodBody body;
        private Instruction endInstruction;

        private Dictionary<Instruction, Node> jumpToEdgePlaceholderTargets = new Dictionary<Instruction, Node>();
        private List<Instruction> jumpToEndPlaceholders = new List<Instruction>();

        private Dictionary<string, MethodDefinition> hookMethods = new Dictionary<string, MethodDefinition>();

        private MethodReference getLength;
        private MethodReference getChars;
        private MethodReference isNullOrEmpty;
        private MethodReference stringEquals;

        private string hookAttribute = typeof(HookMethodAttribute).FullName;

        public DirectCallMethod(ModuleDefinition module, TypeDefinition type)
        {
            this.module = module;
            this.type = type;

            getLength = module.Import(typeof(string).GetMethod("get_Length", new Type[0]));
            getChars = module.Import(typeof(string).GetMethod("get_Chars", new[] { typeof(int) }));
            isNullOrEmpty = module.Import(typeof(string).GetMethod("IsNullOrEmpty", new[] { typeof(string) }));
            stringEquals = module.Import(typeof(string).GetMethod("Equals", new[] { typeof(string) }));

            // Copy method definition from base class
            AssemblyDefinition baseAssembly = AssemblyDefinition.ReadAssembly(Path.Combine(Interface.uMod.ExtensionDirectory, "uMod.dll"));
            ModuleDefinition baseModule = baseAssembly.MainModule;
            TypeDefinition baseType = module.Import(baseAssembly.MainModule.GetType("uMod.Plugins.CSharpPlugin")).Resolve();
            MethodDefinition baseMethod = module.Import(baseType.Methods.First(method => method.Name == "DirectCallHook")).Resolve();

            // Create method override based on virtual method signature
            method = new MethodDefinition(baseMethod.Name, baseMethod.Attributes, baseModule.Import(baseMethod.ReturnType)) { DeclaringType = type };
            foreach (ParameterDefinition parameter in baseMethod.Parameters)
            {
                ParameterDefinition newParam = new ParameterDefinition(parameter.Name, parameter.Attributes, baseModule.Import(parameter.ParameterType))
                {
                    IsOut = parameter.IsOut,
                    Constant = parameter.Constant,
                    MarshalInfo = parameter.MarshalInfo,
                    IsReturnValue = parameter.IsReturnValue
                };
                foreach (CustomAttribute attribute in parameter.CustomAttributes)
                {
                    newParam.CustomAttributes.Add(new CustomAttribute(module.Import(attribute.Constructor)));
                }

                method.Parameters.Add(newParam);
            }

            foreach (CustomAttribute attribute in baseMethod.CustomAttributes)
            {
                method.CustomAttributes.Add(new CustomAttribute(module.Import(attribute.Constructor)));
            }

            method.ImplAttributes = baseMethod.ImplAttributes;
            method.SemanticsAttributes = baseMethod.SemanticsAttributes;

            // Replace the NewSlot attribute with ReuseSlot
            method.Attributes &= ~MethodAttributes.NewSlot;
            method.Attributes |= MethodAttributes.ReuseSlot;

            // Create new method body
            body = new MethodBody(method);
            body.SimplifyMacros();
            method.Body = body;
            type.Methods.Add(method);

            // Create variables
            body.Variables.Add(new VariableDefinition("name_size", module.TypeSystem.Int32));
            body.Variables.Add(new VariableDefinition("i", module.TypeSystem.Int32));

            // Initialize return value to null
            AddInstruction(OpCodes.Ldarg_2);
            AddInstruction(OpCodes.Ldnull);
            AddInstruction(OpCodes.Stind_Ref);

            // Check for name null or empty
            AddInstruction(OpCodes.Ldarg_1);
            AddInstruction(OpCodes.Call, isNullOrEmpty);
            Instruction empty = AddInstruction(OpCodes.Brfalse, body.Instructions[0]);
            Return(false);

            // Get method name length
            empty.Operand = AddInstruction(OpCodes.Ldarg_1);
            AddInstruction(OpCodes.Callvirt, getLength);
            AddInstruction(OpCodes.Stloc_0);

            // Initialize i counter variable to 0
            AddInstruction(OpCodes.Ldc_I4_0);
            AddInstruction(OpCodes.Stloc_1);

            // Find all hook methods defined by the plugin
            foreach (MethodDefinition m in type.Methods.Where(m => !m.IsStatic && (m.IsPrivate || IsHookMethod(m)) && !m.HasGenericParameters && !m.ReturnType.IsGenericParameter && m.DeclaringType == type && !m.IsSetter && !m.IsGetter))
            {
                //ignore compiler generated
                if (m.Name.Contains("<"))
                {
                    continue;
                }

                string name = m.Name;
                if (m.Parameters.Count > 0)
                {
                    name += $"({string.Join(", ", m.Parameters.Select(x => x.ParameterType.ToString().Replace("/", "+").Replace("<", "[").Replace(">", "]")).ToArray())})";
                }

                if (!hookMethods.ContainsKey(name))
                {
                    hookMethods[name] = m;
                }
            }

            // Build a hook method name trie
            Node rootNode = new Node();
            foreach (string methodName in hookMethods.Keys)
            {
                Node currentNode = rootNode;
                for (int i = 1; i <= methodName.Length; i++)
                {
                    char letter = methodName[i - 1];
                    if (!currentNode.Edges.TryGetValue(letter, out Node nextNode))
                    {
                        nextNode = new Node { Parent = currentNode, Char = letter };
                        currentNode.Edges[letter] = nextNode;
                    }
                    if (i == methodName.Length)
                    {
                        nextNode.Name = methodName;
                    }

                    currentNode = nextNode;
                }
            }

            // Build conditional method call logic from trie nodes
            int n = 1;
            foreach (char edge in rootNode.Edges.Keys)
            {
                BuildNode(rootNode.Edges[edge], n++);
            }

            // No valid method was found
            endInstruction = Return(false);

            foreach (Instruction instruction in jumpToEdgePlaceholderTargets.Keys)
            {
                instruction.Operand = jumpToEdgePlaceholderTargets[instruction].FirstInstruction;
            }

            foreach (Instruction instruction in jumpToEndPlaceholders)
            {
                instruction.Operand = endInstruction;
            }

            body.OptimizeMacros();
        }

        private bool IsHookMethod(MethodDefinition method)
        {
            foreach (CustomAttribute attribute in method.CustomAttributes)
            {
                if (attribute.AttributeType.FullName == hookAttribute)
                {
                    return true;
                }
            }
            return false;
        }

        private void BuildNode(Node node, int edgeNumber)
        {
            // Check the char index lower than length on first edge
            if (edgeNumber == 1)
            {
                node.FirstInstruction = AddInstruction(OpCodes.Ldloc_1);
                AddInstruction(OpCodes.Ldloc_0);
                jumpToEndPlaceholders.Add(AddInstruction(OpCodes.Bge, body.Instructions[0]));
            }

            // Check the char at the current position
            if (edgeNumber == 1)
            {
                AddInstruction(OpCodes.Ldarg_1); //methodName
            }
            else
            {
                node.FirstInstruction = AddInstruction(OpCodes.Ldarg_1);
            }

            AddInstruction(OpCodes.Ldloc_1);                            // i
            AddInstruction(OpCodes.Callvirt, getChars);                 // methodName[i]
            AddInstruction(Ldc_I4_n(node.Char));

            if (node.Parent.Edges.Count > edgeNumber)
            {
                // If char does not match and there are more edges to check
                JumpToEdge(node.Parent.Edges.Values.ElementAt(edgeNumber));
            }
            else
            {
                // If char does not match and there are no more edges to check
                JumpToEnd();
            }

            if (node.Edges.Count == 1 && node.Name == null)
            {
                Node last_edge = node;
                while (last_edge.Edges.Count == 1 && last_edge.Name == null)
                {
                    last_edge = last_edge.Edges.Values.First();
                }

                if (last_edge.Edges.Count == 0 && last_edge.Name != null)
                {
                    // There is only one remaining possible hook on this path
                    AddInstruction(OpCodes.Ldarg_1);
                    AddInstruction(Instruction.Create(OpCodes.Ldstr, last_edge.Name));
                    AddInstruction(OpCodes.Callvirt, stringEquals);
                    // If the full method name does not match the only remaining possible hook, return false
                    jumpToEndPlaceholders.Add(AddInstruction(OpCodes.Brfalse, body.Instructions[0]));

                    // Method has been found
                    CallMethod(hookMethods[last_edge.Name]);
                    Return(true);

                    return;
                }
            }

            // Method continuing with this char exists, increment position
            AddInstruction(OpCodes.Ldloc_1);
            AddInstruction(OpCodes.Ldc_I4_1);
            AddInstruction(OpCodes.Add);
            AddInstruction(OpCodes.Stloc_1);

            if (node.Name != null)
            {
                // Check if we are at the end of the method name
                AddInstruction(OpCodes.Ldloc_1);
                AddInstruction(OpCodes.Ldloc_0);
                // If the method name is longer than the current position
                if (node.Edges.Count > 0)
                {
                    JumpToEdge(node.Edges.Values.First());
                }
                else
                {
                    JumpToEnd();
                }

                // Method has been found
                CallMethod(hookMethods[node.Name]);
                Return(true);
            }

            int n = 1;
            foreach (char edge in node.Edges.Keys)
            {
                BuildNode(node.Edges[edge], n++);
            }
        }

        private void CallMethod(MethodDefinition method)
        {
            Dictionary<ParameterDefinition, VariableDefinition> paramDict = new Dictionary<ParameterDefinition, VariableDefinition>();
            //check for ref/out param
            for (int i = 0; i < method.Parameters.Count; i++)
            {
                ParameterDefinition parameter = method.Parameters[i];
                ByReferenceType param = parameter.ParameterType as ByReferenceType;
                if (param != null)
                {
                    VariableDefinition refParam = AddVariable(module.Import(param.ElementType));
                    AddInstruction(OpCodes.Ldarg_3);    // object[] params
                    AddInstruction(Ldc_I4_n(i));        // param_number
                    AddInstruction(OpCodes.Ldelem_Ref);
                    AddInstruction(OpCodes.Unbox_Any, module.Import(param.ElementType));
                    AddInstruction(OpCodes.Stloc_S, refParam);
                    paramDict[parameter] = refParam;
                }
            }

            if (method.ReturnType.Name != "Void")
            {
                AddInstruction(OpCodes.Ldarg_2); // out object ret
            }

            AddInstruction(OpCodes.Ldarg_0);    // this
            for (int i = 0; i < method.Parameters.Count; i++)
            {
                ParameterDefinition parameter = method.Parameters[i];
                ByReferenceType param = parameter.ParameterType as ByReferenceType;
                if (param != null)
                {
                    AddInstruction(OpCodes.Ldloca, paramDict[parameter]);
                }
                else
                {
                    // TODO: Handle params array?
                    AddInstruction(OpCodes.Ldarg_3);    // object[] params
                    AddInstruction(Ldc_I4_n(i));        // param_number
                    AddInstruction(OpCodes.Ldelem_Ref);
                    AddInstruction(OpCodes.Unbox_Any, module.Import(parameter.ParameterType));
                }
            }
            AddInstruction(OpCodes.Call, module.Import(method));

            //handle ref/out params
            for (int i = 0; i < method.Parameters.Count; i++)
            {
                ParameterDefinition parameter = method.Parameters[i];
                ByReferenceType param = parameter.ParameterType as ByReferenceType;
                if (param != null)
                {
                    AddInstruction(OpCodes.Ldarg_3);    // object[] params
                    AddInstruction(Ldc_I4_n(i));        // param_number
                    AddInstruction(OpCodes.Ldloc_S, paramDict[parameter]);
                    AddInstruction(OpCodes.Box, module.Import(param.ElementType));
                    AddInstruction(OpCodes.Stelem_Ref);
                }
            }

            if (method.ReturnType.Name != "Void")
            {
                if (method.ReturnType.Name != "Object")
                {
                    AddInstruction(OpCodes.Box, module.Import(method.ReturnType));
                }

                AddInstruction(OpCodes.Stind_Ref);
            }
        }

        private Instruction Return(bool value)
        {
            Instruction instruction = AddInstruction(Ldc_I4_n(value ? 1 : 0));
            AddInstruction(OpCodes.Ret);
            return instruction;
        }

        private void JumpToEdge(Node node)
        {
            Instruction instruction = AddInstruction(OpCodes.Bne_Un, body.Instructions[1]);
            jumpToEdgePlaceholderTargets[instruction] = node;
        }

        private void JumpToEnd()
        {
            jumpToEndPlaceholders.Add(AddInstruction(OpCodes.Bne_Un, body.Instructions[0]));
        }

        private Instruction AddInstruction(OpCode opcode)
        {
            return AddInstruction(Instruction.Create(opcode));
        }

        private Instruction AddInstruction(OpCode opcode, Instruction instruction)
        {
            return AddInstruction(Instruction.Create(opcode, instruction));
        }

        private Instruction AddInstruction(OpCode opcode, MethodReference method_reference)
        {
            return AddInstruction(Instruction.Create(opcode, method_reference));
        }

        private Instruction AddInstruction(OpCode opcode, TypeReference type_reference)
        {
            return AddInstruction(Instruction.Create(opcode, type_reference));
        }

        private Instruction AddInstruction(OpCode opcode, int value)
        {
            return AddInstruction(Instruction.Create(opcode, value));
        }

        private Instruction AddInstruction(OpCode opcode, VariableDefinition value)
        {
            return AddInstruction(Instruction.Create(opcode, value));
        }

        private Instruction AddInstruction(Instruction instruction)
        {
            body.Instructions.Add(instruction);
            return instruction;
        }

        public VariableDefinition AddVariable(TypeReference typeRef, string name = "")
        {
            VariableDefinition def = new VariableDefinition(name, typeRef);
            body.Variables.Add(def);
            return def;
        }

        private Instruction Ldc_I4_n(int n)
        {
            if (n == 0)
            {
                return Instruction.Create(OpCodes.Ldc_I4_0);
            }

            if (n == 1)
            {
                return Instruction.Create(OpCodes.Ldc_I4_1);
            }

            if (n == 2)
            {
                return Instruction.Create(OpCodes.Ldc_I4_2);
            }

            if (n == 3)
            {
                return Instruction.Create(OpCodes.Ldc_I4_3);
            }

            if (n == 4)
            {
                return Instruction.Create(OpCodes.Ldc_I4_4);
            }

            if (n == 5)
            {
                return Instruction.Create(OpCodes.Ldc_I4_5);
            }

            if (n == 6)
            {
                return Instruction.Create(OpCodes.Ldc_I4_6);
            }

            if (n == 7)
            {
                return Instruction.Create(OpCodes.Ldc_I4_7);
            }

            if (n == 8)
            {
                return Instruction.Create(OpCodes.Ldc_I4_8);
            }

            return Instruction.Create(OpCodes.Ldc_I4_S, (sbyte)n);
        }
    }
}
