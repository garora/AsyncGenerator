﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using AsyncGenerator.Analyzation;
using AsyncGenerator.Core;
using AsyncGenerator.Core.Analyzation;
using AsyncGenerator.Core.Extensions.Internal;
using AsyncGenerator.Extensions.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace AsyncGenerator.Internal
{
	internal class BodyFunctionDataReference : AbstractFunctionDataReference<FunctionData>, IBodyFunctionReferenceAnalyzation
	{
		public BodyFunctionDataReference(FunctionData data, ReferenceLocation reference, SimpleNameSyntax referenceNameNode,
			IMethodSymbol referenceSymbol, FunctionData referenceFunctionData)
			: base(data, reference, referenceNameNode, referenceSymbol, referenceFunctionData, true)
		{
			if (data.Conversion == MethodConversion.Copy)
			{
				Ignore("Method will be copied");
			}
		}

		private HashSet<IMethodSymbol> _referenceAsyncSymbols;

		public HashSet<IMethodSymbol> ReferenceAsyncSymbols
		{
			get => _referenceAsyncSymbols ?? (_referenceAsyncSymbols = new HashSet<IMethodSymbol>());
			set => _referenceAsyncSymbols = value;
		}

		public override ReferenceConversion Conversion { get; set; }

		public string IgnoredReason { get; private set; }

		public bool? AwaitInvocation { get; internal set; }

		public ExpressionSyntax ConfigureAwaitParameter { get; set; }

		public bool SynchronouslyAwaited { get; set; }

		public ITypeSymbol InvokedFromType { get; set; }

		public bool PassCancellationToken { get; set; }

		public IParameterSymbol CancellationTokenParameter { get; set; }

		public bool UseAsReturnValue { get; internal set; }

		public bool WrapInsideFunction { get; internal set; }

		public bool LastInvocation { get; internal set; }

		public IMethodSymbol AsyncDelegateArgument { get; set; }

		public BodyFunctionDataReference ArgumentOfFunctionInvocation { get; set; }

		/// <summary>
		/// Delegates passed as arguments
		/// </summary>
		public List<DelegateArgumentData> DelegateArguments { get; set; }

		public void AddDelegateArgument(DelegateArgumentData functionArgument)
		{
			DelegateArguments = DelegateArguments ?? new List<DelegateArgumentData>();
			DelegateArguments.Add(functionArgument);
		}

		public void Ignore(string reason)
		{
			if (Conversion != ReferenceConversion.Ignore)
			{
				IgnoredReason = reason;
			}
			Conversion = ReferenceConversion.Ignore;
			PassCancellationToken = false;
			if (DelegateArguments == null)
			{
				return;
			}
			foreach (var functionArgument in DelegateArguments)
			{
				functionArgument.FunctionData?.Ignore("Cascade ignored.");
				functionArgument.FunctionReference?.Ignore("Cascade ignored.");
			}
		}

		public override ReferenceConversion GetConversion()
		{
			if (Conversion != ReferenceConversion.Unknown)
			{
				return Conversion;
			}
			var methodConversion = ReferenceFunctionData?.Conversion ?? MethodConversion.Unknown;
			var conversion = Conversion;
			if (methodConversion.HasFlag(MethodConversion.ToAsync))
			{
				conversion = ReferenceConversion.ToAsync;
			}
			else if (methodConversion == MethodConversion.Ignore || methodConversion == MethodConversion.Copy)
			{
				conversion = ReferenceConversion.Ignore;
			}
			return conversion;
		}

		public void CalculateFunctionArguments()
		{
			foreach (var functionArgument in DelegateArguments)
			{
				var syncType = ReferenceSymbol.Parameters[functionArgument.Index].Type;
				var paramOffset = AsyncCounterpartSymbol.IsExtensionMethod ? 1 : 0;
				// If the async counterpart does have less parameters than sync method e.g. (Parallel.ForEach -> Task.WaitAll)
				// return, as we don't have enough information
				if (functionArgument.Index + paramOffset >= AsyncCounterpartSymbol.Parameters.Length)
				{
					return;
				}
				var asyncParamType = AsyncCounterpartSymbol.Parameters[functionArgument.Index + paramOffset].Type;
				var asyncDelegateFunc = (IMethodSymbol)asyncParamType.GetMembers("Invoke").FirstOrDefault();
				// Can be null for expressions e.g. Expression<System.Func<TSource, System.Boolean>>
				if (asyncDelegateFunc != null)
				{
					functionArgument.FunctionReference?.CalculateWrapInsideFunction(asyncDelegateFunc);
				}

				// If the reference is an internal method we need to ignore the argument, as currently we do not support async parameter conversion
				// Ignore if the parameter type is an expression, as they cannot be async
				if (Data.Symbol.ContainingAssembly.Equals(ReferenceSymbol.ContainingAssembly) || asyncDelegateFunc == null)
				{
					// TODO: support for parameter conversion e.g. Action -> Func<Task>
					if (functionArgument.FunctionData != null && functionArgument.FunctionData.Conversion != MethodConversion.Ignore)
					{
						functionArgument.FunctionData.Ignore($"Argument type {syncType} of internal method {ReferenceSymbol} cannot be made async.");
					}
					else if (functionArgument.FunctionReference != null &&
					         functionArgument.FunctionReference.Conversion != ReferenceConversion.Ignore)
					{
						functionArgument.FunctionReference.Ignore($"Argument type {syncType} of internal method {ReferenceSymbol} cannot be made async.");
					}
					continue;
				}

				// When the async delegate return type is a type parameter that supports Task e.g. Assert.That,
				// we shall ignore the current reference if the argument is not async
				if (asyncDelegateFunc.ReturnType is ITypeParameterSymbol)  // TODO: check if supports Task
				{
					if (functionArgument.FunctionReference?.GetConversion() == ReferenceConversion.ToAsync ||
					    functionArgument.FunctionData?.Conversion.HasFlag(MethodConversion.ToAsync) == true)
					{
						continue;
					}
					Ignore($"Argument at index '{functionArgument.Index}' cannot be converted to async. Node: {ReferenceNode}");
					return;
				}

				// If the argument types are the same, we can skip. The argument does not need to be async, in order
				// to convert the reference. This applies only for external refernces as for internal, the async and sync are the same.
				// We need to check the original definition as the async counterpart may not have TypeArguments set.
				// e.g. Expression<TSource,int> and Expression<string,int>
				if (asyncParamType.OriginalDefinition.AreEqual(syncType.OriginalDefinition))
				{
					continue;
				}
				if (
					functionArgument.FunctionReference?.GetConversion() == ReferenceConversion.Ignore ||
					functionArgument.FunctionData?.Conversion.HasFlag(MethodConversion.Ignore) == true
					)
				{
					Ignore($"Argument at index '{functionArgument.Index}' cannot be converted to async. Node: {ReferenceNode}");
					return;
				}
			}
		}

		private void CalculateWrapInsideFunction(IMethodSymbol asyncDelegateArgument)
		{
			AsyncDelegateArgument = asyncDelegateArgument;
			if (AsyncCounterpartSymbol == null)
			{
				return;
			}
			// Ignore if the return types does not match
			// e.g. Assert.DoesNotThrow(SimpleFile.Read)
			if (ReferenceFunctionData == null && !AsyncCounterpartSymbol.ReturnType.Equals(asyncDelegateArgument.ReturnType))
			{
				Ignore("One of the arguments does not match the with the async delegate parameter");
				return;
			}

			// If the argument is an internal method and it will be generated with an additinal parameter we need to wrap it inside a function
			// e.g. Assert.DoesNotThrow(Read)
			if (ReferenceFunctionData != null)
			{
				// TODO: check return type
				WrapInsideFunction = ReferenceFunctionData is MethodData argRefMethodData &&
				                     (argRefMethodData.CancellationTokenRequired || argRefMethodData.PreserveReturnType);
			}
			// For now we check only if the parameters matches in case the async counterpart has a cancellation token parameter
			else if (asyncDelegateArgument.Parameters.Length < AsyncCounterpartSymbol.Parameters.Length)
			{
				WrapInsideFunction = true;
			}
		}

		public bool? CanSkipCancellationTokenArgument()
		{
			if (ReferenceFunctionData != null)
			{
				var refMethodData = ReferenceFunctionData.GetMethodOrAccessorData();
					return refMethodData.MethodCancellationToken.GetValueOrDefault().HasOptionalCancellationToken();
			}

			if (ReferenceAsyncSymbols == null || !ReferenceAsyncSymbols.Any())
			{
				return null;
			}
			foreach (var referenceAsyncSymbol in ReferenceAsyncSymbols)
			{
				if (referenceAsyncSymbol.Parameters.Length == ReferenceSymbol.Parameters.Length)
				{
					return true;
				}
				if (referenceAsyncSymbol.Parameters.Length > ReferenceSymbol.Parameters.Length && referenceAsyncSymbol.Parameters.Last().IsOptional)
				{
					return true;
				}
			}
			return false;
		}

		#region IFunctionReferenceAnalyzationResult

		private IReadOnlyList<IMethodSymbol> _cachedReferenceAsyncSymbols;
		IReadOnlyList<IMethodSymbol> IBodyFunctionReferenceAnalyzationResult.ReferenceAsyncSymbols => 
			_cachedReferenceAsyncSymbols ?? (_cachedReferenceAsyncSymbols = ReferenceAsyncSymbols.ToImmutableArray());

		bool IBodyFunctionReferenceAnalyzationResult.AwaitInvocation => AwaitInvocation.GetValueOrDefault();

		#endregion

		#region IFunctionReferenceAnalyzation

		IFunctionAnalyzationResult IBodyFunctionReferenceAnalyzation.ReferenceFunctionData => ReferenceFunctionData;

		public override string AsyncCounterpartName { get; set; }

		public override IMethodSymbol AsyncCounterpartSymbol { get; set; }

		public override FunctionData AsyncCounterpartFunction { get; set; }

		public override bool IsCref => false;

		public override bool IsNameOf => false;

		#endregion

		#region IBodyFunctionReferenceAnalyzationResult

		IEnumerable<IDelegateArgumentAnalyzationResult> IBodyFunctionReferenceAnalyzationResult.DelegateArguments => DelegateArguments ?? Enumerable.Empty<IDelegateArgumentAnalyzationResult>();

		#endregion
	}
}