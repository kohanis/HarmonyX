using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
#if NETCOREAPP
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
#endif
using HarmonyLib;
using NUnit.Framework;
using NUnit.Framework.Constraints;
using NUnit.Framework.Interfaces;
using NUnit.Framework.Internal;
using Logger = HarmonyLib.Tools.Logger;
using Test = NUnit.Framework.Internal.Test;

namespace HarmonyLibTests
{
	public interface ITestIsolationContext
	{
		void AssemblyLoad(string name);

		void ParentCallback<T>(Action<T> callback, T arg);
	}

	public static class TestTools
	{
		public static long GetMethodStart(MethodBase method, out Exception exception)
		{
			try
			{
				exception = null;
				return MonoMod.Core.Platforms.PlatformTriple.Current.GetNativeMethodBody(method).ToInt64();
			}
			catch (Exception e)
			{
				exception = e;
				return 0;
			}
		}

		// Change this from TestContext.Out to TestContext.Error for immediate output to stderr to help diagnose crashes.
		// Note: Must be a property rather than a field, since the specific TestContext streams can change between tests.
		static TextWriter LogWriter => TestContext.Out;

		public static void Log(object obj, int indentLevel = 1, int? indentLevelAfterNewLine = null, bool writeLine = true)
		{
			var indentBeforeNewLine = new string('\t', indentLevel);
			var indentAfterNewLine = new string('\t', indentLevelAfterNewLine ?? indentLevel + 1);
			var text = $"{indentBeforeNewLine}{obj?.ToString().Replace("\n", "\n" + indentAfterNewLine) ?? "null"}";
			if (writeLine)
				LogWriter.WriteLine(text);
			else
				LogWriter.Write(text);
		}

		// Guarantees that assertion failures throw AssertionException, regardless of whether in Assert.Multiple mode.
		public static void AssertImmediate(TestDelegate testDelegate)
		{
			var currentContext = TestExecutionContext.CurrentContext;
			var multipleAssertLevelProp = AccessTools.Property(currentContext.GetType(), "MultipleAssertLevel");
			var origLevel = multipleAssertLevelProp.GetValue(currentContext, null);
			multipleAssertLevelProp.SetValue(currentContext, 0, null);
			try
			{
				testDelegate();
			}
			finally
			{
				multipleAssertLevelProp.SetValue(currentContext, origLevel, null);
			}
		}

		// AssertThat overloads below are a workaround for the inability to capture and expose ConstraintResult
		// (which contains IsSuccess and ActualValue) when using Assert in Assert.Multiple mode.
		// Especially useful when using Assert.That with a Throws constraint and you need to capture any caught exception.
		// Also includes a workaround for Throws constraints reporting failed assertions within the test delegate as an unexpected
		// AssertionException rather than just reporting the assertion failure message itself.

		public static ConstraintResult AssertThat<TActual>(TActual actual, IResolveConstraint expression, string message = null, params object[] args)
		{
			var capture = new CaptureResultConstraint(expression);
			Assert.That(actual, capture, message, args);
			return capture.capturedResult;
		}

		public static ConstraintResult AssertThat<TActual>(TActual actual, IResolveConstraint expression, Func<string> getExceptionMessage)
		{
			var capture = new CaptureResultConstraint(expression);
			Assert.That(actual, capture, getExceptionMessage);
			return capture.capturedResult;
		}

		public static ConstraintResult AssertThat<TActual>(ActualValueDelegate<TActual> del, IResolveConstraint expr, string message = null, params object[] args)
		{
			var capture = new CaptureResultConstraint(expr);
			Assert.That(del, capture, message, args);
			return capture.capturedResult;
		}

		public static ConstraintResult AssertThat<TActual>(ActualValueDelegate<TActual> del, IResolveConstraint expr, Func<string> getExceptionMessage)
		{
			var capture = new CaptureResultConstraint(expr);
			Assert.That(del, capture, getExceptionMessage);
			return capture.capturedResult;
		}

