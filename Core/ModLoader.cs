using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Diagnostics;
using LinFu.AOP.Interfaces;
using LinFu.Reflection;
using Mono.Cecil;
using ScrollsModLoader.Interfaces;
using UnityEngine;
using System.Threading;
using JsonFx.Json;

namespace ScrollsModLoader {

	/*
	 * handels mod loading and debug file logging
	 * this class should only pass invokes through itself to the right mods
	 * 
	 */

	public class ModInterceptor : IInterceptor
	{
		private ModLoader loader;
		private TypeDefinitionCollection types;

		public ModInterceptor(ModLoader loader) {
			this.loader = loader;
			types = AssemblyFactory.GetAssembly (Platform.getGlobalScrollsInstallPath()+"ModLoader/Assembly-CSharp.dll").MainModule.Types;
		}

		public void Unload(List<String> modsToUnload) {
			//unload
			foreach (String id in modsToUnload) {
				loader.unloadMod ((LocalMod)loader.modManager.installedMods.Find (delegate(Item lmod) {
					return ((lmod as LocalMod).id.Equals (id));
				}));
			}
			modsToUnload.Clear ();
		}

		public object Intercept (IInvocationInfo info)
		{
			//list for unloading
			List<String> modsToUnload = new List<String> ();
			String replacement = "";

			//determine replacement
			foreach (String id in loader.modOrder) {
				BaseMod mod = null;
				try {
					mod = loader.modInstances [id];
				} catch {
					continue;
				}
				if (mod != null) {
					MethodDefinition[] requestedHooks = (MethodDefinition[])mod.GetType ().GetMethod ("GetHooks").Invoke (null, new object[] {
						types,
						SharedConstants.getExeVersionInt ()
					});
					if (requestedHooks.Any (item => ((item.Name.Equals (info.TargetMethod.Name)) && (item.DeclaringType.Name.Equals (info.TargetMethod.DeclaringType.Name))))) {
						try {
							if (mod.WantsToReplace (new InvocationInfo(info)))
								replacement = id;
						} catch (Exception ex) {
							Console.WriteLine (ex);
							modsToUnload.Add (id);
						}
					}
				}
			}

			//unload
			Unload (modsToUnload);

			//load beforeinvoke
			foreach (String id in loader.modOrder) {
				if (id.Equals (replacement)) {
					continue;
				}

				BaseMod mod = null;
				try {
					mod = loader.modInstances [id];
				} catch { continue; }
				if (mod != null) {
					MethodDefinition[] requestedHooks = (MethodDefinition[])mod.GetType ().GetMethod ("GetHooks").Invoke (null, new object[] {
						types,
						SharedConstants.getExeVersionInt ()
					});
					if (requestedHooks.Any (item => ((item.Name.Equals (info.TargetMethod.Name)) && (item.DeclaringType.Name.Equals (info.TargetMethod.DeclaringType.Name))))) {
						try {
							mod.BeforeInvoke (new InvocationInfo (info));
						} catch (Exception exp) {
							Console.WriteLine (exp);
							modsToUnload.Add (id);
						}
					}
				}
			}

			//unload
			Unload (modsToUnload);


			//check for patch call
			object ret = null;
			bool patchFound = false;
			foreach (Patch patch in loader.patches) {
				if (patch.patchedMethods ().Any (item => ((item.Name.Equals (info.TargetMethod.Name)) && (item.DeclaringType.Name.Equals (info.TargetMethod.DeclaringType.Name))))){
					try {
						ret = patch.Intercept (info);
						patchFound = true;
					} catch (Exception ex) {
						Console.WriteLine (ex);
					}
				}
			}
			if (!patchFound) {
				if (replacement.Equals(""))
					ret = info.TargetMethod.Invoke (info.Target, info.Arguments);
				else {
					try {
						BaseMod mod = loader.modInstances [replacement];
						mod.ReplaceMethod(new InvocationInfo(info), out ret);
					} catch (Exception exp) {
						Console.WriteLine (exp);
						modsToUnload.Add (replacement);
					}
				}
			}


