﻿// Copyright (c) 2012 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace ICSharpCode.Decompiler.ILAst
{
	/// <summary>
	/// Decompiler step for C# 5 async/await.
	/// </summary>
	class AsyncDecompiler
	{
		public static bool IsCompilerGeneratedStateMachine(TypeDef type)
		{
			if (!(type.DeclaringType != null && type.IsCompilerGenerated()))
				return false;
			foreach (var i in type.Interfaces) {
				if (i.Interface.Namespace == "System.Runtime.CompilerServices" && i.Interface.Name == "IAsyncStateMachine")
					return true;
			}
			return false;
		}
		
		enum AsyncMethodType
		{
			Void,
			Task,
			TaskOfT
		}
		
		DecompilerContext context;
		
		// These fields are set by MatchTaskCreationPattern()
		AsyncMethodType methodType;
		int initialState;
		TypeDef stateMachineStruct;
		MethodDef moveNextMethod;
		FieldDef builderField;
		FieldDef stateField;
		Dictionary<FieldDef, ILVariable> fieldToParameterMap = new Dictionary<FieldDef, ILVariable>();
		
		// These fields are set by AnalyzeMoveNext()
		int finalState = -2;
		ILTryCatchBlock mainTryCatch;
		ILLabel setResultAndExitLabel;
		ILLabel exitLabel;
		ILExpression resultExpr;
		
		#region RunStep1() method
		public static void RunStep1(DecompilerContext context, ILBlock method)
		{
			if (!context.Settings.AsyncAwait)
				return; // abort if async decompilation is disabled
			var yrd = new AsyncDecompiler();
			yrd.context = context;
			if (!yrd.MatchTaskCreationPattern(method))
				return;
			#if DEBUG
			if (Debugger.IsAttached) {
				yrd.Run();
			} else {
				#endif
				try {
					yrd.Run();
				} catch (SymbolicAnalysisFailedException) {
					return;
				}
				#if DEBUG
			}
			#endif
			context.CurrentMethodIsAsync = true;
			
			method.Body.Clear();
			method.EntryGoto = null;
			method.Body.AddRange(yrd.newTopLevelBody);
			ILAstOptimizer.RemoveRedundantCode(method);
		}
		
		void Run()
		{
			AnalyzeMoveNext();
			ValidateCatchBlock(mainTryCatch.CatchBlocks[0]);
			AnalyzeStateMachine(mainTryCatch.TryBlock);
			// AnalyzeStateMachine invokes ConvertBody
			MarkGeneratedVariables();
			YieldReturnDecompiler.TranslateFieldsToLocalAccess(newTopLevelBody, fieldToParameterMap);
		}
		#endregion
		
		#region MatchTaskCreationPattern
		bool MatchTaskCreationPattern(ILBlock method)
		{
			if (method.Body.Count < 5)
				return false;
			// Check the second-to-last instruction (the start call) first, as we can get the most information from that
			IMethod startMethod;
			ILExpression loadStartTarget, loadStartArgument;
			// call(AsyncTaskMethodBuilder::Start, ldloca(builder), ldloca(stateMachine))
			if (!method.Body[method.Body.Count - 2].Match(ILCode.Call, out startMethod, out loadStartTarget, out loadStartArgument))
				return false;
			if (startMethod.Name != "Start" || startMethod.DeclaringType == null || startMethod.DeclaringType.Namespace != "System.Runtime.CompilerServices")
				return false;
			switch (startMethod.DeclaringType.Name) {
				case "AsyncTaskMethodBuilder`1":
					methodType = AsyncMethodType.TaskOfT;
					break;
				case "AsyncTaskMethodBuilder":
					methodType = AsyncMethodType.Task;
					break;
				case "AsyncVoidMethodBuilder":
					methodType = AsyncMethodType.Void;
					break;
				default:
					return false;
			}
			ILVariable stateMachineVar, builderVar;
			if (!loadStartTarget.Match(ILCode.Ldloca, out builderVar))
				return false;
			if (!loadStartArgument.Match(ILCode.Ldloca, out stateMachineVar))
				return false;

			stateMachineStruct = stateMachineVar.Type.GetTypeDefOrRef().ResolveWithinSameModule();
			if (stateMachineStruct == null || !stateMachineStruct.IsValueType)
				return false;
			moveNextMethod = stateMachineStruct.Methods.FirstOrDefault(f => f.Name == "MoveNext");
			if (moveNextMethod == null)
				return false;
			
			// Check third-to-last instruction (copy of builder):
			// stloc(builder, ldfld(StateMachine::<>t__builder, ldloca(stateMachine)))
			ILExpression loadBuilderExpr;
			if (!method.Body[method.Body.Count - 3].MatchStloc(builderVar, out loadBuilderExpr))
				return false;
			IField builderFieldRef;
			ILExpression loadStateMachineForBuilderExpr;
			if (!loadBuilderExpr.Match(ILCode.Ldfld, out builderFieldRef, out loadStateMachineForBuilderExpr))
				return false;
			if (!loadStateMachineForBuilderExpr.MatchLdloca(stateMachineVar))
				return false;
			builderField = builderFieldRef.ResolveFieldWithinSameModule();
			if (builderField == null)
				return false;
			
			// Check the last instruction (ret)
			if (methodType == AsyncMethodType.Void) {
				if (!method.Body[method.Body.Count - 1].Match(ILCode.Ret))
					return false;
			} else {
				// ret(call(AsyncTaskMethodBuilder::get_Task, ldflda(StateMachine::<>t__builder, ldloca(stateMachine))))
				ILExpression returnValue;
				if (!method.Body[method.Body.Count - 1].Match(ILCode.Ret, out returnValue))
					return false;
				IMethod getTaskMethod;
				ILExpression builderExpr;
				if (!returnValue.Match(ILCode.Call, out getTaskMethod, out builderExpr))
					return false;
				ILExpression loadStateMachineForBuilderExpr2;
				IField builderField2;
				if (!builderExpr.Match(ILCode.Ldflda, out builderField2, out loadStateMachineForBuilderExpr2))
					return false;
				if (builderField2.ResolveFieldWithinSameModule() != builderField || !loadStateMachineForBuilderExpr2.MatchLdloca(stateMachineVar))
					return false;
			}
			
			// Check the last field assignment - this should be the state field
			ILExpression initialStateExpr;
			if (!MatchStFld(method.Body[method.Body.Count - 4], stateMachineVar, out stateField, out initialStateExpr))
				return false;
			if (!initialStateExpr.Match(ILCode.Ldc_I4, out initialState))
				return false;
			if (initialState != -1)
				return false;
			
			// Check the second-to-last field assignment - this should be the builder field
			FieldDef builderField3;
			ILExpression builderInitialization;
			if (!MatchStFld(method.Body[method.Body.Count - 5], stateMachineVar, out builderField3, out builderInitialization))
				return false;
			IMethod createMethodRef;
			if (builderField3 != builderField || !builderInitialization.Match(ILCode.Call, out createMethodRef))
				return false;
			if (createMethodRef.Name != "Create")
				return false;
			
			for (int i = 0; i < method.Body.Count - 5; i++) {
				FieldDef field;
				ILExpression fieldInit;
				if (!MatchStFld(method.Body[i], stateMachineVar, out field, out fieldInit))
					return false;
				ILVariable v;
				if (!fieldInit.Match(ILCode.Ldloc, out v))
					return false;
				if (!v.IsParameter)
					return false;
				fieldToParameterMap[field] = v;
			}
			
			return true;
		}
		
		static bool MatchStFld(ILNode stfld, ILVariable stateMachineVar, out FieldDef field, out ILExpression expr)
		{
			field = null;
			IField fieldRef;
			ILExpression ldloca;
			if (!stfld.Match(ILCode.Stfld, out fieldRef, out ldloca, out expr))
				return false;
			field = fieldRef.ResolveFieldWithinSameModule();
			return field != null && ldloca.MatchLdloca(stateMachineVar);
		}
		#endregion
		
		#region Analyze MoveNext
		void AnalyzeMoveNext()
		{
			ILBlock ilMethod = CreateILAst(moveNextMethod);
			
			if (ilMethod.Body.Count != 6)
				throw new SymbolicAnalysisFailedException();
			
			mainTryCatch = ilMethod.Body[0] as ILTryCatchBlock;
			if (mainTryCatch == null || mainTryCatch.CatchBlocks.Count != 1)
				throw new SymbolicAnalysisFailedException();
			if (mainTryCatch.FaultBlock != null || mainTryCatch.FinallyBlock != null)
				throw new SymbolicAnalysisFailedException();
			
			setResultAndExitLabel = ilMethod.Body[1] as ILLabel;
			if (setResultAndExitLabel == null)
				throw new SymbolicAnalysisFailedException();
			
			if (!MatchStateAssignment(ilMethod.Body[2], out finalState))
				throw new SymbolicAnalysisFailedException();
			
			// call(AsyncTaskMethodBuilder`1::SetResult, ldflda(StateMachine::<>t__builder, ldloc(this)), ldloc(<>t__result))
			IMethod setResultMethod;
			ILExpression builderExpr;
			if (methodType == AsyncMethodType.TaskOfT) {
				if (!ilMethod.Body[3].Match(ILCode.Call, out setResultMethod, out builderExpr, out resultExpr))
					throw new SymbolicAnalysisFailedException();
			} else {
				if (!ilMethod.Body[3].Match(ILCode.Call, out setResultMethod, out builderExpr))
					throw new SymbolicAnalysisFailedException();
			}
			if (!(setResultMethod.Name == "SetResult" && IsBuilderFieldOnThis(builderExpr)))
				throw new SymbolicAnalysisFailedException();
			
			exitLabel = ilMethod.Body[4] as ILLabel;
			if (exitLabel == null)
				throw new SymbolicAnalysisFailedException();
		}
		
		/// <summary>
		/// Creates ILAst for the specified method, optimized up to before the 'YieldReturn' step.
		/// </summary>
		ILBlock CreateILAst(MethodDef method)
		{
			if (method == null || !method.HasBody)
				throw new SymbolicAnalysisFailedException();
			
			ILBlock ilMethod = new ILBlock();
			ILAstBuilder astBuilder = new ILAstBuilder();
			ilMethod.Body = astBuilder.Build(method, true, context);
			ILAstOptimizer optimizer = new ILAstOptimizer();
			optimizer.Optimize(context, ilMethod, ILAstOptimizationStep.YieldReturn);
			return ilMethod;
		}
		
		void ValidateCatchBlock(ILTryCatchBlock.CatchBlock catchBlock)
		{
			if (catchBlock.ExceptionType == null || catchBlock.ExceptionType.TypeName != "Exception")
				throw new SymbolicAnalysisFailedException();
			if (catchBlock.Body.Count != 3)
				throw new SymbolicAnalysisFailedException();
			int stateID;
			if (!(MatchStateAssignment(catchBlock.Body[0], out stateID) && stateID == finalState))
				throw new SymbolicAnalysisFailedException();
			IMethod setExceptionMethod;
			ILExpression builderExpr, exceptionExpr;
			if (!catchBlock.Body[1].Match(ILCode.Call, out setExceptionMethod, out builderExpr, out exceptionExpr))
				throw new SymbolicAnalysisFailedException();
			if (!(setExceptionMethod.Name == "SetException" && IsBuilderFieldOnThis(builderExpr) && exceptionExpr.MatchLdloc(catchBlock.ExceptionVariable)))
				throw new SymbolicAnalysisFailedException();
			
			ILLabel label;
			if (!(catchBlock.Body[2].Match(ILCode.Leave, out label) && label == exitLabel))
				throw new SymbolicAnalysisFailedException();
		}
		
		bool IsBuilderFieldOnThis(ILExpression builderExpr)
		{
			// ldflda(StateMachine::<>t__builder, ldloc(this))
			IField fieldRef;
			ILExpression target;
			return builderExpr.Match(ILCode.Ldflda, out fieldRef, out target)
				&& fieldRef.ResolveFieldWithinSameModule() == builderField
				&& target.MatchThis();
		}
		
		bool MatchStateAssignment(ILNode stfld, out int stateID)
		{
			// stfld(StateMachine::<>1__state, ldloc(this), ldc.i4(-2))
			stateID = 0;
			IField fieldRef;
			ILExpression target, val;
			if (stfld.Match(ILCode.Stfld, out fieldRef, out target, out val)) {
				return fieldRef.ResolveFieldWithinSameModule() == stateField
					&& target.MatchThis()
					&& val.Match(ILCode.Ldc_I4, out stateID);
			}
			return false;
		}
		#endregion
		
		#region AnalyzeStateMachine
		ILVariable doFinallyBodies;
		List<ILNode> newTopLevelBody;
		
		void AnalyzeStateMachine(ILBlock block)
		{
			var body = block.Body;
			if (body.Count == 0)
				throw new SymbolicAnalysisFailedException();
			if (DetectDoFinallyBodies(body)) {
				body.RemoveAt(0);
				if (body.Count == 0)
					throw new SymbolicAnalysisFailedException();
			}
			StateRangeAnalysis rangeAnalysis = new StateRangeAnalysis(body[0], StateRangeAnalysisMode.AsyncMoveNext, stateField);
			int bodyLength = block.Body.Count;
			int pos = rangeAnalysis.AssignStateRanges(body, bodyLength);
			rangeAnalysis.EnsureLabelAtPos(body, ref pos, ref bodyLength);
			
			var labelStateRangeMapping = rangeAnalysis.CreateLabelRangeMapping(body, pos, bodyLength);
			newTopLevelBody = ConvertBody(body, pos, bodyLength, labelStateRangeMapping);
			newTopLevelBody.Insert(0, MakeGoTo(labelStateRangeMapping, initialState));
			newTopLevelBody.Add(setResultAndExitLabel);
			if (methodType == AsyncMethodType.TaskOfT) {
				newTopLevelBody.Add(new ILExpression(ILCode.Ret, null, resultExpr));
			} else {
				newTopLevelBody.Add(new ILExpression(ILCode.Ret, null));
			}
		}
		
		bool DetectDoFinallyBodies(List<ILNode> body)
		{
			ILVariable v;
			ILExpression initExpr;
			if (!body[0].Match(ILCode.Stloc, out v, out initExpr))
				return false;
			int initialValue;
			if (!(initExpr.Match(ILCode.Ldc_I4, out initialValue) && initialValue == 1))
				return false;
			doFinallyBodies = v;
			return true;
		}
		#endregion
		
		#region ConvertBody
		ILExpression MakeGoTo(LabelRangeMapping mapping, int state)
		{
			foreach (var pair in mapping) {
				if (pair.Value.Contains(state))
					return new ILExpression(ILCode.Br, pair.Key);
			}
			throw new SymbolicAnalysisFailedException();
		}
		
		List<ILNode> ConvertBody(List<ILNode> body, int startPos, int bodyLength, LabelRangeMapping mapping)
		{
			List<ILNode> newBody = new List<ILNode>();
			// Copy all instructions from the old body to newBody.
			for (int pos = startPos; pos < bodyLength; pos++) {
				ILTryCatchBlock tryCatchBlock = body[pos] as ILTryCatchBlock;
				ILExpression expr = body[pos] as ILExpression;
				if (expr != null && expr.Code == ILCode.Leave && expr.Operand == exitLabel) {
					ILVariable awaiterVar;
					FieldDef awaiterField;
					int targetStateID;
					HandleAwait(newBody, out awaiterVar, out awaiterField, out targetStateID);
					MarkAsGeneratedVariable(awaiterVar);
					newBody.Add(new ILExpression(ILCode.Await, null, new ILExpression(ILCode.Ldloca, awaiterVar)));
					newBody.Add(MakeGoTo(mapping, targetStateID));
				} else if (tryCatchBlock != null) {
					ILTryCatchBlock newTryCatchBlock = new ILTryCatchBlock();
					var tryBody = tryCatchBlock.TryBlock.Body;
					if (tryBody.Count == 0)
						throw new SymbolicAnalysisFailedException();
					StateRangeAnalysis rangeAnalysis = new StateRangeAnalysis(tryBody[0], StateRangeAnalysisMode.AsyncMoveNext, stateField);
					int tryBodyLength = tryBody.Count;
					int posInTryBody = rangeAnalysis.AssignStateRanges(tryBody, tryBodyLength);
					rangeAnalysis.EnsureLabelAtPos(tryBody, ref posInTryBody, ref tryBodyLength);
					
					var mappingInTryBlock = rangeAnalysis.CreateLabelRangeMapping(tryBody, posInTryBody, tryBodyLength);
					var newTryBody = ConvertBody(tryBody, posInTryBody, tryBodyLength, mappingInTryBlock);
					newTryBody.Insert(0, MakeGoTo(mappingInTryBlock, initialState));
					
					// If there's a label at the beginning of the state dispatcher, copy that
					if (posInTryBody > 0 && tryBody.FirstOrDefault() is ILLabel)
						newTryBody.Insert(0, tryBody.First());
					
					newTryCatchBlock.TryBlock = new ILBlock(newTryBody);
					newTryCatchBlock.CatchBlocks = new List<ILTryCatchBlock.CatchBlock>(tryCatchBlock.CatchBlocks);
					newTryCatchBlock.FaultBlock = tryCatchBlock.FaultBlock;
					if (tryCatchBlock.FinallyBlock != null)
						newTryCatchBlock.FinallyBlock = new ILBlock(ConvertFinally(tryCatchBlock.FinallyBlock.Body));
					
					newBody.Add(newTryCatchBlock);
				} else {
					newBody.Add(body[pos]);
				}
			}
			return newBody;
		}
		
		List<ILNode> ConvertFinally(List<ILNode> body)
		{
			List<ILNode> newBody = new List<ILNode>(body);
			ILLabel endFinallyLabel;
			ILExpression ceqExpr;
			if (newBody.Count > 0 && newBody[0].Match(ILCode.Brtrue, out endFinallyLabel, out ceqExpr)) {
				ILExpression loadDoFinallyBodies, loadZero;
				object unused;
				if (ceqExpr.Match(ILCode.Ceq, out unused, out loadDoFinallyBodies, out loadZero)) {
					int num;
					if (loadDoFinallyBodies.MatchLdloc(doFinallyBodies) && loadZero.Match(ILCode.Ldc_I4, out num) && num == 0) {
						newBody.RemoveAt(0);
					}
				} else if (ceqExpr.Match(ILCode.LogicNot, out loadDoFinallyBodies)) {
					if (loadDoFinallyBodies.MatchLdloc(doFinallyBodies)) {
						newBody.RemoveAt(0);
					}
				}
			}
			return newBody;
		}
		
		void HandleAwait(List<ILNode> newBody, out ILVariable awaiterVar, out FieldDef awaiterField, out int targetStateID)
		{
			// Handle the instructions prior to the exit out of the method to detect what is being awaited.
			// (analyses the last instructions in newBody and removes the analyzed instructions from newBody)
			
			if (doFinallyBodies != null) {
				// stloc(<>t__doFinallyBodies, ldc.i4(0))
				ILExpression dfbInitExpr;
				if (!newBody.LastOrDefault().MatchStloc(doFinallyBodies, out dfbInitExpr))
					throw new SymbolicAnalysisFailedException();
				int val;
				if (!(dfbInitExpr.Match(ILCode.Ldc_I4, out val) && val == 0))
					throw new SymbolicAnalysisFailedException();
				newBody.RemoveAt(newBody.Count - 1); // remove doFinallyBodies assignment
			}
			
			// call(AsyncTaskMethodBuilder::AwaitUnsafeOnCompleted, ldflda(StateMachine::<>t__builder, ldloc(this)), ldloca(CS$0$0001), ldloc(this))
			ILExpression callAwaitUnsafeOnCompleted = newBody.LastOrDefault() as ILExpression;
			newBody.RemoveAt(newBody.Count - 1); // remove AwaitUnsafeOnCompleted call
			if (callAwaitUnsafeOnCompleted == null || callAwaitUnsafeOnCompleted.Code != ILCode.Call)
				throw new SymbolicAnalysisFailedException();
			string methodName = ((IMethod)callAwaitUnsafeOnCompleted.Operand).Name;
			if (methodName != "AwaitUnsafeOnCompleted" && methodName != "AwaitOnCompleted")
				throw new SymbolicAnalysisFailedException();
			if (callAwaitUnsafeOnCompleted.Arguments.Count != 3)
				throw new SymbolicAnalysisFailedException();
			if (!callAwaitUnsafeOnCompleted.Arguments[1].Match(ILCode.Ldloca, out awaiterVar))
				throw new SymbolicAnalysisFailedException();
			
			// stfld(StateMachine::<>u__$awaiter6, ldloc(this), ldloc(CS$0$0001))
			IField awaiterFieldRef;
			ILExpression loadThis, loadAwaiterVar;
			if (!newBody.LastOrDefault().Match(ILCode.Stfld, out awaiterFieldRef, out loadThis, out loadAwaiterVar))
				throw new SymbolicAnalysisFailedException();
			newBody.RemoveAt(newBody.Count - 1); // remove awaiter field assignment
			awaiterField = awaiterFieldRef.ResolveFieldWithinSameModule();
			if (!(awaiterField != null && loadThis.MatchThis() && loadAwaiterVar.MatchLdloc(awaiterVar)))
				throw new SymbolicAnalysisFailedException();
			
			// stfld(StateMachine::<>1__state, ldloc(this), ldc.i4(0))
			if (!MatchStateAssignment(newBody.LastOrDefault(), out targetStateID))
				throw new SymbolicAnalysisFailedException();
			newBody.RemoveAt(newBody.Count - 1); // remove awaiter field assignment
		}
		#endregion
		
		#region MarkGeneratedVariables
		int smallestGeneratedVariableIndex = int.MaxValue;
		
		void MarkAsGeneratedVariable(ILVariable v)
		{
			if (v.OriginalVariable != null && v.OriginalVariable.Index >= 0) {
				smallestGeneratedVariableIndex = Math.Min(smallestGeneratedVariableIndex, v.OriginalVariable.Index);
			}
		}
		
		void MarkGeneratedVariables()
		{
			var expressions = new ILBlock(newTopLevelBody).GetSelfAndChildrenRecursive<ILExpression>();
			foreach (var v in expressions.Select(e => e.Operand).OfType<ILVariable>()) {
				if (v.OriginalVariable != null && v.OriginalVariable.Index >= smallestGeneratedVariableIndex)
					v.IsGenerated = true;
			}
		}
		#endregion
		
		#region RunStep2() method
		public static void RunStep2(DecompilerContext context, ILBlock method)
		{
			if (context.CurrentMethodIsAsync) {
				Step2(method.Body);
				ILAstOptimizer.RemoveRedundantCode(method);
				// Repeat the inlining/copy propagation optimization because the conversion of field access
				// to local variables can open up additional inlining possibilities.
				ILInlining inlining = new ILInlining(method);
				inlining.InlineAllVariables();
				inlining.CopyPropagation();
			}
		}
		
		static void Step2(List<ILNode> body)
		{
			for (int pos = 0; pos < body.Count; pos++) {
				ILTryCatchBlock tc = body[pos] as ILTryCatchBlock;
				if (tc != null) {
					Step2(tc.TryBlock.Body);
				} else {
					Step2(body, ref pos);
				}
			}
		}
		
		static bool Step2(List<ILNode> body, ref int pos)
		{
			// stloc(CS$0$0001, callvirt(class System.Threading.Tasks.Task`1<bool>::GetAwaiter, awaiterExpr)
			// brtrue(IL_7C, call(valuetype [mscorlib]System.Runtime.CompilerServices.TaskAwaiter`1<bool>::get_IsCompleted, ldloca(CS$0$0001)))
			// await(ldloca(CS$0$0001))
			// ...
			// IL_7C:
			// arg_8B_0 = call(valuetype [mscorlib]System.Runtime.CompilerServices.TaskAwaiter`1<bool>::GetResult, ldloca(CS$0$0001))
			// initobj(valuetype [mscorlib]System.Runtime.CompilerServices.TaskAwaiter`1<bool>, ldloca(CS$0$0001))
			
			ILExpression loadAwaiter;
			ILVariable awaiterVar;
			if (!body[pos].Match(ILCode.Await, out loadAwaiter))
				return false;
			if (!loadAwaiter.Match(ILCode.Ldloca, out awaiterVar))
				return false;
			
			ILVariable stackVar;
			ILExpression stackExpr;
			while (pos >= 1 && body[pos - 1].Match(ILCode.Stloc, out stackVar, out stackExpr))
				pos--;
			
			// stloc(CS$0$0001, callvirt(class System.Threading.Tasks.Task`1<bool>::GetAwaiter, awaiterExpr)
			ILExpression getAwaiterCall;
			if (!(pos >= 2 && body[pos - 2].MatchStloc(awaiterVar, out getAwaiterCall)))
				return false;
			IMethod getAwaiterMethod;
			ILExpression awaitedExpr;
			if (!(getAwaiterCall.Match(ILCode.Call, out getAwaiterMethod, out awaitedExpr) || getAwaiterCall.Match(ILCode.Callvirt, out getAwaiterMethod, out awaitedExpr)))
				return false;
			
			if (awaitedExpr.Code == ILCode.AddressOf) {
				// remove 'AddressOf()' when calling GetAwaiter() on a value type
				awaitedExpr = awaitedExpr.Arguments[0];
			}
			
			// brtrue(IL_7C, call(valuetype [mscorlib]System.Runtime.CompilerServices.TaskAwaiter`1<bool>::get_IsCompleted, ldloca(CS$0$0001)))
			ILLabel label;
			ILExpression getIsCompletedCall;
			if (!(pos >= 1 && body[pos - 1].Match(ILCode.Brtrue, out label, out getIsCompletedCall)))
				return false;
			
			int labelPos = body.IndexOf(label);
			if (labelPos < pos)
				return false;
			for (int i = pos + 1; i < labelPos; i++) {
				// validate that we aren't deleting any unexpected instructions -
				// between the await and the label, there should only be the stack, awaiter and state logic
				ILExpression expr = body[i] as ILExpression;
				if (expr == null)
					return false;
				switch (expr.Code) {
					case ILCode.Stloc:
					case ILCode.Initobj:
					case ILCode.Stfld:
					case ILCode.Await:
						// e.g.
						// stloc(CS$0$0001, ldfld(StateMachine::<>u__$awaitere, ldloc(this)))
						// initobj(valuetype [mscorlib]System.Runtime.CompilerServices.TaskAwaiter`1<bool>, ldloca(CS$0$0002_66))
						// stfld('<AwaitInLoopCondition>d__d'::<>u__$awaitere, ldloc(this), ldloc(CS$0$0002_66))
						// stfld('<AwaitInLoopCondition>d__d'::<>1__state, ldloc(this), ldc.i4(-1))
						break;
					default:
						return false;
				}
			}
			if (labelPos + 1 >= body.Count)
				return false;
			ILExpression resultAssignment = body[labelPos + 1] as ILExpression;
			ILVariable resultVar;
			ILExpression getResultCall;
			bool isResultAssignment = resultAssignment.Match(ILCode.Stloc, out resultVar, out getResultCall);
			if (!isResultAssignment)
				getResultCall = resultAssignment;
			if (!(getResultCall.Operand is IMethod && ((IMethod)getResultCall.Operand).Name == "GetResult"))
				return false;
			
			pos -= 2; // also delete 'stloc', 'brtrue' and 'await'
			body.RemoveRange(pos, labelPos - pos);
			Debug.Assert(body[pos] == label);
			
			pos++;
			if (isResultAssignment) {
				Debug.Assert(body[pos] == resultAssignment);
				resultAssignment.Arguments[0] = new ILExpression(ILCode.Await, null, awaitedExpr);
			} else {
				body[pos] = new ILExpression(ILCode.Await, null, awaitedExpr);
			}
			
			// if the awaiter variable is cleared out in the next instruction, remove that instruction
			if (IsVariableReset(body.ElementAtOrDefault(pos + 1), awaiterVar)) {
				body.RemoveAt(pos + 1);
			}
			
			return true;
		}
		
		static bool IsVariableReset(ILNode expr, ILVariable variable)
		{
			object unused;
			ILExpression ldloca;
			return expr.Match(ILCode.Initobj, out unused, out ldloca) && ldloca.MatchLdloca(variable);
		}
		#endregion
	}
}