		public static ConstraintResult AssertThat(TestDelegate code, IResolveConstraint constraint, string message = null, params object[] args)
		{
			var capture = new CaptureResultConstraint(constraint);
			Assert.That(code, capture, message, args);
			return capture.capturedResult;
		}

		public static ConstraintResult AssertThat(TestDelegate code, IResolveConstraint constraint, Func<string> getExceptionMessage)
		{
			var capture = new CaptureResultConstraint(constraint);
			Assert.That(code, capture, getExceptionMessage);
			return capture.capturedResult;
		}

		class CaptureResultConstraint(IResolveConstraint parent) : IConstraint
		{
			readonly IResolveConstraint parent = parent;
			IConstraint resolvedParent;
			public ConstraintResult capturedResult;

			public string DisplayName => throw new NotImplementedException();

			public string Description => throw new NotImplementedException();

			public object[] Arguments => throw new NotImplementedException();

			public ConstraintBuilder Builder { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

			ConstraintResult CaptureResult(ConstraintResult result)
			{
				capturedResult = result;
				// If failure result is due to an AssertionException, report that assertion failure directly,
				// and return a dummy "success" constraint to avoid the redundant unexpected AssertionException report.
				if (result.IsSuccess is false && result.ActualValue is AssertionException ex)
				{
					Assert.Fail(ex.Message);
					capturedResult = new ConstraintResult(resolvedParent, null, isSuccess: false); // result returned by above AssertThat
					result = new ConstraintResult(resolvedParent, null, isSuccess: true); // result returned to Assert.That
				}
				return result;
			}

			public ConstraintResult ApplyTo<TActual>(TActual actual) => CaptureResult(resolvedParent.ApplyTo(actual));

			public ConstraintResult ApplyTo<TActual>(ActualValueDelegate<TActual> del) => CaptureResult(resolvedParent.ApplyTo(del));

			public ConstraintResult ApplyTo<TActual>(ref TActual actual) => CaptureResult(resolvedParent.ApplyTo(ref actual));

			public IConstraint Resolve()
			{
				resolvedParent = parent.Resolve();
				return this;
			}
		}

		// Returns the exception Type of a Throws constraint.
		public static Type ThrowsConstraintExceptionType(IConstraint resolvedConstraint)
		{
			switch (resolvedConstraint)
			{
				case ThrowsNothingConstraint _:
					return null;
				case ThrowsExceptionConstraint _:
					return typeof(Exception);
				case ThrowsConstraint _ when resolvedConstraint.Arguments[0] is TypeConstraint typeConstraint:
					return (Type)typeConstraint.Arguments[0];
				default:
					throw new ArgumentException("unrecognized Throws constraint");
			}
		}

		// Run an action in a test isolation context.
		public static void RunInIsolationContext(Action<ITestIsolationContext> action) =>
#if NETCOREAPP
			TestAssemblyLoadContext.RunInIsolationContext(action);
#else
			TestDomainProxy.RunInIsolationContext(action);
#endif

#if NETCOREAPP
		// .NET Core does not support multiple AppDomains, but it does support unloading assemblies via AssemblyLoadContext.
		// Based off sample code in https://docs.microsoft.com/en-us/dotnet/standard/assembly/unloadability
		class TestAssemblyLoadContext : AssemblyLoadContext, ITestIsolationContext
		{
			// Run an action in "isolation" (collectible AssemblyLoadContext that's unloaded afterwards).
			public static void RunInIsolationContext(Action<ITestIsolationContext> action)
			{
				var alcWeakRef = RunInAssemblyLoadContext(action);
				// Ensure test assembly load context is unloaded before ending this test.
				for (var i = 0; alcWeakRef.IsAlive && i < 10; i++)
				{
					GC.Collect();
					GC.WaitForPendingFinalizers();
				}
			}

