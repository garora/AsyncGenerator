﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AsyncGenerator.Core;
using AsyncGenerator.Tests.Github.Issue105.Input;
using NUnit.Framework;

namespace AsyncGenerator.Tests.Github.Issue105
{
	[TestFixture]
	public class Fixture : BaseFixture
	{
		[Test]
		public Task TestAfterTransformation()
		{
			return ReadonlyTest(nameof(TestCase), p => p
				.ConfigureAnalyzation(a => a
					.MethodConversion(symbol => MethodConversion.Smart)
					.SearchAsyncCounterpartsInInheritedTypes(true)
				)
				.ConfigureTransformation(t => t
					.AfterTransformation(result =>
					{
						AssertValidAnnotations(result);
						Assert.AreEqual(1, result.Documents.Count);
						var document = result.Documents[0];

						Assert.NotNull(document.OriginalModified);
						Assert.AreEqual(GetOutputFile(nameof(TestCase)), document.Transformed.ToFullString());
					})
				)
			);
		}

		[Test]
		public Task TestCase2AfterTransformation()
		{
			return ReadonlyTest(nameof(TestCase2), p => p
				.ConfigureAnalyzation(a => a
					.MethodConversion(symbol => symbol.Name == "Test" ?  MethodConversion.Smart: MethodConversion.Unknown)
					.SearchAsyncCounterpartsInInheritedTypes(true)
					.ScanForMissingAsyncMembers(true)
					.CancellationTokens(true)
					.CallForwarding(true)
				)
				.ConfigureTransformation(t => t
					.AfterTransformation(result =>
					{
						AssertValidAnnotations(result);
						Assert.AreEqual(1, result.Documents.Count);
						var document = result.Documents[0];

						Assert.NotNull(document.OriginalModified);
						Assert.AreEqual(GetOutputFile(nameof(TestCase2)), document.Transformed.ToFullString());
					})
				)
			.ConfigureParsing(pp => pp
				.AddPreprocessorSymbolName("TEST")
				.AddPreprocessorSymbolName("ASYNC"))
			);
		}
	}
}