			//load afterinvoke
			foreach (String id in loader.modOrder) {
				BaseMod mod = null;
				try {
					mod = loader.modInstances [id];
				} catch { continue; }
				if (mod != null) {
					MethodDefinition[] requestedHooks = (MethodDefinition[])mod.GetType ().GetMethod ("GetHooks").Invoke (null, new object[] {
						types,
						SharedConstants.getExeVersionInt ()
					});
					if (requestedHooks.Any (item => ((item.Name.Equals (info.TargetMethod.Name)) && (item.DeclaringType.Name.Equals (info.TargetMethod.DeclaringType.Name))))) {
						try {
							mod.AfterInvoke (new InvocationInfo (info), ref ret);
						} catch (Exception exp) {
							Console.WriteLine (exp);
							modsToUnload.Add (id);
						}
					}
				}
			}

			//unload
			Unload (modsToUnload);

			return ret;
		}
		
	}

	public class SimpleMethodReplacementProvider : IMethodReplacementProvider 
	{
		private ModInterceptor interceptor;
		public SimpleMethodReplacementProvider(ModLoader loader) {
			interceptor = new ModInterceptor (loader);
		}

		public bool CanReplace (object host, IInvocationInfo info)
		{
			StackTrace trace = info.StackTrace;
			foreach (StackFrame frame in trace.GetFrames()) {
				if (frame.GetMethod ().Name.Equals(info.TargetMethod.Name))
					// this replacement disables us to hook rekursive functions, however the default one is broken
					return false;
			}
			return true;
		}

		public IInterceptor GetMethodReplacement (object host, IInvocationInfo info)
		{
			return interceptor;
		}
	}
	

	public class ModLoader
	{
		static bool init = false;
		static ModLoader instance = null;
		private String modLoaderPath;

		public ModManager modManager;
		public List<String> modOrder = new List<String>();

		private bool isRepatchNeeded = false;

		public Dictionary<String, BaseMod> modInstances = new Dictionary<String, BaseMod>();
		public static Dictionary<String, ExceptionLogger> logger = new Dictionary<String, ExceptionLogger> ();
		public List<Patch> patches = new List<Patch>();

		private APIHandler publicAPI = null;

		public ModLoader ()
		{
			modLoaderPath = Platform.getGlobalScrollsInstallPath() + System.IO.Path.DirectorySeparatorChar + "ModLoader" + System.IO.Path.DirectorySeparatorChar;


			//load installed mods
			modManager = new ModManager (this);


			//load order list
			if (!File.Exists (modLoaderPath+"mods.ini")) {
				File.CreateText (modLoaderPath+"mods.ini").Close();
				//first launch, set hooks for patches
				this.queueRepatch();
			}
			modOrder = File.ReadAllLines (modLoaderPath+"mods.ini").ToList();



			//match order with installed mods
			foreach (LocalMod mod in modManager.installedMods) {
				if (mod.enabled)
					if (!modOrder.Contains (mod.localId))
						modOrder.Add (mod.localId);
			}

			//clean up not available mods
			foreach (String id in modOrder.ToArray()) {
				if (modManager.installedMods.Find (delegate(Item mod) {
					return ((mod as LocalMod).localId.Equals (id));
				}) == null)
					modOrder.Remove (id);
			}



			//get Scrolls Types list
			TypeDefinitionCollection types = AssemblyFactory.GetAssembly (modLoaderPath+"Assembly-CSharp.dll").MainModule.Types;

			//get ModAPI
			publicAPI = new APIHandler (this);

			//loadPatches
			this.loadPatches (types);

			//loadModsStatic
			this.loadModsStatic (types);

			//repatch
			this.repatchIfNeeded ();

			Console.WriteLine ("------------------------------");
			Console.WriteLine ("ModLoader Hooks:");
			ScrollsFilter.Log ();
			Console.WriteLine ("------------------------------");

		}

		public void loadPatches(TypeDefinitionCollection types) {

			//get Patches
			patches.Add (new PatchUpdater (types));
			patches.Add (new PatchPopups (types));
			//patches.Add(new PatchOffline(types));

			PatchSettingsMenu settingsMenuHook = new PatchSettingsMenu (types);
			publicAPI.setSceneCallback (settingsMenuHook);
			patches.Add (settingsMenuHook);

			PatchModsMenu modMenuHook = new PatchModsMenu (types, this);
			modMenuHook.Initialize (publicAPI);
			patches.Add (modMenuHook);

			//add Hooks
			addPatchHooks ();
		}

		public void addPatchHooks () {
			foreach (Patch patch in patches) {
				try {
					foreach (MethodDefinition definition in patch.patchedMethods())
						ScrollsFilter.AddHook (definition);
				} catch {}
			}
		}