			// These must be a separate non-inlined method so that the TestAssemblyLoadContext it creates can be Unload()-ed and GC-ed
			// (which is required for the unloading to finish).
			[MethodImpl(MethodImplOptions.NoInlining)]
			static WeakReference RunInAssemblyLoadContext(Action<ITestIsolationContext> action)
			{
				var alc = new TestAssemblyLoadContext();
				var alcWeakRef = new WeakReference(alc, trackResurrection: true);
				action(alc);
				alc.Unload();
				return alcWeakRef;
			}

			public TestAssemblyLoadContext() : base(isCollectible: true) { }

			protected override Assembly Load(AssemblyName name) =>
				// Defer loading of assembly's dependencies to parent (AssemblyLoadContext.Default) assembly load context.
				null;

			public void AssemblyLoad(string name) => _ = LoadFromAssemblyPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name + ".dll"));

			// There's no separate AppDomain, so this is just an alias for callback(arg).
			public void ParentCallback<T>(Action<T> callback, T arg) => callback(arg);
		}
#else
		// For .NET Framework and its multiple AppDomain support, need a MarshalByRefObject, so that for an instance created
		// via appDomain.CreateInstanceAndUnwrap, all calls to that instance's methods are executed in that appDomain.

		class TestDomainProxy(AppDomain parentDomain) : MarshalByRefObject, ITestIsolationContext
		{
			readonly AppDomain parentDomain = parentDomain;

			// Run an action in "isolation" (seperate AppDomain that's unloaded afterwards).
			// This a static method and thus is run in the AppDomain of the caller (the main AppDomain).
			public static void RunInIsolationContext(Action<ITestIsolationContext> action)
			{
				var testDomain = AppDomain.CreateDomain("TestDomain", AppDomain.CurrentDomain.Evidence, new AppDomainSetup
				{
					ApplicationBase = AppDomain.CurrentDomain.SetupInformation.ApplicationBase,
				});
				// There's no simpler way to call a non-parameterless constructor than this monstrosity.
				var proxy = (TestDomainProxy)testDomain.CreateInstanceAndUnwrap(
					typeof(TestDomainProxy).Assembly.FullName, typeof(TestDomainProxy).FullName, default, default, default,
					[AppDomain.CurrentDomain], default, default
#if NET35
					, default // .NET Framework requires obsolete Evidence parameter overload
#endif
					);
				proxy.Run(action);
				AppDomain.Unload(testDomain);
			}

			// Rules for proxy instance methods:
			// Ensure that all loaded Types of the dummy assemblies are never leaked out of the test domain, so:
			// 1) never return loaded Types (or instances of those Types); and
			// 2) always catch exceptions that may contain loaded Types (or instances of those Types) directly.
			// As long as there is no such leakage, AppDomain.Unload will fully unload the domain and all its assemblies.

			void Run(Action<ITestIsolationContext> action) => action(this);

			// Note: Console usage won't work within a non-main domain - that has to be delegated to the main domain via a callback.
			public void ParentCallback<T>(Action<T> action, T arg) => parentDomain.DoCallBack(new ActionTCallback<T>(action, arg).Call);

			// Delegates used for DoCallback must be serializable.
			[Serializable]
			class ActionTCallback<T>(Action<T> action, T arg)
			{
				readonly Action<T> action = action;
				readonly T arg = arg;

				public void Call() => action(arg);
			}

			public void AssemblyLoad(string assemblyName) => _ = Assembly.Load(assemblyName);
		}
#endif
	}

	public class TestLogger
	{
		private static bool loggingSetup;

		[SetUp]
		public void BaseSetUp()
		{
			if (!loggingSetup)
			{
				Logger.MessageReceived += (sender, args) => TestTools.Log($"[{args.LogChannel}]\n{args.Message}");
				Logger.ChannelFilter = Logger.LogChannel.All;
				loggingSetup = true;
			}

			TestTools.Log($"### {TestExecutionContext.CurrentContext.CurrentResult.FullName}", indentLevel: 0);
		}

		[TearDown]
		public void BaseTearDown()
		{
			var result = TestExecutionContext.CurrentContext.CurrentResult;
			TestTools.Log($"--- {result.FullName} => {result.ResultState}", indentLevel: 0);
		}
	}
}
