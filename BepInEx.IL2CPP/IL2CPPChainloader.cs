﻿extern alias il2cpp;

using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.IL2CPP.Hook;
using BepInEx.IL2CPP.Logging;
using BepInEx.Logging;
using BepInEx.Preloader.Core;
using BepInEx.Preloader.Core.Logging;
using HarmonyLib.Public.Patching;
using UnhollowerBaseLib.Runtime;
using UnhollowerRuntimeLib;
using IL2CPPUnityEngine = il2cpp::UnityEngine;

namespace BepInEx.IL2CPP
{
	public class IL2CPPChainloader : BaseChainloader<BasePlugin>
	{
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate IntPtr RuntimeInvokeDetourDelegate(IntPtr method, IntPtr obj, IntPtr parameters, IntPtr exc);

		[DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
		private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

		private static RuntimeInvokeDetourDelegate originalInvoke;

		private static FastNativeDetour RuntimeInvokeDetour { get; set; }

		private static IL2CPPChainloader Instance { get; set; }

		public override unsafe void Initialize(string gameExePath = null)
		{
			UnhollowerBaseLib.GeneratedDatabasesUtil.DatabasesLocationOverride = Preloader.IL2CPPUnhollowedPath;
			PatchManager.ResolvePatcher += IL2CPPDetourMethodPatcher.TryResolve;

			base.Initialize(gameExePath);
			Instance = this;

			var version = //Version.Parse(Application.unityVersion);
				Version.Parse(Process.GetCurrentProcess().MainModule.FileVersionInfo.FileVersion);

			UnityVersionHandler.Initialize(version.Major, version.Minor, version.Revision);

			// One or the other here for Unhollower to work correctly

			//ClassInjector.Detour = new DetourHandler();

			ClassInjector.DoHook = (ptr, patchedFunctionPtr) =>
			{
				IntPtr originalFunc = new IntPtr(*(void**)ptr);

				var detour = new FastNativeDetour(originalFunc, patchedFunctionPtr);
				
				detour.Apply();

				*(void**)ptr = (void*)detour.TrampolinePtr;
			};

			var gameAssemblyModule = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().First(x => x.ModuleName.Contains("GameAssembly"));

			var functionPtr = GetProcAddress(gameAssemblyModule.BaseAddress, "il2cpp_runtime_invoke"); //DynDll.GetFunction(gameAssemblyModule.BaseAddress, "il2cpp_runtime_invoke");


			PreloaderLogger.Log.LogDebug($"Runtime invoke pointer: 0x{functionPtr.ToInt64():X}");

			RuntimeInvokeDetour = new FastNativeDetour(functionPtr,
				MonoExtensions.GetFunctionPointerForDelegate(new RuntimeInvokeDetourDelegate(OnInvokeMethod), CallingConvention.Cdecl));

			RuntimeInvokeDetour.Apply();

			originalInvoke = RuntimeInvokeDetour.GenerateTrampoline<RuntimeInvokeDetourDelegate>();

			PreloaderLogger.Log.LogDebug("Runtime invoke patched");
		}



		private static IntPtr OnInvokeMethod(IntPtr method, IntPtr obj, IntPtr parameters, IntPtr exc)
		{
			string methodName = Marshal.PtrToStringAnsi(UnhollowerBaseLib.IL2CPP.il2cpp_method_get_name(method));

			bool unhook = false;

			if (methodName == "Internal_ActiveSceneChanged")
			{
				try
				{
					if (ConfigUnityLogging.Value)
					{
						Logger.Sources.Add(new IL2CPPUnityLogSource());

						IL2CPPUnityEngine.Application.CallLogCallback("Test call after applying unity logging hook", "", IL2CPPUnityEngine.LogType.Assert, true);
					}

					unhook = true;

					Instance.Execute();
				}
				catch (Exception ex)
				{
					Logger.LogFatal("Unable to execute IL2CPP chainloader");
					Logger.LogError(ex);
				}
			}

			var result = originalInvoke(method, obj, parameters, exc);

			if (unhook)
			{
				RuntimeInvokeDetour.Dispose();

				PreloaderLogger.Log.LogDebug("Runtime invoke unpatched");
			}

			return result;
		}

		protected override void InitializeLoggers()
		{
			base.InitializeLoggers();

			if (!ConfigDiskWriteUnityLog.Value)
			{
				DiskLogListener.BlacklistedSources.Add("Unity");
			}

			ChainloaderLogHelper.RewritePreloaderLogs();

			Logger.Sources.Add(new IL2CPPLogSource());
		}

		public override BasePlugin LoadPlugin(PluginInfo pluginInfo, Assembly pluginAssembly)
		{
			var type = pluginAssembly.GetType(pluginInfo.TypeName);

			var pluginInstance = (BasePlugin)Activator.CreateInstance(type);

			pluginInstance.Load();

			return pluginInstance;
		}

		private static readonly ConfigEntry<bool> ConfigUnityLogging = ConfigFile.CoreConfig.Bind(
			"Logging", "UnityLogListening",
			true,
			"Enables showing unity log messages in the BepInEx logging system.");

		private static readonly ConfigEntry<bool> ConfigDiskWriteUnityLog = ConfigFile.CoreConfig.Bind(
			"Logging.Disk", "WriteUnityLog",
			false,
			"Include unity log messages in log file output.");
	}
}