		public void loadModsStatic(TypeDefinitionCollection types) {
			//get Mods
			foreach (LocalMod mod in modManager.installedMods) {
				if (mod.enabled) {
					if (this.loadModStatic (mod.installPath) == null) {
						modManager.disableMod(mod, false);
						modOrder.Remove (mod.localId);
					}
				}
			}
		}

		public Mod loadModStatic(String filePath) {
			//get Scrolls Types list
			TypeDefinitionCollection types = AssemblyFactory.GetAssembly (modLoaderPath+"Assembly-CSharp.dll").MainModule.Types;
			return this._loadModStatic (types, filePath);
		}

		public void loadModStatic(TypeDefinitionCollection types, String filepath) {
			this._loadModStatic (types, filepath);
		}

		private Mod _loadModStatic(TypeDefinitionCollection types, String filepath) {
			ResolveEventHandler resolver = new ResolveEventHandler(CurrentDomainOnAssemblyResolve);
			AppDomain.CurrentDomain.AssemblyResolve += resolver;

			Assembly modAsm = null;
			try {
				modAsm = Assembly.LoadFile(filepath);
			} catch (BadImageFormatException) {
				AppDomain.CurrentDomain.AssemblyResolve -= resolver;
				return null;
			}
			Type modClass = (from _modClass in modAsm.GetTypes ()
			                 where _modClass.InheritsFrom (typeof(ScrollsModLoader.Interfaces.BaseMod))
			                 select _modClass).First();

			//no mod classes??
			if (modClass == null) {
				AppDomain.CurrentDomain.AssemblyResolve -= resolver;
				return null;
			}

			//get hooks
			MethodDefinition[] hooks = null;
			try {
				hooks =(MethodDefinition[]) modClass.GetMethod ("GetHooks").Invoke (null, new object[] {
					types,
					SharedConstants.getExeVersionInt ()
				});
			} catch  {
				AppDomain.CurrentDomain.AssemblyResolve -= resolver;
				return null;
			}


			TypeDefinition[] typeDefs = new TypeDefinition[types.Count];
			types.CopyTo(typeDefs, 0);

			//check hooks
			foreach (MethodDefinition hook in hooks) {
				//type/method does not exists
				if ((from type in typeDefs
				     where type.Equals(hook.DeclaringType)
				     select type).Count() == 0) {
					//disable mod
					AppDomain.CurrentDomain.AssemblyResolve -= resolver;
					return null;
				}
			}

			//add hooks
			foreach (MethodDefinition hook in hooks) {
				ScrollsFilter.AddHook (hook);
			}

			//mod object for local mods on ModManager
			Mod mod = new Mod();
			try {
				mod.id = "00000000000000000000000000000000";
				mod.name = (String)modClass.GetMethod("GetName").Invoke(null, null);
				mod.version = (int)modClass.GetMethod("GetVersion").Invoke(null, null);
				mod.versionCode = ""+mod.version;
				mod.description = "";
			} catch {
				AppDomain.CurrentDomain.AssemblyResolve -= resolver;
				return null;
			}

			AppDomain.CurrentDomain.AssemblyResolve -= resolver;
			return mod;
		}


		public void loadMods() {

			BaseMod.Initialize (publicAPI);
			foreach (String id in modOrder) {
				LocalMod lmod = (LocalMod)modManager.installedMods.Find (delegate(Item mod) {
					return ((mod as LocalMod).localId.Equals (id));
				});
				if (lmod.enabled)
					this.loadMod(lmod);
			}

		}

