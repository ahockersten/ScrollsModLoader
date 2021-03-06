using System;
using System.IO;
using ScrollsModLoader.Interfaces;

namespace ScrollsModLoader
{
	public class APIHandler : ModAPI
	{
		private PatchSettingsMenu sceneHandler = null;
		private ModLoader loader;
		private LocalMod currentlyLoading = null;

		public APIHandler (ModLoader loader)
		{
			this.loader = loader;
		}


		public void ShowLogin(Popups popups, IOkStringsCancelCallback callback, string username, string problems, string popupType, string header, string description, string okText) {
			PatchPopups.ShowLogin (popups, callback, username, problems, popupType, header, description, okText);
		}

		public void setSceneCallback(PatchSettingsMenu patch) {
			sceneHandler = patch;
		}

		public bool AddScene (string desc, SceneProvider provider)
		{
			return sceneHandler.AddScene(desc, provider);
		}

		public void LoadScene (string providerDesc)
		{
			sceneHandler.LoadScene (providerDesc);
		}




		public string FileOpenDialog() {
			return Dialogs.fileOpenDialog ();
		}

		public string OwnFolder(BaseMod mod)
		{
			String installpath = null;

			foreach (String id in loader.modInstances.Keys) {
				if (loader.modInstances [id].Equals (mod))
					installpath = (loader.modManager.installedMods.Find (delegate(Item lmod) {
						return ((lmod as LocalMod).localId.Equals (id));
					}) as LocalMod).installPath;
			}

			if (installpath == null && currentlyLoading != null)
				return Path.GetDirectoryName(currentlyLoading.installPath);
			if (installpath == null)
				return Platform.getGlobalScrollsInstallPath () + "ModLoader" + Path.DirectorySeparatorChar + "mods" + Path.DirectorySeparatorChar + "Unknown" + Path.DirectorySeparatorChar;
			return Path.GetDirectoryName(installpath);
		}

		public void setCurrentlyLoading(LocalMod mod) {
			currentlyLoading = mod;
		}
	}
}