		public void loadMod(LocalMod mod) {

			ResolveEventHandler resolver = new ResolveEventHandler(CurrentDomainOnAssemblyResolve);
			AppDomain.CurrentDomain.AssemblyResolve += resolver;

			Assembly modAsm = null;
			try {
				modAsm = Assembly.LoadFile(mod.installPath);
			} catch (BadImageFormatException) {
				AppDomain.CurrentDomain.AssemblyResolve -= resolver;
				return;
			}
			Type modClass = (from _modClass in modAsm.GetTypes ()
			                 where _modClass.InheritsFrom (typeof(BaseMod))
			                 select _modClass).First();


			//no mod classes??
			if (modClass == null) {
				AppDomain.CurrentDomain.AssemblyResolve -= resolver;
				return;
			}

			publicAPI.setCurrentlyLoading (mod);

			int countmods = modInstances.Count;
			int countlog = logger.Count;
			try {
				modInstances.Add(mod.localId, (BaseMod)(modClass.GetConstructor (Type.EmptyTypes).Invoke (new object[0])));
				if (!mod.localInstall)
					logger.Add (mod.localId, new ExceptionLogger (mod, mod.source));
			} catch (Exception exp) {
				Console.WriteLine (exp);
				if (modInstances.Count > countmods)
					modInstances.Remove (mod.localId);
				if (logger.Count > countlog)
					logger.Remove (mod.localId);
				AppDomain.CurrentDomain.AssemblyResolve -= resolver;
				return;
			}

			publicAPI.setCurrentlyLoading (null);

			if (!modOrder.Contains (mod.localId))
				modOrder.Add (mod.localId);

			AppDomain.CurrentDomain.AssemblyResolve -= resolver;
		}

		public void unloadMod(LocalMod mod) {
			modManager.disableMod (mod, true);
			this._unloadMod (mod);
		}
		public void _unloadMod(LocalMod mod) {
			modOrder.Remove (mod.localId);
			modInstances.Remove (mod.localId);
		}

		public void moveModUp(LocalMod mod) {
			int index = modOrder.IndexOf (mod.localId);
			if (index > 0) {
				modOrder.Remove (mod.localId);
				modOrder.Insert (index - 1, mod.localId);
			}
			modManager.sortInstalledMods ();
		}

		public void moveModDown(LocalMod mod) {
			int index = modOrder.IndexOf (mod.localId);
			if (index+1 < modOrder.Count) {
				modOrder.Remove (mod.localId);
				modOrder.Insert (index + 1, mod.localId);
			}
			modManager.sortInstalledMods ();
		}

		public void queueRepatch() {
			isRepatchNeeded = true;
		}

		public void repatchIfNeeded() {
			if (isRepatchNeeded) {
				repatch ();
			}
		}

		public void repatch()
		{
				//save ModList
				File.Delete (modLoaderPath+"mods.ini");
				StreamWriter modOrderWriter = File.CreateText (modLoaderPath+"mods.ini");
				foreach (String modId in modOrder) {
					modOrderWriter.WriteLine (modId);
				}
				modOrderWriter.Flush ();
				modOrderWriter.Close ();

				String installPath = Platform.getGlobalScrollsInstallPath ();
				File.Delete(installPath+"Assembly-CSharp.dll");
				File.Copy(installPath+"ModLoader"+ System.IO.Path.DirectorySeparatorChar +"Assembly-CSharp.dll", installPath+"Assembly-CSharp.dll");

				Patcher patcher = new Patcher ();
				if (!patcher.patchAssembly (Platform.getGlobalScrollsInstallPath ())) {
					//normal patching should never fail at this point
					//because this is no update and we are already patched
					//TO-DO get hook that crashed the patching and deactive mod instead
					//No idea how to do that correctly
					Dialogs.showNotification ("Scrolls is broken", "Your Scrolls install appears to be broken or modified by other tools. Scrolls Summoner failed to load and will de-install itself");
					File.Delete(Platform.getGlobalScrollsInstallPath()+"Assembly-CSharp.dll");
					File.Copy(Platform.getGlobalScrollsInstallPath()+"ModLoader"+ System.IO.Path.DirectorySeparatorChar +"Assembly-CSharp.dll", Platform.getGlobalScrollsInstallPath()+"Assembly-CSharp.dll");
					Application.Quit();
				}

				Platform.RestartGame ();
		}

		private static System.Reflection.Assembly CurrentDomainOnAssemblyResolve(object sender, ResolveEventArgs args)
		{
			var asm = (from a in AppDomain.CurrentDomain.GetAssemblies()
			           where a.GetName().FullName.Equals(args.Name)
			           select a).FirstOrDefault();
			if (asm == null) {
				asm = System.Reflection.Assembly.GetExecutingAssembly();
			}
			return asm;
		}

		public static void UnhandledExceptionHandler(object sender, UnhandledExceptionEventArgs e) {
			if ((e.ExceptionObject as Exception).TargetSite.Module.Assembly.GetName().Name.Equals("UnityEngine")
			    || (e.ExceptionObject as Exception).TargetSite.Module.Assembly.GetName().Name.Equals("Assembly-CSharp")
			    || (e.ExceptionObject as Exception).TargetSite.Module.Assembly.GetName().Name.Equals("ScrollsModLoader")
			    || (e.ExceptionObject as Exception).TargetSite.Module.Assembly.Location.ToLower().Equals(Platform.getGlobalScrollsInstallPath().ToLower())
			    || (e.ExceptionObject as Exception).TargetSite.Module.Assembly.Location.Equals("")) { //no location or Managed => mod loader crash

				//log
				Console.WriteLine (e.ExceptionObject);
				new ExceptionLogger ().logException ((Exception)e.ExceptionObject);

				//unload ScrollsModLoader
				MethodBodyReplacementProviderRegistry.SetProvider (new NoMethodReplacementProvider());

				//check for frequent crashes
				if (!System.IO.File.Exists (Platform.getGlobalScrollsInstallPath () + System.IO.Path.DirectorySeparatorChar + "check.txt")) {
					System.IO.File.CreateText (Platform.getGlobalScrollsInstallPath () + System.IO.Path.DirectorySeparatorChar + "check.txt");
					Platform.RestartGame ();
				} else {
					try {
						foreach (String id in instance.modOrder) {
							BaseMod mod = instance.modInstances [id];
							if (mod != null) {
								try {
									instance.unloadMod((LocalMod)instance.modManager.installedMods.Find (delegate(Item lmod) {
										return ((lmod as LocalMod).id.Equals (id));
									}));
								} catch  (Exception exp) {
									Console.WriteLine (exp);
								}
							}
						}
					} catch  (Exception exp) {
						Console.WriteLine (exp);
					}
					instance.repatch ();
				}

			} else if (instance != null && logger != null && logger.Count > 0) {

				Console.WriteLine (e.ExceptionObject);

				Assembly asm = (e.ExceptionObject as Exception).TargetSite.Module.Assembly;
				Type modClass = (from _modClass in asm.GetTypes ()
				                 where _modClass.InheritsFrom (typeof(BaseMod))
				                 select _modClass).First();

				//no mod classes??
				if (modClass == null) {
					return;
				}

				foreach (String id in instance.modOrder) {
					BaseMod mod = null;
					try {
						mod = instance.modInstances [id];
					} catch  (Exception exp) {
						Console.WriteLine (exp);
					}
					if (mod != null) {
						if (modClass.Equals(mod.GetType())) {
							String folder = Path.GetDirectoryName (asm.Location);
							if (File.Exists (folder + Path.DirectorySeparatorChar + "config.json")) {
								JsonReader reader = new JsonReader ();
								LocalMod lmod = (LocalMod) reader.Read (File.ReadAllText (folder + Path.DirectorySeparatorChar + "config.json"), typeof(LocalMod));
								if (!lmod.localInstall)
									logger [lmod.localId].logException ((Exception)e.ExceptionObject);
							}
						}
					}
				}
			}
		}

		public class NoMethodReplacementProvider : IMethodReplacementProvider 
		{
			public bool CanReplace (object host, IInvocationInfo info)
			{
				return false;
			}

			public IInterceptor GetMethodReplacement (object host, IInvocationInfo info)
			{
				return null;
			}
		}

		//initial game callback
		public static void Init() {

			//wiredly App.Awake() calls Init multiple times, but we do not want multiple instances
			if (init)
				return;
			init = true;

			Console.WriteLine ("ModLoader version: " + ModLoader.getVersion ());

			if (Updater.tryUpdate ()) { //update
				Application.Quit ();
				return;
			}

			//Install global mod exception helper
			AppDomain.CurrentDomain.UnhandledException += UnhandledExceptionHandler;

			instance = new ModLoader();
			MethodBodyReplacementProviderRegistry.SetProvider (new SimpleMethodReplacementProvider(instance));

			//otherwise we can finally load
			instance.loadMods ();

			//delete checks for loading crashes
			if (System.IO.File.Exists (Platform.getGlobalScrollsInstallPath () + System.IO.Path.DirectorySeparatorChar + "check.txt")) {
				System.IO.File.Delete (Platform.getGlobalScrollsInstallPath () + System.IO.Path.DirectorySeparatorChar + "check.txt");
			}
		}

		public static int getVersion() {
			return 4;
		}
	}
}